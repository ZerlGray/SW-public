using Content.Shared.Actions;
using Content.Shared.ActionBlocker;
using Content.Shared.DoAfter;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Systems;
using Content.Shared.Movement.Systems;
using Content.Shared.Standing;
using Content.Shared.Weapons.Melee.Events;
using Robust.Shared.Prototypes;

namespace Content.Shared.Imperial.Medieval.Skills;

/// <summary>Vitality changes action permissions without changing the actual mob state.</summary>
public sealed class SkillCriticalSystem : EntitySystem
{
    [Dependency] private readonly IPrototypeManager _prototypes = default!;
    [Dependency] private readonly MobStateSystem _mobs = default!;
    [Dependency] private readonly ActionBlockerSystem _blocker = default!;
    [Dependency] private readonly MovementSpeedModifierSystem _movement = default!;
    [Dependency] private readonly StandingStateSystem _standing = default!;

    public override void Initialize()
    {
        SubscribeLocalEvent<SkillProfileChangedEvent>(OnProfile);
        SubscribeLocalEvent<SkillVitalityComponent, BeforeDoAfterStartEvent>(OnDoAfter);
        SubscribeLocalEvent<SkillVitalityComponent, CanActInCriticalEvent>(OnCriticalPermission);
        SubscribeLocalEvent<SkillVitalityComponent, GetActionSpeedModifierEvent>(OnActionSpeed);
        SubscribeLocalEvent<SkillVitalityComponent, GetMeleeAttackRateEvent>(OnMeleeRate);
        SubscribeLocalEvent<SkillVitalityComponent, RefreshMovementSpeedModifiersEvent>(OnMovement);
        SubscribeLocalEvent<SkillVitalityComponent, MobStateChangedEvent>(OnMobState);
    }

    private int Level(EntityUid uid) => TryComp<SkillsComponent>(uid, out var skills)
        ? SkillScaling.Level(skills, SharedSkillsSystem.VitalityId) : SkillScaling.Baseline;

    private float CriticalSpeed(EntityUid uid) =>
        _mobs.IsCritical(uid) && Level(uid) >= SkillScaling.Legendary
            ? _prototypes.Index<SkillPrototype>(SharedSkillsSystem.VitalityId).Modifiers["CriticalSpeed"] : 1f;

    private void OnActionSpeed(EntityUid uid, SkillVitalityComponent state, ref GetActionSpeedModifierEvent args) =>
        args.Multiplier *= CriticalSpeed(uid);

    private void OnMeleeRate(EntityUid uid, SkillVitalityComponent state, ref GetMeleeAttackRateEvent args)
    {
        if (args.RaisedOnUser)
            args.Rate *= CriticalSpeed(uid);
    }

    private void OnCriticalPermission(EntityUid uid, SkillVitalityComponent state, ref CanActInCriticalEvent args) =>
        args.Allowed |= Level(uid) >= SkillScaling.Legendary;

    private void OnDoAfter(EntityUid uid, SkillVitalityComponent state, ref BeforeDoAfterStartEvent args)
    {
        if (Level(uid) >= SkillScaling.Expert)
            args.Args.BreakOnDamage = false;
        args.Args.Delay /= CriticalSpeed(uid);
    }

    private void OnMovement(EntityUid uid, SkillVitalityComponent state, RefreshMovementSpeedModifiersEvent args) =>
        args.ModifySpeed(CriticalSpeed(uid), CriticalSpeed(uid), convertible: false);

    private void OnMobState(EntityUid uid, SkillVitalityComponent state, ref MobStateChangedEvent args) =>
        _movement.RefreshMovementSpeedModifiers(uid);

    private void OnProfile(ref SkillProfileChangedEvent args)
    {
        var uid = args.Uid;
        EnsureComp<SkillVitalityComponent>(uid);
        _blocker.UpdateCanMove(uid);
        _movement.RefreshMovementSpeedModifiers(uid);
        if (!_mobs.IsCritical(uid))
            return;
        if (_mobs.CanActInCritical(uid))
            _standing.Stand(uid);
        else
            _standing.Down(uid);
    }
}
