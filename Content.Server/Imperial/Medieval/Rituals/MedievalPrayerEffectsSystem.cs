using System.Linq;
using Content.Server.Body.Systems;
using Content.Server.Botany.Components;
using Content.Server.Imperial.Medieval.Body;
using Content.Server.Imperial.Medieval.BookAbilities;
using Content.Shared.Actions;
using Content.Shared.Body.Components;
using Content.Shared.Body.Events;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.Cuffs;
using Content.Shared.Cuffs.Components;
using Content.Shared.Damage;
using Content.Shared.Damage.Components;
using Content.Shared.Damage.Systems;
using Content.Shared.DoAfter;
using Content.Shared.FixedPoint;
using Content.Shared.Humanoid;
using Content.Shared.Imperial.Medieval.Rituals;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Mobs.Systems;
using Content.Shared.Movement.Pulling.Components;
using Content.Shared.Popups;
using Content.Shared.Verbs;
using Robust.Shared.Physics.Events;
using Robust.Shared.Timing;

namespace Content.Server.Imperial.Medieval.Rituals;

[RegisterComponent]
public sealed partial class MedievalPreparedBlessingsComponent : Component
{
    public HashSet<string> Charges = new();
    public Dictionary<string, EntityUid> Actions = new();
    public EntityUid? ReturnCenter;
}

[RegisterComponent]
public sealed partial class MedievalSomaProtectionComponent : Component
{
    public TimeSpan Until;
}

[RegisterComponent]
public sealed partial class MedievalSomaHospitalComponent : Component
{
    public TimeSpan Until;
    public float Radius;
}

[RegisterComponent]
public sealed partial class MedievalTerraHarvestComponent : Component
{
    public TimeSpan Until;
    public TimeSpan NextHarvest;
    public int Remaining = 2;
    public object? Seed;
}

[RegisterComponent]
public sealed partial class MedievalValtorConsciousnessComponent : Component
{
    public TimeSpan Until;
    public bool DelayDeath;
}

[RegisterComponent]
public sealed partial class MedievalKrezaGateComponent : Component
{
    public EntityUid Owner;
    public EntityUid Other;
}

public sealed class MedievalRitualTeleportAttemptEvent(EntityUid subject, EntityUid destination) : EntityEventArgs
{
    public EntityUid Subject = subject;
    public EntityUid Destination = destination;
    public bool Cancelled;
}

[ByRefEvent]
public record struct MedievalPlantHarvestedEvent(bool PreservePlant);

public sealed class MedievalPrayerEffectsSystem : EntitySystem
{
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly MedievalRitualSystem _rituals = default!;
    [Dependency] private readonly BloodstreamSystem _blood = default!;
    [Dependency] private readonly SharedSolutionContainerSystem _solutions = default!;
    [Dependency] private readonly DamageableSystem _damage = default!;
    [Dependency] private readonly MobStateSystem _mobs = default!;
    [Dependency] private readonly MobThresholdSystem _thresholds = default!;
    [Dependency] private readonly SharedStaminaSystem _stamina = default!;
    [Dependency] private readonly SharedActionsSystem _actions = default!;
    [Dependency] private readonly SharedDoAfterSystem _doAfter = default!;
    [Dependency] private readonly SharedCuffableSystem _cuffs = default!;
    [Dependency] private readonly SharedPopupSystem _popup = default!;
    [Dependency] private readonly MedievalCompanionSystem _companions = default!;
    private readonly Dictionary<EntityUid, TimeSpan> _portalTimeout = new();
    private TimeSpan _nextTick;

    public override void Initialize()
    {
        SubscribeLocalEvent<MedievalRitualValidateEvent>(OnValidate);
        SubscribeLocalEvent<MedievalRitualExecuteEvent>(OnExecute);
        SubscribeLocalEvent<MedievalPreparedBlessingsComponent, MedievalUseBlessingActionEvent>(OnBlessing);
        SubscribeLocalEvent<MedievalPreparedBlessingsComponent, MedievalActivateBlessingActionEvent>(OnActivate);
        SubscribeLocalEvent<MedievalPreparedBlessingsComponent, MedievalBlessingDoAfterEvent>(OnBlessingFinished);
        SubscribeLocalEvent<MedievalSomaProtectionComponent, BleedModifierEvent>(OnBleed);
        SubscribeLocalEvent<MedievalSomaProtectionComponent, GetSuffocationDamageModifiersEvent>(OnSuffocation);
        SubscribeLocalEvent<MedievalSomaProtectionComponent, DamageModifyEvent>(OnSomaDamage);
        SubscribeLocalEvent<MedievalValtorConsciousnessComponent, UpdateMobStateEvent>(OnConsciousness, after: new[] {typeof(MobThresholdSystem)});
        SubscribeLocalEvent<MedievalTerraHarvestComponent, MedievalPlantHarvestedEvent>(OnHarvest);
        SubscribeLocalEvent<MedievalKrezaGateComponent, StartCollideEvent>(OnGateCollide);
        SubscribeLocalEvent<MedievalKrezaGateComponent, GetVerbsEvent<AlternativeVerb>>(OnGateVerb);
        SubscribeLocalEvent<MedievalKrezaGateComponent, ComponentShutdown>(OnGateShutdown);
    }

    private void OnValidate(MedievalRitualValidateEvent ev)
    {
        var c = ev.Context;
        var effect = c.Ritual.Effect;
        if (!(effect.StartsWith("Soma") || effect.StartsWith("Terra") || effect.StartsWith("Kreza") || effect.StartsWith("Valtor"))) return;
        ev.Handled = true;
        if (effect is "Soma2" or "Kreza2" or "Kreza3" or "Valtor2" or "Valtor3")
        {
            if (!c.Participants.Contains(c.Target) || !_mobs.IsAlive(c.Target) || !_rituals.Near(c.Target, c.Center, c.Ritual.Radius))
                ev.Error = "medieval-ritual-target-participant";
        }
        if (effect.StartsWith("TerraHarvest"))
        {
            var plants = c.StationaryTargets?.Where(p => TryComp<PlantHolderComponent>(p, out var holder) && holder.Seed != null && (c.Ritual.Tier > 2 || !holder.Dead) && _rituals.Near(p, c.Center, c.Ritual.Radius)).ToArray() ?? _lookup.GetEntitiesInRange<PlantHolderComponent>(_transform.GetMapCoordinates(c.Center), c.Ritual.Radius)
                .Where(p => p.Comp.Seed != null && (c.Ritual.Tier > 2 || !p.Comp.Dead))
                .OrderBy(p => (_transform.GetMapCoordinates(p).Position - _transform.GetMapCoordinates(c.Center).Position).LengthSquared())
                .Take(c.Ritual.Tier == 2 ? 4 : c.Ritual.Tier == 3 ? 12 : 24).Select(p => p.Owner).ToArray();
            if (plants.Length == 0) ev.Error = "medieval-ritual-no-plants";
            c.Data["plants"] = plants;
            c.StationaryTargets ??= plants;
        }
        if (effect is "TerraBeasts2" or "TerraBeasts3" or "TerraBeasts4")
        {
            if (!c.Participants.Contains(c.Target)) { ev.Error = "medieval-ritual-target-participant"; return; }
            var limit = c.Ritual.Tier == 4 ? 3 : 1;
            var animals = c.StationaryTargets?.Where(e => _companions.CanTame(e, c.Target) && _rituals.Near(e, c.Center, c.Ritual.Radius)).ToArray() ?? _lookup.GetEntitiesInRange(_transform.GetMapCoordinates(c.Center), c.Ritual.Radius)
                .Where(e => _companions.CanTame(e, c.Target))
                .OrderBy(e => (_transform.GetMapCoordinates(e).Position - _transform.GetMapCoordinates(c.Center).Position).LengthSquared())
                .Take(c.Ritual.Tier == 2 ? 30 : limit).ToArray();
            if (animals.Length == 0) ev.Error = "medieval-ritual-no-beasts";
            if (c.Ritual.Tier > 2 && _companions.OwnedCount(c.Target) + animals.Count(a => !HasComp<MedievalCompanionComponent>(a)) > limit)
                ev.Error = "medieval-ritual-pack-full";
            c.Data["beasts"] = animals;
            // The small truce affects wildlife where it is at completion; binding needs beasts to stay.
            if (c.Ritual.Tier > 2) c.StationaryTargets ??= animals;
        }
        if (effect == "Kreza4")
        {
            var center = Comp<MedievalRitualCenterComponent>(c.Center);
            if (center.LinkedCenter is not { } destination || destination == c.Center || !TryComp<MedievalRitualCenterComponent>(destination, out var other) || other.Owner != c.Caster)
                ev.Error = "medieval-ritual-link-required";
            else c.Data["destination"] = destination;
        }
        if (effect == "Valtor4")
        {
            if (!HasComp<HumanoidAppearanceComponent>(c.Target) || _mobs.IsDead(c.Target) || !_rituals.Near(c.Target, c.Center, 1.5f) || c.Participants.Contains(c.Target))
                ev.Error = "medieval-ritual-sacrifice-required";
        }
    }

    private void OnExecute(MedievalRitualExecuteEvent ev)
    {
        var c = ev.Context;
        switch (c.Ritual.Effect)
        {
            case "Soma2": Prepare(c.Target, "Soma", "ActionMedievalSomaAid"); break;
            case "Soma3": case "Soma4":
                var hospital = EnsureComp<MedievalSomaHospitalComponent>(c.Center);
                hospital.Radius = c.Ritual.Tier == 4 ? 5 : 4;
                hospital.Until = _timing.CurTime + TimeSpan.FromSeconds(c.Ritual.Tier == 4 ? 120 : 300);
                if (c.Ritual.Tier == 4)
                {
                    var dome = EnsureComp<MedievalSomaSanctuaryComponent>(c.Center);
                    dome.Radius = 5;
                    dome.Until = hospital.Until;
                    Dirty(c.Center, dome);
                }
                break;
            case "TerraHarvest2": case "TerraHarvest3": case "TerraHarvest4":
                foreach (var plant in (EntityUid[])c.Data["plants"])
                {
                    var holder = Comp<PlantHolderComponent>(plant);
                    RestorePlant(holder);
                    if (c.Ritual.Tier != 4 || HasComp<MedievalTerraHarvestComponent>(plant)) continue;
                    var blessing = EnsureComp<MedievalTerraHarvestComponent>(plant);
                    blessing.Seed = holder.Seed;
                    blessing.Until = _timing.CurTime + TimeSpan.FromMinutes(10);
                }
                break;
            case "TerraBeasts2":
                foreach (var beast in (EntityUid[])c.Data["beasts"]) _companions.Pacify(beast, TimeSpan.FromMinutes(3));
                break;
            case "TerraBeasts3": case "TerraBeasts4":
                foreach (var beast in (EntityUid[])c.Data["beasts"]) _companions.Tame(beast, c.Target, c.Ritual.Tier == 4 ? 3 : 1);
                break;
            case "Kreza2": Prepare(c.Target, "KrezaFree", "ActionMedievalKrezaFree"); break;
            case "Kreza3":
                Prepare(c.Target, "KrezaReturn", "ActionMedievalKrezaReturn");
                Comp<MedievalPreparedBlessingsComponent>(c.Target).ReturnCenter = c.Center;
                break;
            case "Kreza4":
                var first = Spawn("MedievalKrezaGate", Transform(c.Center).Coordinates);
                var second = Spawn("MedievalKrezaGate", Transform((EntityUid)c.Data["destination"]).Coordinates);
                var a = Comp<MedievalKrezaGateComponent>(first);
                var b = Comp<MedievalKrezaGateComponent>(second);
                a.Other = second; b.Other = first; a.Owner = b.Owner = c.Caster;
                break;
            case "Valtor2": Prepare(c.Target, "ValtorEndurance", "ActionMedievalValtorEndurance"); break;
            case "Valtor3": Prepare(c.Target, "ValtorConscious", "ActionMedievalValtorConscious"); break;
            case "Valtor4":
                // Direct death is intentional sacrifice, not threshold damage that a previous blessing could defer.
                if (_thresholds.TryGetDeadThreshold(c.Target, out var death) && TryComp<DamageableComponent>(c.Target, out var sacrificed))
                    _damage.TryChangeDamage(c.Target, new DamageSpecifier {DamageDict = new() {{"Bloodloss", FixedPoint2.Max(1, death.Value - sacrificed.TotalDamage + 1)}}}, ignoreResistances: true, origin: c.Caster);
                _mobs.ChangeMobState(c.Target, MobState.Dead, origin: c.Caster);
                foreach (var participant in c.Participants.Take(4)) Prepare(participant, "ValtorDeath", "ActionMedievalValtorDeath");
                break;
        }
    }

    private void Prepare(EntityUid uid, string kind, string actionPrototype)
    {
        var comp = EnsureComp<MedievalPreparedBlessingsComponent>(uid);
        comp.Charges.Add(kind);
        if (comp.Actions.TryGetValue(kind, out var old) && Exists(old)) return;
        EntityUid? action = null;
        _actions.AddAction(uid, ref action, actionPrototype);
        if (action is { } entity) comp.Actions[kind] = entity;
    }

    private void OnBlessing(EntityUid uid, MedievalPreparedBlessingsComponent comp, MedievalUseBlessingActionEvent args)
    {
        if (args.Handled || _mobs.IsDead(uid) || !comp.Charges.Contains(args.Kind) || !_rituals.Near(uid, args.Target, 1.5f)) return;
        if (args.Kind == "Soma" && (!HasComp<BloodstreamComponent>(args.Target) || _mobs.IsDead(args.Target))) return;
        if (args.Kind == "KrezaFree" && !HasComp<CuffableComponent>(args.Target)) return;
        args.Handled = _doAfter.TryStartDoAfter(new DoAfterArgs(EntityManager, uid, TimeSpan.FromSeconds(1), new MedievalBlessingDoAfterEvent {Kind = args.Kind}, uid, args.Target)
        {
            NeedHand = false, BreakOnDamage = true, BreakOnMove = true, RequireCanInteract = args.Kind != "KrezaFree"
        });
    }

    private void OnActivate(EntityUid uid, MedievalPreparedBlessingsComponent comp, MedievalActivateBlessingActionEvent args)
    {
        if (args.Handled || _mobs.IsDead(uid) || !comp.Charges.Contains(args.Kind)) return;
        if (args.Kind == "KrezaReturn")
        {
            if (comp.ReturnCenter is not { } center || !Exists(center)) return;
            args.Handled = _doAfter.TryStartDoAfter(new DoAfterArgs(EntityManager, uid, TimeSpan.FromSeconds(3), new MedievalBlessingDoAfterEvent {Kind = args.Kind}, uid, uid)
            {NeedHand = false, BreakOnMove = true, BreakOnDamage = true});
            return;
        }
        args.Handled = true;
        ApplyValtor(uid, args.Kind);
        Consume(uid, comp, args.Kind);
    }

    private void ApplyValtor(EntityUid uid, string kind)
    {
        var seconds = kind == "ValtorDeath" ? 30 : kind == "ValtorEndurance" ? 45 : 60;
        if (kind is "ValtorEndurance" or "ValtorDeath")
        {
            var endurance = EnsureComp<MedievalValtorEnduranceComponent>(uid);
            endurance.Until = _timing.CurTime + TimeSpan.FromSeconds(seconds);
            Dirty(uid, endurance);
            _stamina.TakeStaminaDamage(uid, 0, visual: false, log: false);
        }
        if (kind is "ValtorConscious" or "ValtorDeath")
        {
            var blessing = EnsureComp<MedievalValtorConsciousnessComponent>(uid);
            blessing.Until = _timing.CurTime + TimeSpan.FromSeconds(seconds);
            blessing.DelayDeath |= kind == "ValtorDeath";
            _mobs.UpdateMobState(uid);
        }
    }

    private void OnBlessingFinished(EntityUid uid, MedievalPreparedBlessingsComponent comp, MedievalBlessingDoAfterEvent args)
    {
        if (args.Handled || args.Cancelled || !comp.Charges.Contains(args.Kind) || args.Target is not { } target) return;
        args.Handled = true;
        switch (args.Kind)
        {
            case "Soma":
                if (_mobs.IsDead(target) || !_rituals.Near(uid, target, 1.5f)) return;
                Protect(target, TimeSpan.FromSeconds(90));
                break;
            case "KrezaFree":
                if (!_rituals.Near(uid, target, 1.5f) || !TryComp<CuffableComponent>(target, out var cuffs) || _cuffs.GetAllCuffs(cuffs).Count == 0) return;
                foreach (var cuff in _cuffs.GetAllCuffs(cuffs).ToArray()) _cuffs.Uncuff(target, uid, cuff, cuffs);
                break;
            case "KrezaReturn":
                if (comp.ReturnCenter is not { } destination || !Exists(destination) || !Teleport(uid, destination)) return;
                break;
        }
        Consume(uid, comp, args.Kind);
    }

    private void Consume(EntityUid uid, MedievalPreparedBlessingsComponent comp, string kind)
    {
        comp.Charges.Remove(kind);
        if (comp.Actions.Remove(kind, out var action)) _actions.RemoveAction(uid, action);
    }

    private void Protect(EntityUid uid, TimeSpan duration)
    {
        var protection = EnsureComp<MedievalSomaProtectionComponent>(uid);
        protection.Until = TimeSpan.FromTicks(Math.Max(protection.Until.Ticks, (_timing.CurTime + duration).Ticks));
        if (TryComp<BloodstreamComponent>(uid, out var blood)) _blood.TryModifyBleedAmount((uid, blood), -blood.BleedAmount);
    }
    private void OnBleed(EntityUid uid, MedievalSomaProtectionComponent comp, ref BleedModifierEvent args)
    {
        if (comp.Until > _timing.CurTime) args.BleedAmount = 0;
    }
    private void OnSuffocation(EntityUid uid, MedievalSomaProtectionComponent comp, ref GetSuffocationDamageModifiersEvent args)
    {
        if (comp.Until > _timing.CurTime) args.Modifier = 0;
    }
    private void OnSomaDamage(EntityUid uid, MedievalSomaProtectionComponent comp, DamageModifyEvent args)
    {
        if (comp.Until <= _timing.CurTime || args.Origin != null) return;
        if (args.Damage.DamageDict.TryGetValue("Bloodloss", out var loss) && loss > 0) args.Damage.DamageDict["Bloodloss"] = 0;
    }
    private void OnConsciousness(EntityUid uid, MedievalValtorConsciousnessComponent comp, ref UpdateMobStateEvent args)
    {
        if (comp.Until <= _timing.CurTime || args.Component.CurrentState == MobState.Dead) return;
        if (args.State == MobState.Critical || comp.DelayDeath && args.State == MobState.Dead) args.State = MobState.Alive;
    }

    private void RestorePlant(PlantHolderComponent plant)
    {
        if (plant.Seed == null) return;
        plant.Dead = false;
        plant.Health = plant.Seed.Endurance;
        plant.WaterLevel = plant.NutritionLevel = 100;
        plant.PestLevel = plant.WeedLevel = plant.Toxins = 0;
        plant.Age = Math.Max(plant.Age, (int)Math.Ceiling(plant.Seed.Maturation));
        plant.Harvest = true;
        plant.ForceUpdate = true;
    }
    private void OnHarvest(EntityUid uid, MedievalTerraHarvestComponent comp, ref MedievalPlantHarvestedEvent args)
    {
        if (comp.Until <= _timing.CurTime || comp.Remaining <= 0 || !TryComp<PlantHolderComponent>(uid, out var holder) || !ReferenceEquals(holder.Seed, comp.Seed)) return;
        args.PreservePlant = true;
        comp.Remaining--;
        comp.NextHarvest = _timing.CurTime + TimeSpan.FromMinutes(2);
    }

    private void OnGateCollide(EntityUid uid, MedievalKrezaGateComponent comp, ref StartCollideEvent args)
    {
        if (Transform(args.OtherEntity).Anchored || _portalTimeout.TryGetValue(args.OtherEntity, out var until) && until > _timing.CurTime || !Exists(comp.Other)) return;
        Teleport(args.OtherEntity, comp.Other);
    }
    private bool Teleport(EntityUid subject, EntityUid destination)
    {
        var attempt = new MedievalRitualTeleportAttemptEvent(subject, destination);
        RaiseLocalEvent(attempt);
        if (attempt.Cancelled) return false;
        var carried = CompOrNull<PullerComponent>(subject)?.Pulling;
        if (carried is { } patient && _rituals.Near(subject, patient, 2))
        {
            var patientAttempt = new MedievalRitualTeleportAttemptEvent(patient, destination);
            RaiseLocalEvent(patientAttempt);
            if (patientAttempt.Cancelled) return false;
            _portalTimeout[patient] = _timing.CurTime + TimeSpan.FromSeconds(2);
            _transform.SetCoordinates(patient, Transform(destination).Coordinates);
        }
        _portalTimeout[subject] = _timing.CurTime + TimeSpan.FromSeconds(2);
        _transform.SetCoordinates(subject, Transform(destination).Coordinates);
        return true;
    }
    private void OnGateVerb(EntityUid uid, MedievalKrezaGateComponent comp, GetVerbsEvent<AlternativeVerb> args)
    {
        if (!args.CanAccess || !args.CanInteract || args.User != comp.Owner) return;
        var user = args.User;
        args.Verbs.Add(new AlternativeVerb {Text = Loc.GetString("medieval-ritual-close-gate"), Act = () => {if (_rituals.Near(user, uid, 3)) QueueDel(uid);}});
    }
    private void OnGateShutdown(EntityUid uid, MedievalKrezaGateComponent comp, ComponentShutdown args)
    {
        if (Exists(comp.Other) && !TerminatingOrDeleted(comp.Other)) QueueDel(comp.Other);
    }

    public override void Update(float frameTime)
    {
        if (_timing.CurTime < _nextTick) return;
        _nextTick = _timing.CurTime + TimeSpan.FromSeconds(1);
        var hospitals = EntityQueryEnumerator<MedievalSomaHospitalComponent>();
        while (hospitals.MoveNext(out var uid, out var hospital))
        {
            if (hospital.Until <= _timing.CurTime) {RemCompDeferred<MedievalSomaHospitalComponent>(uid); RemCompDeferred<MedievalSomaSanctuaryComponent>(uid); continue;}
            foreach (var patient in _lookup.GetEntitiesInRange<BloodstreamComponent>(_transform.GetMapCoordinates(uid), hospital.Radius))
            {
                if (_mobs.IsDead(patient)) continue;
                Protect(patient, TimeSpan.FromSeconds(1.5));
                _blood.TryModifyBloodLevel((patient.Owner, patient.Comp), FixedPoint2.New(2));
                if (_solutions.ResolveSolution(patient.Owner, patient.Comp.ChemicalSolutionName, ref patient.Comp.ChemicalSolution, out var chemicals))
                    foreach (var (reagent, _) in chemicals.Contents.ToArray())
                        if (reagent.Prototype is "Toxin" or "medievalbestpoison")
                            _solutions.RemoveReagent(patient.Comp.ChemicalSolution.Value, reagent, FixedPoint2.New(1));
                _damage.TryChangeDamage(patient, new DamageSpecifier {DamageDict = new() {{"Blunt", -1}, {"Slash", -1}, {"Piercing", -1}, {"Heat", -1}, {"Asphyxiation", -1}, {"Bloodloss", -1}}}, ignoreResistances: true, interruptsDoAfters: false);
            }
        }
        var protection = EntityQueryEnumerator<MedievalSomaProtectionComponent>();
        while (protection.MoveNext(out var uid, out var comp)) if (comp.Until <= _timing.CurTime) RemCompDeferred<MedievalSomaProtectionComponent>(uid);
        var plants = EntityQueryEnumerator<MedievalTerraHarvestComponent, PlantHolderComponent>();
        while (plants.MoveNext(out var uid, out var blessing, out var holder))
        {
            if (blessing.Until <= _timing.CurTime || !ReferenceEquals(holder.Seed, blessing.Seed)) {RemCompDeferred<MedievalTerraHarvestComponent>(uid); continue;}
            if (blessing.NextHarvest != TimeSpan.Zero && blessing.NextHarvest <= _timing.CurTime)
            {
                RestorePlant(holder);
                blessing.NextHarvest = TimeSpan.Zero;
            }
        }
        var valtor = EntityQueryEnumerator<MedievalValtorConsciousnessComponent>();
        while (valtor.MoveNext(out var uid, out var blessing))
        {
            if (blessing.Until > _timing.CurTime) continue;
            RemCompDeferred<MedievalValtorConsciousnessComponent>(uid);
            _thresholds.RefreshLivingThresholds(uid);
        }
        var endurance = EntityQueryEnumerator<MedievalValtorEnduranceComponent>();
        while (endurance.MoveNext(out var uid, out var blessing))
        {
            if (blessing.Until > _timing.CurTime) continue;
            RemCompDeferred<MedievalValtorEnduranceComponent>(uid);
            _stamina.TakeStaminaDamage(uid, 0, visual: false, log: false);
        }
        foreach (var entity in _portalTimeout.Where(x => x.Value <= _timing.CurTime).Select(x => x.Key).ToArray()) _portalTimeout.Remove(entity);
    }
}
