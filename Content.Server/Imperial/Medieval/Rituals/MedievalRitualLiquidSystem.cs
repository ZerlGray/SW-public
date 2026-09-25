using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Content.Server.NPC.Pathfinding;
using Content.Shared.Body.Components;
using Content.Shared.ActionBlocker;
using Content.Server.Body.Systems;
using Content.Server.Fluids.EntitySystems;
using Content.Shared.Chemistry;
using Content.Shared.Chemistry.Components;
using Content.Shared.Chemistry.Components.SolutionManager;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.Chemistry.Reagent;
using Content.Shared.Examine;
using Content.Shared.FixedPoint;
using Content.Shared.Imperial.Medieval.Rituals;
using Content.Shared.Interaction;
using Content.Shared.Mobs.Components;
using Content.Shared.Physics;
using Content.Shared.Popups;
using Content.Shared.Verbs;
using Robust.Shared.Map;
using Robust.Shared.Physics.Components;
using Robust.Shared.Timing;

namespace Content.Server.Imperial.Medieval.Rituals;

[RegisterComponent]
public sealed partial class RitualLivingLiquidComponent : Component
{
    public Solution Contents = new();
    public string Kind = string.Empty;
    public EntityUid Target;
    public TimeSpan Until;
    public TimeSpan NextDose;
    public int Tier;
    public bool Released;
    public readonly Queue<PathPoly> Path = new();
    public Task<PathResultEvent>? PendingPath;
    public CancellationTokenSource? PathCancellation;
    public EntityCoordinates PlannedDestination;
    public TimeSpan NextPathRequest;
}

public sealed class MedievalRitualLiquidSystem : EntitySystem
{
    [Dependency] private readonly SharedSolutionContainerSystem _solutions = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly SharedInteractionSystem _interaction = default!;
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly PuddleSystem _puddles = default!;
    [Dependency] private readonly ReactiveSystem _reactive = default!;
    [Dependency] private readonly BloodstreamSystem _blood = default!;
    [Dependency] private readonly InternalsSystem _internals = default!;
    [Dependency] private readonly SharedPopupSystem _popup = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly SharedRitualMagicSystem _magic = default!;
    [Dependency] private readonly ActionBlockerSystem _blocker = default!;
    [Dependency] private readonly PathfindingSystem _pathfinding = default!;

    public override void Initialize()
    {
        SubscribeLocalEvent<SolutionContainerManagerComponent, AfterInteractEvent>(OnUse, before: new[] { typeof(SolutionTransferSystem) });
        SubscribeLocalEvent<SolutionContainerManagerComponent, GetVerbsEvent<AlternativeVerb>>(OnVerbs);
        SubscribeLocalEvent<RitualLivingLiquidComponent, ComponentShutdown>(OnShutdown);
    }

    public bool CanApplyGift(EntityUid vessel, string kind)
    {
        return kind is "liquidWall" or "liquidLife" or "mist" &&
               _solutions.TryGetDrainableSolution(vessel, out _, out var solution) && solution.Volume >= 5 &&
               !solution.Contents.Any(entry => entry.Reagent.Data?.OfType<RitualLiquidData>().Any(data => data.Until > _timing.CurTime) == true);
    }

    public bool ApplyLiquidGift(EntityUid vessel, string kind, EntityUid owner, int tier, TimeSpan until)
    {
        if (!CanApplyGift(vessel, kind) || !_solutions.TryGetDrainableSolution(vessel, out var soln, out var solution))
            return false;
        foreach (var entry in solution.Contents.ToArray())
        {
            solution.RemoveReagent(entry.Reagent, entry.Quantity);
            var metadata = entry.Reagent.Data?.Where(data => data is not RitualLiquidData).Select(data => data.Clone()).ToList() ?? new();
            metadata.Add(new RitualLiquidData { Kind = kind, Tier = tier, Until = until });
            solution.AddReagent(new ReagentId(entry.Reagent.Prototype, metadata), entry.Quantity);
        }
        _solutions.UpdateChemicals(soln.Value);
        _popup.PopupEntity(Loc.GetString("ritual-liquid-enchanted", ("kind", Loc.GetString("ritual-liquid-" + kind))), vessel, owner);
        return true;
    }

    private IEnumerable<RitualLiquidData> Gifts(Solution solution)
    {
        return solution.Contents.SelectMany(entry => entry.Reagent.Data?.OfType<RitualLiquidData>() ?? Enumerable.Empty<RitualLiquidData>())
            .Where(data => data.Until > _timing.CurTime).Distinct();
    }

    public void AddExamineText(EntityUid vessel, ExaminedEvent args)
    {
        if (!_solutions.TryGetDrainableSolution(vessel, out _, out var solution))
            return;
        foreach (var gift in Gifts(solution))
            args.PushMarkup(Loc.GetString("ritual-liquid-examine", ("kind", Loc.GetString("ritual-liquid-" + gift.Kind))));
    }

    private void OnVerbs(Entity<SolutionContainerManagerComponent> ent, ref GetVerbsEvent<AlternativeVerb> args)
    {
        if (!args.CanAccess || !args.CanInteract || !_solutions.TryGetDrainableSolution(ent.Owner, out _, out var solution))
            return;
        var user = args.User;
        foreach (var gift in Gifts(solution))
        {
            if (gift.Kind == "liquidWall")
                continue; // Aim at the ground with the held vessel.
            var kind = gift.Kind;
            args.Verbs.Add(new AlternativeVerb
            {
                Text = Loc.GetString("ritual-liquid-self", ("kind", Loc.GetString("ritual-liquid-" + kind))),
                Act = () => TryActivate(ent, user, Transform(user).Coordinates, user, kind),
            });
        }
    }

    private void OnUse(Entity<SolutionContainerManagerComponent> ent, ref AfterInteractEvent args)
    {
        if (args.Handled || !_solutions.TryGetDrainableSolution(ent.Owner, out _, out var solution))
            return;
        // Ordinary pouring between containers keeps the reagent metadata and does not discharge it.
        if (args.Target is {} target && (HasComp<RefillableSolutionComponent>(target) || HasComp<DrainableSolutionComponent>(target)))
            return;
        var gift = Gifts(solution).FirstOrDefault();
        if (gift == null)
            return;
        args.Handled = TryActivate(ent, args.User, args.ClickLocation, args.Target, gift.Kind);
    }

    public bool TryActivate(EntityUid vessel, EntityUid user, EntityCoordinates destination, EntityUid? target, string kind)
    {
        if (!_blocker.CanInteract(user, vessel) || _magic.IsSuppressed(user) || _magic.IsSuppressed(vessel) ||
            !_interaction.InRangeUnobstructed(user, vessel) || !_interaction.CanAccess(user, vessel) ||
            !_solutions.TryGetDrainableSolution(vessel, out var soln, out var source) ||
            !_interaction.InRangeUnobstructed(user, destination, range: 6f) ||
            _magic.BlocksMagic(_transform.GetMapCoordinates(user), _transform.ToMapCoordinates(destination)))
            return false;
        var gift = Gifts(source).FirstOrDefault(data => data.Kind == kind);
        if (gift == null)
            return false;
        if (kind != "liquidWall" && (target == null || !HasComp<MobStateComponent>(target.Value)))
            return false;
        if (kind == "liquidWall")
        {
            foreach (var entity in _lookup.GetEntitiesInRange(destination, 0.4f))
            {
                if (HasComp<MobStateComponent>(entity) || TryComp<PhysicsComponent>(entity, out var physics) &&
                    (physics.CollisionLayer & (int) CollisionGroup.Impassable) != 0)
                    return false;
            }
        }
        var available = source.Contents.Where(entry => entry.Reagent.Data?.Contains(gift) == true).Aggregate(FixedPoint2.Zero, (sum, entry) => sum + entry.Quantity);
        if (available < 5)
            return false;
        var amount = FixedPoint2.Min(available, kind == "liquidLife" ? 10 : 20);
        var dose = ExtractGift(source, gift, amount);
        _solutions.UpdateChemicals(soln.Value);
        var prototype = kind switch { "liquidWall" => "MedievalRitualLiquidWall", "liquidLife" => "MedievalRitualLivingDrop", _ => "MedievalRitualFollowingMist" };
        var entityUid = Spawn(prototype, kind == "liquidWall" ? destination : Transform(user).Coordinates);
        var effect = EnsureComp<RitualLivingLiquidComponent>(entityUid);
        effect.Contents = dose;
        effect.Kind = kind;
        effect.Target = target ?? user;
        effect.Tier = gift.Tier;
        effect.Until = TimeSpan.FromTicks(Math.Min(gift.Until.Ticks, (_timing.CurTime + TimeSpan.FromSeconds(kind == "liquidWall" ? 20 * gift.Tier : 30)).Ticks));
        return true;
    }

    /// <summary>Consumes only enchanted volume; plain dilution cannot manufacture extra charges.</summary>
    public static Solution ExtractGift(Solution source, RitualLiquidData gift, FixedPoint2 amount)
    {
        var eligible = new Solution { Temperature = source.Temperature };
        foreach (var entry in source.Contents.ToArray())
        {
            if (entry.Reagent.Data?.Contains(gift) != true)
                continue;
            source.RemoveReagent(entry.Reagent, entry.Quantity);
            eligible.AddReagent(entry);
        }
        var dose = eligible.SplitSolution(FixedPoint2.Min(amount, eligible.Volume));
        foreach (var remainder in eligible.Contents)
            source.AddReagent(remainder);
        var extracted = new Solution { Temperature = source.Temperature };
        foreach (var entry in dose.Contents)
            extracted.AddReagent(new ReagentId(entry.Reagent.Prototype, entry.Reagent.Data?.Where(data => data is not RitualLiquidData).ToList()), entry.Quantity);
        return extracted;
    }

    public override void Update(float frameTime)
    {
        var query = EntityQueryEnumerator<RitualLivingLiquidComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var effect, out var xform))
        {
            if (effect.Until <= _timing.CurTime || effect.Contents.Volume <= 0 || _magic.IsSuppressed(uid))
            {
                Release(uid, effect);
                QueueDel(uid);
                continue;
            }
            if (effect.Kind == "liquidWall")
                continue;
            if (Deleted(effect.Target) || Transform(effect.Target).MapID != xform.MapID)
            {
                Release(uid, effect);
                QueueDel(uid);
                continue;
            }
            FollowTarget(uid, effect, xform, frameTime);
            if (effect.Kind == "liquidLife")
            {
                var targetPosition = _transform.GetMapCoordinates(effect.Target);
                if ((_transform.GetWorldPosition(uid) - targetPosition.Position).LengthSquared() > 0.36f ||
                    !CanTravel(uid, effect.Target, targetPosition))
                    continue;
                if (_solutions.TryGetInjectableSolution(effect.Target, out var injectable, out var targetSolution))
                {
                    var dose = effect.Contents.SplitSolution(FixedPoint2.Min(effect.Contents.Volume, targetSolution.AvailableVolume));
                    _reactive.DoEntityReaction(effect.Target, dose, ReactionMethod.Injection);
                    _solutions.Inject(effect.Target, injectable.Value, dose);
                }
                Release(uid, effect);
                QueueDel(uid);
                continue;
            }
            if (_timing.CurTime < effect.NextDose)
                continue;
            effect.NextDose = _timing.CurTime + TimeSpan.FromSeconds(1);
            foreach (var nearby in _lookup.GetEntitiesInRange<BloodstreamComponent>(xform.Coordinates, 1.5f))
            {
                if (effect.Contents.Volume <= 0 || !_interaction.InRangeUnobstructed(uid, nearby.Owner, range: 2))
                    continue;
                var dose = effect.Contents.SplitSolution(FixedPoint2.Min(1, effect.Contents.Volume));
                _reactive.DoEntityReaction(nearby, dose, ReactionMethod.Touch);
                if (!_internals.AreInternalsWorking(nearby))
                    _reactive.DoEntityReaction(nearby, dose, ReactionMethod.Ingestion);
                if (_internals.AreInternalsWorking(nearby) || !_blood.TryAddToChemicals((nearby.Owner, nearby.Comp), dose))
                    _puddles.TrySpillAt(xform.Coordinates, dose, out _, false);
            }
        }
    }

    private bool CanTravel(EntityUid uid, EntityUid target, MapCoordinates destination)
    {
        var current = _transform.GetMapCoordinates(uid);
        return _interaction.InRangeUnobstructed(current, destination, 12f, CollisionGroup.MobMask,
                   predicate: entity => entity == uid || entity == target) &&
               !_magic.BlocksMagicPassage(current, destination);
    }

    private void FollowTarget(EntityUid uid, RitualLivingLiquidComponent effect, TransformComponent xform, float frameTime)
    {
        var target = _transform.GetMapCoordinates(effect.Target);
        var current = _transform.GetMapCoordinates(uid);
        var offset = target.Position - current.Position;
        if (offset.LengthSquared() <= 0.16f || offset.LengthSquared() > 144f) return;
        var destination = target;
        if (CanTravel(uid, effect.Target, target))
        {
            effect.Path.Clear();
            ClearPathRequest(effect);
        }
        else
        {
            if (effect.PendingPath is { IsCompleted: true } pending)
            {
                // This task has finished; IsCompletedSuccessfully also excludes cancellation and faults.
#pragma warning disable RA0004
                if (pending.IsCompletedSuccessfully && pending.Result.Result == PathResult.Path)
                {
                    effect.Path.Clear();
                    foreach (var node in pending.Result.Path) effect.Path.Enqueue(node);
                }
#pragma warning restore RA0004
                // Observe a failed request without allowing a stale task to mutate a deleted liquid.
                _ = pending.Exception;
                ClearPathRequest(effect);
            }
            var targetCoordinates = Transform(effect.Target).Coordinates;
            var targetMoved = !effect.PlannedDestination.TryDistance(EntityManager, targetCoordinates, out var moved) || moved > 1f;
            if (effect.PendingPath == null && _timing.CurTime >= effect.NextPathRequest &&
                (effect.Path.Count == 0 || targetMoved))
            {
                effect.NextPathRequest = _timing.CurTime + TimeSpan.FromSeconds(1);
                effect.PlannedDestination = targetCoordinates;
                effect.PathCancellation = new CancellationTokenSource();
                // The native navmesh handles walls and furniture. No interaction/prying flags: closed doors stay closed.
                effect.PendingPath = _pathfinding.GetPath(xform.Coordinates, targetCoordinates, 0.35f,
                    0, (int) CollisionGroup.MobMask, effect.PathCancellation.Token, PathFlags.None);
            }
            while (effect.Path.TryPeek(out var node))
            {
                if (!node.IsValid()) { effect.Path.Clear(); break; }
                var waypoint = _transform.ToMapCoordinates(node.Coordinates);
                if (waypoint.MapId != current.MapId) { effect.Path.Clear(); break; }
                if ((waypoint.Position - current.Position).LengthSquared() > 0.04f) break;
                effect.Path.Dequeue();
            }
            if (!effect.Path.TryPeek(out var next)) return;
            destination = _transform.ToMapCoordinates(next.Coordinates);
        }

        var direction = destination.Position - current.Position;
        if (direction.LengthSquared() <= 0.0001f) return;
        var step = new MapCoordinates(current.Position + Vector2.Normalize(direction) * MathF.Min(direction.Length(), frameTime * 3), current.MapId);
        // Recheck the actual segment: a door may have closed since the path was built.
        if (!CanTravel(uid, effect.Target, step))
        {
            effect.Path.Clear();
            return;
        }
        _transform.SetWorldPosition(uid, step.Position);
    }

    private static void ClearPathRequest(RitualLivingLiquidComponent effect)
    {
        effect.PathCancellation?.Cancel();
        effect.PathCancellation?.Dispose();
        effect.PathCancellation = null;
        effect.PendingPath = null;
    }

    private void Release(EntityUid uid, RitualLivingLiquidComponent effect)
    {
        if (effect.Released)
            return;
        effect.Released = true;
        ClearPathRequest(effect);
        effect.Path.Clear();
        if (effect.Contents.Volume > 0)
            _puddles.TrySpillAt(Transform(uid).Coordinates, effect.Contents, out _, false);
        effect.Contents = new();
    }

    private void OnShutdown(Entity<RitualLivingLiquidComponent> ent, ref ComponentShutdown args) => Release(ent, ent.Comp);
}
