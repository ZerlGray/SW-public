using System.Numerics;
using Content.Shared.Imperial.Dash;
using Content.Shared.Physics;
using Content.Shared.Throwing;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Events;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Shared.Imperial.Medieval.Skills;

/// <summary>Reuses the existing dash binding and airborne movement instead of adding an action.</summary>
public sealed class SkillJumpSystem : EntitySystem
{
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IPrototypeManager _prototypes = default!;
    [Dependency] private readonly SharedPhysicsSystem _physics = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly ThrowingSystem _throwing = default!;

    private float ActionSpeed(EntityUid uid)
    {
        var ev = new Content.Shared.Actions.GetActionSpeedModifierEvent(1f);
        RaiseLocalEvent(uid, ref ev);
        return ev.Multiplier;
    }

    private static int JumpCharges(int level) => level >= SkillScaling.Legendary ? 3 : level >= SkillScaling.Master ? 2 : 1;

    public override void Initialize()
    {
        SubscribeLocalEvent<SkillProfileChangedEvent>(OnProfile);
        SubscribeLocalEvent<SkillAgilityComponent, DashAttemptEvent>(OnDashAttempt);
        SubscribeLocalEvent<SkillAgilityComponent, DashOverrideEvent>(OnDashOverride);
        SubscribeLocalEvent<SkillAgilityComponent, Content.Shared.Damage.ImpactDamageAttemptEvent>(OnImpact);
        SubscribeLocalEvent<SkillAgilityComponent, PreventCollideEvent>(OnCollision);
        SubscribeLocalEvent<SkillsComponent, CheckDashStaminaCostModifiersEvent>(OnStaminaCost);
    }

    private void OnProfile(ref SkillProfileChangedEvent args) => EnsureComp<SkillAgilityComponent>(args.Uid);

    private void OnDashAttempt(EntityUid uid, SkillAgilityComponent state, ref DashAttemptEvent args) =>
        args.Cancelled |= !CanJump(uid);

    private void OnDashOverride(EntityUid uid, SkillAgilityComponent state, ref DashOverrideEvent args)
    {
        if (!args.Handled)
            args.Handled = TryJump(uid, args.Dash, args.Rotation, args.DistanceMultiplier, args.CooldownMultiplier);
    }

    private void OnImpact(EntityUid uid, SkillAgilityComponent state, ref Content.Shared.Damage.ImpactDamageAttemptEvent args) =>
        args.Cancelled |= state.JumpUntil > _timing.CurTime;

    private void OnStaminaCost(EntityUid uid, SkillsComponent skills, ref CheckDashStaminaCostModifiersEvent args)
    {
        args.Modifier /= JumpCharges(SkillScaling.Level(skills, SharedSkillsSystem.AgilityId));
    }

    private void OnCollision(EntityUid uid, SkillAgilityComponent state, ref PreventCollideEvent args)
    {
        if (state.JumpUntil <= _timing.CurTime || args.OurBody.BodyStatus != BodyStatus.InAir
            || !args.OtherFixture.Hard || HasComp<ThrownItemComponent>(args.OtherEntity)
            || HasComp<Content.Shared.Projectiles.ProjectileComponent>(args.OtherEntity))
            return;
        var layer = (CollisionGroup) args.OtherFixture.CollisionLayer;
        if ((layer & (CollisionGroup.Impassable | CollisionGroup.HighImpassable)) == 0
            && (layer & (CollisionGroup.MidImpassable | CollisionGroup.LowImpassable)) != 0)
            args.Cancelled = true;
    }

    public bool CanJump(EntityUid uid)
    {
        if (!TryComp<SkillsComponent>(uid, out var skills)
            || SkillScaling.Level(skills, SharedSkillsSystem.AgilityId) < SkillScaling.Expert
            || !TryComp<SkillAgilityComponent>(uid, out var state) || _timing.CurTime >= state.JumpRecharge)
            return true;
        var charges = JumpCharges(SkillScaling.Level(skills, SharedSkillsSystem.AgilityId));
        return state.JumpsUsed < charges;
    }

    public bool TryJump(EntityUid uid, MedievalDashComponent dash, Angle angle, float distanceModifier, float cooldownModifier)
    {
        if (!TryComp<SkillsComponent>(uid, out var skills) || SkillScaling.Level(skills, SharedSkillsSystem.AgilityId) < SkillScaling.Expert)
            return false;
        var proto = _prototypes.Index<SkillPrototype>(SharedSkillsSystem.AgilityId);
        var state = EnsureComp<SkillAgilityComponent>(uid);
        var direction = angle.ToWorldVec();
        var distance = proto.Modifiers["JumpDistance"] * Math.Max(0.1f, distanceModifier);
        // Low obstacles are jumpable, but walls, closed doors and full-height objects are not.
        var ray = new CollisionRay(_transform.GetWorldPosition(uid), direction,
            (int) (CollisionGroup.Impassable | CollisionGroup.HighImpassable));
        foreach (var hit in _physics.IntersectRay(Transform(uid).MapID, ray, distance, uid, false))
            distance = Math.Min(distance, Math.Max(0f, hit.Distance - 0.4f));
        if (distance > 0.1f)
        {
            state.JumpUntil = _timing.CurTime + TimeSpan.FromSeconds(distance / (10f * ActionSpeed(uid)) + 0.25f);
            _physics.SetLinearVelocity(uid, Vector2.Zero);
            _throwing.TryThrow(uid, direction * distance, 10f * ActionSpeed(uid), uid,
                pushbackRatio: 0f, compensateFriction: true, recoil: false, playSound: false, doSpin: false);
            if (TryComp<ThrownItemComponent>(uid, out var thrown) && thrown.LandTime is { } landing)
                state.JumpUntil = landing + TimeSpan.FromSeconds(0.25f);
            // Re-filter contacts which already existed before takeoff.
            _physics.RegenerateContacts(uid);
        }

        if (_timing.CurTime >= state.JumpRecharge)
        {
            state.JumpsUsed = 0;
            state.JumpRecharge = _timing.CurTime + TimeSpan.FromSeconds(proto.Modifiers["JumpCooldown"] * Math.Max(0.1f, cooldownModifier));
        }
        state.JumpsUsed++;
        var charges = JumpCharges(SkillScaling.Level(skills, SharedSkillsSystem.AgilityId));
        dash.NextDash = state.JumpsUsed < charges ? _timing.CurTime : state.JumpRecharge;
        dash.DashButtonPressedTick = _timing.CurTick;
        Dirty(uid, state);
        return true;
    }
}
