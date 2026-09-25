using System.Linq;
using System.Numerics;
using Content.Server.MagicBarrier.Components;
using Content.Shared.Actions;
using Content.Shared.Doors.Components;
using Content.Shared.Examine;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Imperial.Medieval.Rituals;
using Content.Shared.Interaction;
using Content.Shared.Item;
using Content.Shared.Mobs.Components;
using Content.Shared.Popups;
using Content.Shared.Projectiles;
using Content.Shared.Throwing;
using Content.Shared.Verbs;
using Content.Shared.Weapons.Melee;
using Robust.Shared.Containers;
using Robust.Shared.Map;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Timing;

namespace Content.Server.Imperial.Medieval.Rituals;

/// <summary>Offerings select phenomena; recipients do not need spells, mana or the book.</summary>
public sealed class MedievalMagnusSystem : EntitySystem
{
    [Dependency] private readonly IGameTiming _time = default!;
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly SharedRitualMagicSystem _magic = default!;
    [Dependency] private readonly SharedHandsSystem _hands = default!;
    [Dependency] private readonly SharedContainerSystem _containers = default!;
    [Dependency] private readonly SharedProjectileSystem _projectiles = default!;
    [Dependency] private readonly SharedInteractionSystem _interaction = default!;
    [Dependency] private readonly SharedPopupSystem _popup = default!;
    [Dependency] private readonly SharedPhysicsSystem _physics = default!;
    [Dependency] private readonly OccluderSystem _occluders = default!;
    [Dependency] private readonly ThrowingSystem _throwing = default!;
    [Dependency] private readonly SharedActionsSystem _actions = default!;
    [Dependency] private readonly MedievalRitualLiquidSystem _liquids = default!;
    [Dependency] private readonly MedievalRitualAnimatedSystem _animated = default!;
    private TimeSpan _nextUpdate;
    public static readonly string[] GiftKinds = { "recall", "vessels", "animation", "phase", "liquidWall", "liquidLife", "mist", "thunder" };

    public override void Initialize()
    {
        SubscribeLocalEvent<MedievalRitualValidateEvent>(Validate);
        SubscribeLocalEvent<MedievalRitualExecuteEvent>(Execute);
        SubscribeLocalEvent<MagnusRecallComponent, GetVerbsEvent<AlternativeVerb>>(OnBind);
        SubscribeLocalEvent<MobStateComponent, GetVerbsEvent<AlternativeVerb>>(OnRecallVerbs);
        SubscribeLocalEvent<MagnusThunderComponent, GetItemActionsEvent>(OnThunderActions);
        SubscribeLocalEvent<MagnusThunderComponent, MagnusThunderActionEvent>(OnThunder);
        SubscribeLocalEvent<MagnusRecallComponent, ExaminedEvent>((EntityUid uid, MagnusRecallComponent c, ExaminedEvent e) => GiftExamine(e, "recall", c.Charges, c.Until));
        SubscribeLocalEvent<MagnusThunderComponent, ExaminedEvent>((EntityUid uid, MagnusThunderComponent c, ExaminedEvent e) => GiftExamine(e, "thunder", c.Charges, c.Until));
        SubscribeLocalEvent<MagnusMagicSourceComponent, ExaminedEvent>(OnSourceExamine);
    }

    private void Validate(MedievalRitualValidateEvent args)
    {
        var context = args.Context;
        var effect = context.Ritual.Effect;
        if (!effect.StartsWith("Magnus", StringComparison.Ordinal)) return;
        args.Handled = true;
        if (effect.StartsWith("MagnusAntimagic", StringComparison.Ordinal))
        {
            if (context.Ritual.Tier == 4 && !context.Participants.Contains(context.Target))
                args.Error = "medieval-magnus-need-participant";
            return;
        }
        var kinds = context.Offerings.Where(HasComp<RitualOfferingComponent>)
            .Select(uid => Comp<RitualOfferingComponent>(uid).Kind).Where(GiftKinds.Contains).ToHashSet();
        if (kinds.Count == 0 || context.Ritual.Tier < 4 && kinds.Count != 1 ||
            kinds.Count(k => k is "liquidWall" or "liquidLife" or "mist") > 1 ||
            kinds.Contains("phase") && kinds.Contains("animation"))
        { args.Error = "medieval-magnus-incompatible-offerings"; return; }
        if (context.Ritual.Tier == 2 && !CanApply(context.Target, kinds.Single()))
        { args.Error = "medieval-magnus-need-target"; return; }
        context.Data["magnusKinds"] = kinds;
    }

    private void Execute(MedievalRitualExecuteEvent args)
    {
        var context = args.Context;
        var tier = context.Ritual.Tier;
        if (context.Ritual.Effect.StartsWith("MagnusAntimagic", StringComparison.Ordinal))
        {
            var fieldUid = tier == 4 ? context.Target : Spawn("MedievalMagnusField", Transform(context.Center).Coordinates);
            var field = EnsureComp<MedievalAntimagicFieldComponent>(fieldUid);
            field.Radius = tier == 3 ? 5 : 3;
            field.Until = _time.CurTime + TimeSpan.FromSeconds(tier == 2 ? 60 : tier == 3 ? 120 : 75);
            field.BlockPassage = tier >= 3;
            Dirty(fieldUid, field);
            return;
        }
        if (!context.Data.TryGetValue("magnusKinds", out var data) || data is not HashSet<string> kinds) return;
        if (tier == 2)
        {
            Apply(context.Target, kinds.Single(), context.Caster, tier, _time.CurTime + TimeSpan.FromMinutes(5));
            return;
        }
        var source = Spawn("MedievalMagnusSource", Transform(context.Center).Coordinates);
        var component = EnsureComp<MagnusMagicSourceComponent>(source);
        component.Caster = context.Caster;
        component.Kinds = kinds;
        component.Tier = tier;
        component.Radius = context.Ritual.Radius;
        component.Until = _time.CurTime + TimeSpan.FromMinutes(tier == 3 ? 3 : 15);
        component.RemainingGifts = tier == 3 ? 32 : 128;
        Pulse(source, component);
    }

    private bool CanApply(EntityUid uid, string kind)
    {
        if (!Exists(uid) || HasComp<RitualTransportProtectedComponent>(uid) || HasComp<MagicBarrierComponent>(uid)) return false;
        return kind switch
        {
            "recall" => HasComp<ItemComponent>(uid) && !Transform(uid).Anchored && !HasComp<MobStateComponent>(uid),
            "thunder" => HasComp<ItemComponent>(uid) && HasComp<MeleeWeaponComponent>(uid),
            "phase" => IsOrdinaryBarrier(uid),
            "animation" or "vessels" => _animated.CanApplyGift(uid, kind),
            "liquidWall" or "liquidLife" or "mist" => _liquids.CanApplyGift(uid, kind),
            _ => false,
        };
    }

    private bool IsOrdinaryBarrier(EntityUid uid)
    {
        if (!Transform(uid).Anchored || !HasComp<PhysicsComponent>(uid)) return false;
        var id = MetaData(uid).EntityPrototype?.ID ?? "";
        if (id.Contains("Rock", StringComparison.OrdinalIgnoreCase) || id.Contains("Magic", StringComparison.OrdinalIgnoreCase) ||
            id.Contains("Barrier", StringComparison.OrdinalIgnoreCase) || id.Contains("Floor", StringComparison.OrdinalIgnoreCase) ||
            id.Contains("Unbreakable", StringComparison.OrdinalIgnoreCase)) return false;
        return id.Contains("Wall", StringComparison.OrdinalIgnoreCase) || id.Contains("Door", StringComparison.OrdinalIgnoreCase) || id.Contains("Window", StringComparison.OrdinalIgnoreCase);
    }

    private void Apply(EntityUid uid, string kind, EntityUid owner, int tier, TimeSpan until)
    {
        switch (kind)
        {
            case "recall":
                if (HasComp<MagnusRecallComponent>(uid)) break;
                var recall = EnsureComp<MagnusRecallComponent>(uid);
                recall.Master = tier == 2 ? owner : null;
                recall.Until = until;
                recall.Charges = tier + 1;
                break;
            case "thunder":
                if (HasComp<MagnusThunderComponent>(uid)) break;
                var thunder = EnsureComp<MagnusThunderComponent>(uid);
                thunder.Until = until;
                thunder.Charges = tier + 1;
                // Items already held do not receive a fresh equipment event.
                if (_containers.TryGetContainingContainer((uid, null, null), out var held) &&
                    _hands.IsHolding(held.Owner, uid, out _))
                    _actions.AddAction(held.Owner, ref thunder.Action, "ActionMagnusThunder", uid);
                break;
            case "phase":
                if (!TryComp<MagnusPhasedComponent>(uid, out var phase))
                {
                    phase = AddComp<MagnusPhasedComponent>(uid);
                    phase.OriginalCollision = Comp<PhysicsComponent>(uid).CanCollide;
                    phase.OriginalOcclusion = TryComp<OccluderComponent>(uid, out var occlusion) && occlusion.Enabled;
                }
                if (phase.Until < until) phase.Until = until;
                SetPhased(uid, phase, true);
                break;
            case "animation": case "vessels": _animated.ApplyGift(uid, kind, owner, tier, until); break;
            default: _liquids.ApplyLiquidGift(uid, kind, owner, tier, until); break;
        }
    }

    private void Pulse(EntityUid uid, MagnusMagicSourceComponent source)
    {
        if (_magic.IsSuppressed(uid)) return;
        foreach (var target in _lookup.GetEntitiesInRange(_transform.GetMapCoordinates(uid), source.Radius, LookupFlags.All).OrderBy(e => e.Id))
        {
            if (_magic.IsSuppressed(target)) continue;
            foreach (var kind in source.Kinds)
            {
                if (!CanApply(target, kind)) continue;
                if (kind == "phase")
                {
                    // Spatial phenomena last only while their source supports this location.
                    Apply(target, kind, source.Caster, source.Tier, _time.CurTime + TimeSpan.FromSeconds(2));
                    continue;
                }
                if (source.RemainingGifts <= 0 || !source.Granted.Add((target, kind))) continue;
                Apply(target, kind, source.Caster, source.Tier, kind == "animation" ? source.Until : _time.CurTime + TimeSpan.FromMinutes(3));
                source.RemainingGifts--;
            }
        }
    }

    private void OnBind(EntityUid uid, MagnusRecallComponent component, GetVerbsEvent<AlternativeVerb> args)
    {
        if (!args.CanAccess || !args.CanInteract || !_hands.IsHolding(args.User, uid, out _)) return;
        args.Verbs.Add(new AlternativeVerb { Text = Loc.GetString("medieval-magnus-bind"), Act = () =>
        {
            if (Exists(uid) && _hands.IsHolding(args.User, uid, out _) && !_magic.IsSuppressed(args.User)) component.Master = args.User;
        }});
    }

    private void OnRecallVerbs(EntityUid uid, MobStateComponent component, GetVerbsEvent<AlternativeVerb> args)
    {
        if (args.User != uid || !args.CanInteract) return;
        var query = EntityQueryEnumerator<MagnusRecallComponent>();
        while (query.MoveNext(out var item, out var recall))
        {
            if (recall.Master != uid || recall.Until <= _time.CurTime || recall.Charges <= 0) continue;
            var selected = item;
            args.Verbs.Add(new AlternativeVerb { Text = Loc.GetString("medieval-magnus-recall", ("item", Name(item))), Act = () => TryRecall(uid, selected) });
        }
    }

    public bool TryRecall(EntityUid user, EntityUid item)
    {
        if (!TryComp<MagnusRecallComponent>(item, out var recall) || recall.Master != user || recall.Charges <= 0 ||
            recall.Until <= _time.CurTime || !_hands.TryGetEmptyHand(user, out _) || Transform(item).Anchored || _magic.IsSuppressed(user) ||
            _magic.BlocksMagic(_transform.GetMapCoordinates(item), _transform.GetMapCoordinates(user))) return false;
        // A weapon held by somebody else is not a free remote disarm.
        if (_containers.TryGetContainingContainer((item, null, null), out var container) && container.Owner != user) return false;
        if (TryComp<EmbeddableProjectileComponent>(item, out var embedded) && embedded.EmbeddedIntoUid != null)
        {
            if (embedded.DeleteOnRemove) return false;
            _projectiles.EmbedDetach(item, embedded, user);
        }
        if (!_hands.TryPickupAnyHand(user, item, checkActionBlocker: true, animateUser: false)) return false;
        recall.Charges--;
        return true;
    }

    private void OnThunderActions(EntityUid uid, MagnusThunderComponent comp, GetItemActionsEvent args) =>
        args.AddAction(ref comp.Action, "ActionMagnusThunder");

    private void OnThunder(EntityUid uid, MagnusThunderComponent comp, MagnusThunderActionEvent args)
    {
        if (args.Handled || comp.Until <= _time.CurTime || comp.Charges <= 0 || _hands.GetActiveItem(args.Performer) != uid ||
            !_interaction.InRangeUnobstructed(args.Performer, args.Target, range: 2) || _magic.BlocksCast(args.Performer, args.Target)) return;
        var point = _transform.ToMapCoordinates(args.Target);
        comp.Charges--;
        args.Handled = true;
        Spawn("MedievalMagnusThunderWave", point);
        foreach (var target in _lookup.GetEntitiesInRange(point, 3))
        {
            if (target == args.Performer || Transform(target).Anchored || !HasComp<PhysicsComponent>(target)) continue;
            var pos = _transform.GetMapCoordinates(target);
            if (_magic.BlocksMagic(point, pos) || !_interaction.InRangeUnobstructed(point, target, 3)) continue;
            var delta = pos.Position - point.Position;
            if (delta.LengthSquared() < .01f) delta = Vector2.UnitX;
            _throwing.TryThrow(target, Vector2.Normalize(delta) * 2, 5, args.Performer, animated: false, doSpin: false);
        }
    }

    private void SetPhased(EntityUid uid, MagnusPhasedComponent comp, bool active)
    {
        comp.Active = active;
        var collision = TryComp<DoorComponent>(uid, out var door) ? door.State != DoorState.Open : comp.OriginalCollision;
        _physics.SetCanCollide(uid, !active && collision);
        if (TryComp<OccluderComponent>(uid, out var occluder))
            _occluders.SetEnabled(uid, !active && (door != null ? door.Occludes && collision : comp.OriginalOcclusion), occluder);
        Dirty(uid, comp);
    }

    private void GiftExamine(ExaminedEvent args, string kind, int charges, TimeSpan until) =>
        args.PushMarkup(Loc.GetString("medieval-magnus-gift-examine", ("kind", Loc.GetString("medieval-offering-" + kind)),
            ("charges", charges), ("seconds", Math.Max(0, (int)(until - _time.CurTime).TotalSeconds))));

    private void OnSourceExamine(EntityUid uid, MagnusMagicSourceComponent source, ExaminedEvent args) =>
        args.PushMarkup(Loc.GetString("medieval-magnus-source-examine", ("kinds", string.Join(", ", source.Kinds.Select(k => Loc.GetString("medieval-offering-" + k)))),
            ("radius", source.Radius), ("seconds", Math.Max(0, (int)(source.Until - _time.CurTime).TotalSeconds)), ("gifts", source.RemainingGifts)));

    public override void Update(float frameTime)
    {
        if (_time.CurTime < _nextUpdate) return;
        _nextUpdate = _time.CurTime + TimeSpan.FromSeconds(.25);
        var recalls = EntityQueryEnumerator<MagnusRecallComponent>();
        while (recalls.MoveNext(out var uid, out var recall))
            if (recall.Until <= _time.CurTime) RemCompDeferred<MagnusRecallComponent>(uid);
        var thunders = EntityQueryEnumerator<MagnusThunderComponent>();
        while (thunders.MoveNext(out var uid, out var thunder))
            if (thunder.Until <= _time.CurTime)
            {
                if (thunder.Action is {} action) QueueDel(action);
                RemCompDeferred<MagnusThunderComponent>(uid);
            }
        var fields = EntityQueryEnumerator<MedievalAntimagicFieldComponent>();
        while (fields.MoveNext(out var uid, out var field))
        {
            if (field.Until > _time.CurTime) continue;
            RemCompDeferred<MedievalAntimagicFieldComponent>(uid);
            if (!HasComp<MobStateComponent>(uid)) QueueDel(uid);
        }
        var sources = EntityQueryEnumerator<MagnusMagicSourceComponent>();
        while (sources.MoveNext(out var uid, out var source))
        {
            if (source.Until <= _time.CurTime) { QueueDel(uid); continue; }
            if (source.NextPulse > _time.CurTime) continue;
            source.NextPulse = _time.CurTime + TimeSpan.FromSeconds(1);
            Pulse(uid, source);
        }
        var phases = EntityQueryEnumerator<MagnusPhasedComponent>();
        while (phases.MoveNext(out var uid, out var phase))
        {
            var shouldPhase = phase.Until > _time.CurTime && !_magic.IsSuppressed(uid);
            if (!shouldPhase && _lookup.GetEntitiesInRange(_transform.GetMapCoordinates(uid), .8f)
                    .Any(e => e != uid && HasComp<MobStateComponent>(e))) continue;
            if (phase.Active != shouldPhase || shouldPhase && Comp<PhysicsComponent>(uid).CanCollide) SetPhased(uid, phase, shouldPhase);
            if (phase.Until <= _time.CurTime) RemCompDeferred<MagnusPhasedComponent>(uid);
        }
    }
}

[RegisterComponent]
public sealed partial class MagnusRecallComponent : Component
{
    public EntityUid? Master;
    public TimeSpan Until;
    public int Charges;
}
[RegisterComponent]
public sealed partial class MagnusThunderComponent : Component
{
    public TimeSpan Until;
    public int Charges;
    public EntityUid? Action;
}
[RegisterComponent]
public sealed partial class MagnusMagicSourceComponent : Component
{
    public EntityUid Caster;
    public HashSet<string> Kinds = new();
    public HashSet<(EntityUid, string)> Granted = new();
    public float Radius;
    public int Tier;
    public int RemainingGifts;
    public TimeSpan Until;
    public TimeSpan NextPulse;
}
