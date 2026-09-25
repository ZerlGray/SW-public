using System.Linq;
using Content.Server.Imperial.Medieval.Knowledge;
using Content.Shared.Actions;
using Content.Shared.Actions.Components;
using Content.Shared.Chemistry.Components;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.FixedPoint;
using Content.Shared.Item;
using Content.Shared.DoAfter;
using Content.Shared.Examine;
using Content.Shared.Imperial.Medieval.Rituals;
using Content.Shared.Interaction;
using Content.Shared.Mobs.Systems;
using Content.Shared.Popups;
using Content.Shared.Stacks;
using Content.Shared.Verbs;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;

namespace Content.Server.Imperial.Medieval.Rituals;

/// <summary>Independent local ritual attempts. Reserves offerings, never charges on cancellation.</summary>
public sealed partial class MedievalRitualSystem : EntitySystem
{
    [Dependency] private readonly IPrototypeManager _prototypes = default!;
    [Dependency] private readonly MedievalKnowledgeSystem _knowledge = default!;
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly SharedDoAfterSystem _doAfter = default!;
    [Dependency] private readonly SharedPopupSystem _popup = default!;
    [Dependency] private readonly SharedStackSystem _stacks = default!;
    [Dependency] private readonly MobStateSystem _mobs = default!;
    [Dependency] private readonly SharedSolutionContainerSystem _solutions = default!;
    [Dependency] private readonly MedievalOfferingPreparationSystem _offeringPreparation = default!;
    [Dependency] private readonly SharedInteractionSystem _interaction = default!;

    public override void Initialize()
    {
        SubscribeLocalEvent<ActionsComponent, MedievalDrawRitualActionEvent>(OnDraw);
        SubscribeLocalEvent<MedievalRitualCenterComponent, GetVerbsEvent<AlternativeVerb>>(OnVerbs);
        SubscribeLocalEvent<MedievalRitualCenterComponent, ExaminedEvent>(OnExamine);
        SubscribeLocalEvent<MedievalRitualCenterComponent, MedievalPrayerDoAfterEvent>(OnPrayer);
        SubscribeLocalEvent<MedievalRitualCenterComponent, ComponentShutdown>(OnShutdown);
        // Target selection includes objects, plants, people and the mobile Zaygo token.
        SubscribeLocalEvent<TransformComponent, GetVerbsEvent<Verb>>(OnTargetVerbs);
    }

    private void OnDraw(EntityUid uid, ActionsComponent component, MedievalDrawRitualActionEvent args)
    {
        if (args.Handled || !_prototypes.EnumeratePrototypes<MedievalRitualPrototype>().Any(p => _knowledge.HasKnowledge(uid, p.Knowledge)))
            return;
        var position = args.Target;
        if (!Transform(uid).Coordinates.TryDistance(EntityManager, position, out var distance) || distance > 2)
            return;
        var center = Spawn("MedievalRitualCenter", position);
        var ritual = Comp<MedievalRitualCenterComponent>(center);
        ritual.Owner = uid;
        ritual.Target = uid;
        ritual.Participants.Add(uid);
        Dirty(center, ritual);
        args.Handled = true;
        _popup.PopupEntity(Loc.GetString("medieval-ritual-drawn"), center, uid);
    }

    private void OnExamine(EntityUid uid, MedievalRitualCenterComponent comp, ExaminedEvent args)
    {
        args.PushMarkup(Loc.GetString("medieval-ritual-examine", ("count", comp.Participants.Count)));
        if (comp.Target is { } target && Exists(target))
            args.PushMarkup(Loc.GetString("medieval-ritual-target", ("target", Name(target))));
    }

    private void OnVerbs(EntityUid uid, MedievalRitualCenterComponent comp, GetVerbsEvent<AlternativeVerb> args)
    {
        var user = args.User;
        var remoteTheft = comp.Owner == user && _knowledge.HasKnowledge(user, "RitualZaygoPlace4") &&
            _prototypes.TryIndex<MedievalRitualPrototype>("ZaygoPlace4", out var theft) && CanLead(user, uid, theft);
        if (!args.CanInteract || !args.CanAccess && !remoteTheft)
            return;
        if (args.CanAccess)
            args.Verbs.Add(new AlternativeVerb
            {
                Text = Loc.GetString(comp.Participants.Contains(user) ? "medieval-ritual-leave" : "medieval-ritual-join"),
                Act = () =>
                {
                    if (!Near(user, uid, 3) || _mobs.IsIncapacitated(user)) return;
                    if (!comp.Participants.Remove(user)) comp.Participants.Add(user);
                }
            });
        if (comp.Owner != user)
            return;

        if (args.CanAccess)
            args.Verbs.Add(new AlternativeVerb
            {
                Text = Loc.GetString("medieval-ritual-erase"),
                Act = () => { if (Near(user, uid, 3)) QueueDel(uid); }
            });
        foreach (var ritual in _prototypes.EnumeratePrototypes<MedievalRitualPrototype>())
        {
            if (!_knowledge.HasKnowledge(user, ritual.Knowledge) || !args.CanAccess && ritual.Effect != "ZaygoPlace4") continue;
            var selected = ritual;
            var recipe = string.Join(", ", ritual.Offerings.Select(o => $"{o.Value} × {(_prototypes.TryIndex<EntityPrototype>(o.Key, out var item) ? item.Name : o.Key)}")
                .Concat(ritual.Reagents.Select(o => $"{o.Value} u {o.Key}")));
            var recipeText = Loc.GetString("medieval-ritual-recipe", ("seconds", ritual.Duration), ("people", ritual.Participants), ("radius", ritual.Radius), ("items", recipe))
                + "\n" + Loc.GetString(ritual.Description) + "\n" + DescribeChoices(uid, comp, ritual);
            args.Verbs.Add(new AlternativeVerb
            {
                Text = Loc.GetString(ritual.Name),
                Message = recipeText,
                Act = () => Start(uid, comp, user, selected)
            });
            args.Verbs.Add(new AlternativeVerb
            {
                Text = Loc.GetString("medieval-ritual-preview", ("name", Loc.GetString(ritual.Name))),
                Message = recipeText,
                Act = () =>
                {
                    if (!CanLead(user, uid, selected) || !_knowledge.HasKnowledge(user, selected.Knowledge) || HasComp<MedievalRitualAttemptComponent>(uid)) return;
                    comp.PreviewRadius = selected.Radius;
                    comp.PreviewTheft = selected.Effect == "ZaygoPlace4";
                    Dirty(uid, comp);
                    _popup.PopupEntity(DescribeChoices(uid, comp, selected), uid, user);
                }
            });
        }
        if (!args.CanAccess) return;
        var centers = EntityQueryEnumerator<MedievalRitualCenterComponent>();
        while (centers.MoveNext(out var other, out var otherComp))
        {
            if (other == uid || otherComp.Owner != user) continue;
            var destination = other;
            args.Verbs.Add(new AlternativeVerb
            {
                Text = Loc.GetString("medieval-ritual-link", ("position", _transform.GetMapCoordinates(other).Position)),
                Act = () => { if (Near(user, uid, 3) && Exists(destination)) comp.LinkedCenter = destination; }
            });
        }
    }

    private void OnTargetVerbs(EntityUid uid, TransformComponent comp, GetVerbsEvent<Verb> args)
    {
        if (!args.CanAccess || !args.CanInteract) return;
        var user = args.User;
        foreach (var center in _lookup.GetEntitiesInRange<MedievalRitualCenterComponent>(_transform.GetMapCoordinates(user), 5))
        {
            if (center.Comp.Owner != user || HasComp<MedievalRitualAttemptComponent>(center)) continue;
            var selected = center;
            args.Verbs.Add(new Verb
            {
                Text = Loc.GetString("medieval-ritual-select-target"),
                Act = () => { if (Near(user, uid, 3) && Near(user, selected, 5)) {selected.Comp.Target = uid; Dirty(selected, selected.Comp);} }
            });
            break;
        }
    }

    public bool Near(EntityUid a, EntityUid b, float range)
    {
        if (!Exists(a) || !Exists(b)) return false;
        var ac = _transform.GetMapCoordinates(a);
        var bc = _transform.GetMapCoordinates(b);
        return ac.MapId == bc.MapId && (ac.Position - bc.Position).LengthSquared() <= range * range;
    }

    private static float ParticipantRange(MedievalRitualPrototype ritual) =>
        ritual.Effect == "ZaygoPlace4" ? ritual.Radius + 2 : ritual.Radius;

    private bool CanLead(EntityUid user, EntityUid center, MedievalRitualPrototype ritual)
    {
        var range = ritual.Effect == "ZaygoPlace4" ? ParticipantRange(ritual) : 3;
        return Near(user, center, range) &&
            (ritual.Effect != "ZaygoPlace4" || _interaction.InRangeUnobstructed(user, center, range));
    }

    private string DescribeChoices(EntityUid center, MedievalRitualCenterComponent component, MedievalRitualPrototype ritual)
    {
        var kinds = _lookup.GetEntitiesInRange<RitualOfferingComponent>(_transform.GetMapCoordinates(center), 3)
            .Where(e => ritual.OptionalKinds.Contains(e.Comp.Kind) && _offeringPreparation.IsValidOffering(e, e.Comp.Kind) && !HasComp<MedievalRitualOfferingReservedComponent>(e)
                && Transform(e).ParentUid == Transform(center).ParentUid && !Transform(e).Anchored)
            .Select(e => e.Comp.Kind).Distinct().OrderBy(k => k).Select(k => Loc.GetString("medieval-offering-" + k));
        var selected = string.Join(", ", kinds);
        if (selected.Length == 0) selected = Loc.GetString("medieval-ritual-no-options");
        var target = component.Target ?? component.Owner;
        var text = Loc.GetString("medieval-ritual-choices", ("target", target is { } entity && Exists(entity) ? Name(entity) : Loc.GetString("medieval-ritual-no-options")), ("options", selected));
        if (ritual.Effect == "ZaygoPlace4") text += "\n" + Loc.GetString("medieval-ritual-theft-exchange-warning");
        return text;
    }

    /// <summary>Server-authoritative entry point used by the ritual menu.</summary>
    public bool TryStart(EntityUid center, EntityUid user, string ritualId)
    {
        if (HasComp<MedievalRitualAttemptComponent>(center) || !TryComp<MedievalRitualCenterComponent>(center, out var comp) ||
            !_prototypes.TryIndex<MedievalRitualPrototype>(ritualId, out var ritual)) return false;
        Start(center, comp, user, ritual);
        return HasComp<MedievalRitualAttemptComponent>(center);
    }

    private void Start(EntityUid center, MedievalRitualCenterComponent comp, EntityUid user, MedievalRitualPrototype ritual)
    {
        if (comp.Owner != user || !CanLead(user, center, ritual) || HasComp<MedievalRitualAttemptComponent>(center) || !_knowledge.HasKnowledge(user, ritual.Knowledge)) return;
        comp.Participants.Add(user);
        comp.PreviewRadius = ritual.Radius;
        comp.PreviewTheft = ritual.Effect == "ZaygoPlace4";
        Dirty(center, comp);
        var people = comp.Participants.Where(p => Near(p, center, ParticipantRange(ritual)) && _mobs.IsAlive(p)).ToArray();
        if (people.Length < ritual.Participants) { Fail(user, center, "medieval-ritual-not-enough-people"); return; }
        if (!CollectOfferings(center, ritual, out var offerings) || !CollectLiquids(center, ritual, offerings, out var liquids)) { Fail(user, center, "medieval-ritual-missing-offerings"); return; }
        var context = new MedievalRitualContext
        {
            Caster = user, Center = center, Target = comp.Target ?? user, Ritual = ritual,
            Participants = people, Offerings = offerings.Keys.ToArray()
        };
        var validate = new MedievalRitualValidateEvent(context);
        RaiseLocalEvent(validate);
        if (validate.Error != null || !validate.Handled)
        {
            Fail(user, center, validate.Error ?? "medieval-ritual-invalid-target");
            return;
        }
        foreach (var carrier in context.ReservedObjects)
        {
            if (!Exists(carrier) || HasComp<MedievalRitualOfferingReservedComponent>(carrier))
            { Fail(user, center, "medieval-ritual-missing-offerings"); return; }
            offerings.TryAdd(carrier, 0);
        }
        var attempt = EnsureComp<MedievalRitualAttemptComponent>(center);
        attempt.Context = context;
        attempt.Reserved = offerings;
        attempt.Liquids = liquids;
        foreach (var entity in people.Concat(offerings.Keys))
            attempt.Positions[entity] = _transform.GetMapCoordinates(entity);
        if (ritual.Effect != "ZaygoPlace4")
            attempt.Positions[context.Target] = _transform.GetMapCoordinates(context.Target);
        if (context.StationaryTargets != null)
            foreach (var target in context.StationaryTargets)
                attempt.Positions[target] = _transform.GetMapCoordinates(target);
        foreach (var entity in offerings.Keys)
            EnsureComp<MedievalRitualOfferingReservedComponent>(entity).Center = center;
        var doAfter = new DoAfterArgs(EntityManager, user, TimeSpan.FromSeconds(ritual.Duration), new MedievalPrayerDoAfterEvent(), center, center)
        {
            BreakOnMove = true, BreakOnDamage = true, NeedHand = false,
            DistanceThreshold = ritual.Effect == "ZaygoPlace4" ? ParticipantRange(ritual) : 3
        };
        if (!_doAfter.TryStartDoAfter(doAfter, out var id)) Release(center);
        else attempt.DoAfter = id;
    }

    private bool CollectOfferings(EntityUid center, MedievalRitualPrototype ritual, out Dictionary<EntityUid, int> result)
    {
        result = new();
        var nearby = _lookup.GetEntitiesInRange(_transform.GetMapCoordinates(center), 3)
            .Where(e => !HasComp<MedievalRitualOfferingReservedComponent>(e) && !Transform(e).Anchored && Transform(e).ParentUid == Transform(center).ParentUid)
            .ToArray();
        foreach (var (prototype, required) in ritual.Offerings.Concat(ritual.OptionalOfferings))
        {
            var left = required;
            foreach (var uid in nearby)
            {
                // A dedicated selector is distinct from the prayer's common payment.
                if (TryComp<RitualOfferingComponent>(uid, out var dedicated) && ritual.OptionalKinds.Contains(dedicated.Kind)) continue;
                var proto = MetaData(uid).EntityPrototype;
                if (proto == null || result.ContainsKey(uid) || !(proto.ID == prototype || _prototypes.EnumerateParents<EntityPrototype>(proto.ID).Any(p => p.ID == prototype))) continue;
                var amount = Math.Min(left, TryComp<StackComponent>(uid, out var stack) ? _stacks.GetCount(uid, stack) : 1);
                if (amount <= 0) continue;
                result[uid] = amount;
                left -= amount;
                if (left == 0) break;
            }
            if (left > 0 && ritual.Offerings.ContainsKey(prototype)) return false;
        }
        foreach (var entity in nearby)
        {
            if (result.ContainsKey(entity) || !TryComp<RitualOfferingComponent>(entity, out var kind) || !ritual.OptionalKinds.Contains(kind.Kind) ||
                !_offeringPreparation.IsValidOffering(entity, kind.Kind)) continue;
            result[entity] = 1;
        }
        return true;
    }

    private bool StillValid(EntityUid center, MedievalRitualAttemptComponent attempt)
    {
        var context = attempt.Context;
        if (attempt.Invalid || !Exists(context.Target) || !_knowledge.HasKnowledge(context.Caster, context.Ritual.Knowledge)) return false;
        var centerComp = Comp<MedievalRitualCenterComponent>(center);
        foreach (var participant in context.Participants)
            if (!centerComp.Participants.Contains(participant) || !_mobs.IsAlive(participant)) return false;
        foreach (var (entity, position) in attempt.Positions)
        {
            if (!Exists(entity)) return false;
            var current = _transform.GetMapCoordinates(entity);
            if (current.MapId != position.MapId || (current.Position - position.Position).LengthSquared() > .09f) return false;
            if (attempt.Reserved.TryGetValue(entity, out var amount) && TryComp<StackComponent>(entity, out var stack) && stack.Count < amount) return false;
            if (attempt.Reserved.ContainsKey(entity) && TryComp<RitualOfferingComponent>(entity, out var offering) &&
                context.Ritual.OptionalKinds.Contains(offering.Kind) && !_offeringPreparation.IsValidOffering(entity, offering.Kind)) return false;
        }
        foreach (var liquid in attempt.Liquids)
            if (!TryComp<SolutionComponent>(liquid.Solution, out var solution) || solution.Solution.GetTotalPrototypeQuantity(liquid.Reagent) < liquid.Amount) return false;
        return true;
    }

    private bool CollectLiquids(EntityUid center, MedievalRitualPrototype ritual, Dictionary<EntityUid, int> offerings,
        out List<(EntityUid Holder, EntityUid Solution, string Reagent, FixedPoint2 Amount)> liquids)
    {
        liquids = new();
        foreach (var (reagent, required) in ritual.Reagents)
        {
            var left = FixedPoint2.New(required);
            foreach (var holder in _lookup.GetEntitiesInRange<ItemComponent>(_transform.GetMapCoordinates(center), 3))
            {
                if (HasComp<MedievalRitualOfferingReservedComponent>(holder) || offerings.TryGetValue(holder, out var itemCount) && itemCount > 0 || Transform(holder).ParentUid != Transform(center).ParentUid) continue;
                foreach (var (_, solution) in _solutions.EnumerateSolutions((holder.Owner, null)))
                {
                    var available = solution.Comp.Solution.GetTotalPrototypeQuantity(reagent);
                    var take = FixedPoint2.Min(left, available);
                    if (take <= 0) continue;
                    liquids.Add((holder.Owner, solution.Owner, reagent, take));
                    offerings[holder.Owner] = 0;
                    left -= take;
                    if (left == 0) break;
                }
                if (left == 0) break;
            }
            if (left > 0) return false;
        }
        return true;
    }

    public override void Update(float frameTime)
    {
        var query = EntityQueryEnumerator<MedievalRitualAttemptComponent>();
        while (query.MoveNext(out var center, out var attempt))
            if (!attempt.Invalid && !StillValid(center, attempt))
            {
                attempt.Invalid = true;
                _doAfter.Cancel(attempt.DoAfter);
            }
    }

    private void OnPrayer(EntityUid uid, MedievalRitualCenterComponent component, MedievalPrayerDoAfterEvent args)
    {
        if (args.Handled || !TryComp<MedievalRitualAttemptComponent>(uid, out var attempt)) return;
        args.Handled = true;
        if (args.Cancelled || !StillValid(uid, attempt))
        {
            Fail(attempt.Context.Caster, uid, "medieval-ritual-interrupted");
            Release(uid);
            return;
        }
        attempt.Context.Data.Clear();
        var validate = new MedievalRitualValidateEvent(attempt.Context);
        RaiseLocalEvent(validate);
        if (validate.Error != null || !validate.Handled)
        {
            Fail(attempt.Context.Caster, uid, validate.Error ?? "medieval-ritual-invalid-target");
            Release(uid);
            return;
        }
        // Execute synchronously, while optional offering entities can still be inspected.
        RaiseLocalEvent(new MedievalRitualExecuteEvent(attempt.Context));
        foreach (var liquid in attempt.Liquids)
            if (TryComp<SolutionComponent>(liquid.Solution, out var solution))
                _solutions.RemoveReagent((liquid.Solution, solution), liquid.Reagent, liquid.Amount);
        foreach (var (offering, count) in attempt.Reserved)
        {
            if (!Exists(offering) || count == 0) continue;
            if (HasComp<StackComponent>(offering)) _stacks.Use(offering, count);
            else QueueDel(offering);
        }
        _popup.PopupEntity(Loc.GetString("medieval-ritual-success"), uid, attempt.Context.Caster);
        Release(uid);
    }

    private void OnShutdown(EntityUid uid, MedievalRitualCenterComponent comp, ComponentShutdown args) => Release(uid);

    private void Release(EntityUid center)
    {
        if (!TryComp<MedievalRitualAttemptComponent>(center, out var attempt)) return;
        foreach (var offering in attempt.Reserved.Keys)
            if (TryComp<MedievalRitualOfferingReservedComponent>(offering, out var reservation) && reservation.Center == center)
                RemComp<MedievalRitualOfferingReservedComponent>(offering);
        RemComp<MedievalRitualAttemptComponent>(center);
    }

    private void Fail(EntityUid user, EntityUid center, string error) => _popup.PopupEntity(Loc.GetString(error), center, user);
}
