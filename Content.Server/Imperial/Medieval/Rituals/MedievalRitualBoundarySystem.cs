using System.Numerics;
using Content.Shared.Imperial.Medieval.Magic;
using Content.Shared.Imperial.Medieval.Rituals;
using Content.Shared.Item;
using Content.Shared.Projectiles;
using Content.Shared.Throwing;
using Robust.Shared.Map;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Systems;

namespace Content.Server.Imperial.Medieval.Rituals;

public sealed class MedievalRitualBoundarySystem : EntitySystem
{
    [Dependency] private readonly SharedRitualMagicSystem _magic = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly SharedPhysicsSystem _physics = default!;
    [Dependency] private readonly ThrownItemSystem _thrown = default!;

    public override void Initialize()
    {
        SubscribeLocalEvent<ProjectileComponent, MedievalAfterSpawnEntityBySpellEvent>(OnSpellProjectile);
        SubscribeLocalEvent<ProjectileComponent, MoveEvent>(OnMove);
        // The projectile component's handler belongs to riding; a physical projectile also dispatches here.
        SubscribeLocalEvent<PhysicsComponent, ProjectileBeforeHitEvent>(OnHit);
        SubscribeLocalEvent<ThrownItemComponent, MoveEvent>(OnThrownMove);
        SubscribeLocalEvent<MedievalRitualTeleportAttemptEvent>(OnTeleport);
    }

    private void OnSpellProjectile(EntityUid uid, ProjectileComponent comp, MedievalAfterSpawnEntityBySpellEvent args)
    {
        if (args.SpawnedEntity == uid) EnsureComp<RitualMagicProjectileComponent>(uid);
    }

    private void OnMove(EntityUid uid, ProjectileComponent comp, ref MoveEvent args)
    {
        if (comp.ProjectileSpent) return;
        var from = _transform.ToMapCoordinates(args.OldPosition);
        var to = _transform.ToMapCoordinates(args.NewPosition);
        if (!Blocked(uid, from, to)) return;
        Stop(uid, comp, from);
    }

    private bool Blocked(EntityUid uid, MapCoordinates from, MapCoordinates to) =>
        HasComp<RitualMagicProjectileComponent>(uid) ? _magic.BlocksMagicPassage(from, to) : _magic.BlocksHostility(from, to);

    private void OnHit(EntityUid uid, PhysicsComponent physics, ref ProjectileBeforeHitEvent args)
    {
        if (!TryComp<ProjectileComponent>(uid, out var comp)) return;
        var to = _transform.GetMapCoordinates(args.Target);
        var from = _transform.GetMapCoordinates(uid);
        if (!Blocked(uid, from, to)) return;
        args.Cancelled = true;
        Stop(uid, comp, from);
    }

    private void Stop(EntityUid uid, ProjectileComponent comp, MapCoordinates previous)
    {
        comp.ProjectileSpent = true;
        Dirty(uid, comp);
        if (HasComp<RitualMagicProjectileComponent>(uid) || !HasComp<ItemComponent>(uid)) QueueDel(uid);
        else
        {
            _physics.SetLinearVelocity(uid, Vector2.Zero);
            _transform.SetMapCoordinates(uid, previous);
        }
    }

    private void OnThrownMove(EntityUid uid, ThrownItemComponent comp, ref MoveEvent args)
    {
        if (comp.Landed || HasComp<ProjectileComponent>(uid)) return;
        var from = _transform.ToMapCoordinates(args.OldPosition);
        var to = _transform.ToMapCoordinates(args.NewPosition);
        if (!_magic.BlocksHostility(from, to)) return;
        _thrown.StopThrow(uid, comp);
        _physics.SetLinearVelocity(uid, Vector2.Zero);
        // Avoid recursively moving through the same boundary when the throw is landed.
        _transform.SetMapCoordinates(uid, from);
    }

    private void OnTeleport(MedievalRitualTeleportAttemptEvent args) =>
        args.Cancelled |= _magic.BlocksMagic(_transform.GetMapCoordinates(args.Subject), _transform.GetMapCoordinates(args.Destination));
}

[RegisterComponent]
public sealed partial class RitualMagicProjectileComponent : Component;
