using System.Linq;
using Content.Server.Imperial.Medieval.BookAbilities;
using Content.Server.NPC.Components;
using Content.Server.NPC.HTN;
using Content.Shared.Armor;
using Content.Shared.ActionBlocker;
using Content.Shared.Chemistry.Components;
using Content.Shared.Chemistry.Components.SolutionManager;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.Clothing.Components;
using Content.Shared.CombatMode;
using Content.Shared.FixedPoint;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Humanoid;
using Content.Shared.Imperial.Medieval.Rituals;
using Content.Shared.Inventory;
using Content.Shared.Interaction;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Movement.Components;
using Content.Shared.NPC.Components;
using Content.Shared.Prying.Components;
using Content.Shared.Storage;
using Content.Shared.Storage.Components;
using Content.Shared.Verbs;
using Content.Shared.Weapons.Melee;
using Robust.Shared.Containers;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Timing;

namespace Content.Server.Imperial.Medieval.Rituals;

[RegisterComponent]
public sealed partial class RitualVesselLinkComponent : Component
{
    public EntityUid Master;
    public EntityUid? Partner;
    public FixedPoint2 LastVolume;
    public TimeSpan Until;
    public bool Transferring;
    public bool CanInitiate;
}

[RegisterComponent]
public sealed partial class RitualAnimatedComponent : Component
{
    public EntityUid Master;
    public TimeSpan Until;
    public bool Shell;
    public bool Finishing;
    public BodyType OriginalBodyType;
    public List<Component> Added = new();
    public List<EntityUid> Equipment = new();
    public bool Suppressed;
}

/// <summary>Animation moves real items; linked vessels transfer real solution rather than copying its composition.</summary>
public sealed class MedievalRitualAnimatedSystem : EntitySystem
{
    [Dependency] private readonly SharedSolutionContainerSystem _solutions = default!;
    [Dependency] private readonly SharedContainerSystem _containers = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly SharedPhysicsSystem _physics = default!;
    [Dependency] private readonly SharedHandsSystem _hands = default!;
    [Dependency] private readonly InventorySystem _inventory = default!;
    [Dependency] private readonly MedievalCompanionSystem _companions = default!;
    [Dependency] private readonly SharedRitualMagicSystem _magic = default!;
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly IGameTiming _time = default!;
    [Dependency] private readonly SharedInteractionSystem _interaction = default!;
    [Dependency] private readonly HTNSystem _htn = default!;
    [Dependency] private readonly ActionBlockerSystem _blocker = default!;

    public override void Initialize()
    {
        SubscribeLocalEvent<RitualVesselLinkComponent, GetVerbsEvent<AlternativeVerb>>(OnLinkVerbs);
        SubscribeLocalEvent<RitualVesselLinkComponent, SolutionContainerChangedEvent>(OnChanged);
        SubscribeLocalEvent<RitualVesselLinkComponent, ComponentShutdown>(OnLinkShutdown);
        SubscribeLocalEvent<RitualAnimatedComponent, ComponentShutdown>(OnAnimatedShutdown);
    }

    public bool CanApplyGift(EntityUid target, string kind)
    {
        if (kind == "vessels")
            return _solutions.TryGetDrainableSolution(target, out _, out _) && !HasComp<RitualVesselLinkComponent>(target);
        if (TryComp<ClothingComponent>(target, out var clothing) && HasComp<ArmorComponent>(target))
        {
            if ((clothing.Slots & (SlotFlags.OUTERCLOTHING | SlotFlags.INNERCLOTHING)) == 0 || FindWeapon(target) == null)
                return false;
        }
        return kind == "animation" && !HasComp<RitualAnimatedComponent>(target) && !HasComp<MobStateComponent>(target) &&
               !HasComp<RitualTransportProtectedComponent>(target) &&
               !_containers.IsEntityInContainer(target) && HasComp<PhysicsComponent>(target) &&
               (HasComp<StorageComponent>(target) || HasComp<EntityStorageComponent>(target) || HasComp<MeleeWeaponComponent>(target) ||
                HasComp<ArmorComponent>(target) && HasComp<ClothingComponent>(target));
    }

    public void ApplyGift(EntityUid target, string kind, EntityUid owner, int tier, TimeSpan until)
    {
        if (!CanApplyGift(target, kind))
            return;
        if (kind == "vessels")
        {
            var link = AddComp<RitualVesselLinkComponent>(target);
            link.Master = owner;
            link.Until = until;
            link.CanInitiate = true;
            _solutions.TryGetDrainableSolution(target, out _, out var solution);
            link.LastVolume = solution?.Volume ?? FixedPoint2.Zero;
            // The second vessel in the same prepared offering joins the first unpaired vessel automatically.
            var other = EntityQuery<RitualVesselLinkComponent>().FirstOrDefault(comp => comp.Master == owner && comp.Owner != target &&
                comp.Partner == null && comp.CanInitiate && comp.Until > _time.CurTime && comp != link &&
                Transform(comp.Owner).MapID == Transform(target).MapID);
            if (other != null)
                Link(target, other.Owner);
            return;
        }

        if (TryComp<ClothingComponent>(target, out var clothing) && HasComp<ArmorComponent>(target))
        {
            var weapon = FindWeapon(target);
            if (weapon == null)
                return;
            var shell = Spawn("MedievalRitualAnimatedArmour", Transform(target).Coordinates);
            var slot = (clothing.Slots & SlotFlags.OUTERCLOTHING) != 0 ? "outerClothing" : "jumpsuit";
            if (!_inventory.TryEquip(shell, target, slot, silent: true, force: true))
            {
                QueueDel(shell);
                return;
            }
            var animated = AddComp<RitualAnimatedComponent>(shell);
            animated.Master = owner;
            animated.Until = until;
            animated.Shell = true;
            animated.Equipment.Add(target);
            if (_hands.TryPickupAnyHand(shell, weapon.Value))
            {
                animated.Equipment.Add(weapon.Value);
            }
            if (TryComp<HumanoidAppearanceComponent>(shell, out var appearance))
            {
                appearance.PermanentlyHidden.UnionWith(Enum.GetValues<HumanoidVisualLayers>());
                Dirty(shell, appearance);
            }
            _companions.Bind(shell, owner);
            return;
        }

        var effect = AddComp<RitualAnimatedComponent>(target);
        effect.Master = owner;
        effect.Until = until;
        var physics = Comp<PhysicsComponent>(target);
        effect.OriginalBodyType = physics.BodyType;
        _transform.Unanchor(target);
        _physics.SetBodyType(target, BodyType.KinematicController);
        AddTemporary<InputMoverComponent>(target, effect);
        AddTemporary<MobMoverComponent>(target, effect);
        AddTemporary<MovementSpeedModifierComponent>(target, effect);
        AddTemporary<MobStateComponent>(target, effect);
        AddTemporary<CombatModeComponent>(target, effect);
        AddTemporary<NpcFactionMemberComponent>(target, effect);
        AddTemporary<PryingComponent>(target, effect);
        AddTemporary<NPCTargetMemoryComponent>(target, effect);
        var htn = AddTemporary<HTNComponent>(target, effect);
        htn.RootTask = new HTNCompoundTask { Task = "RatServantCompound" };
        AddTemporary<MedievalCompanionComponent>(target, effect);
        _companions.Bind(target, owner);
    }

    private T AddTemporary<T>(EntityUid uid, RitualAnimatedComponent effect) where T : Component, new()
    {
        if (TryComp<T>(uid, out var existing))
            return existing;
        var added = AddComp<T>(uid);
        effect.Added.Add(added);
        return added;
    }

    private EntityUid? FindWeapon(EntityUid armour)
    {
        foreach (var item in _lookup.GetEntitiesInRange(Transform(armour).Coordinates, 1.5f))
        {
            if (item == armour || !HasComp<MeleeWeaponComponent>(item) ||
                HasComp<MobStateComponent>(item) || _containers.IsEntityInContainer(item))
                continue;
            // Clothing is also used for weapons carried on a belt or back.
            const SlotFlags weaponMounts = SlotFlags.BELT | SlotFlags.BACK | SlotFlags.POCKET | SlotFlags.SUITSTORAGE | SlotFlags.PREVENTEQUIP;
            if (TryComp<ClothingComponent>(item, out var clothing) && (clothing.Slots & ~weaponMounts) != SlotFlags.NONE)
                continue;
            return item;
        }
        return null;
    }

    public bool Link(EntityUid first, EntityUid second)
    {
        if (first == second || !TryComp<RitualVesselLinkComponent>(first, out var a) || !a.CanInitiate || a.Until <= _time.CurTime ||
            !_solutions.TryGetDrainableSolution(second, out _, out _))
            return false;
        if (!TryComp<RitualVesselLinkComponent>(second, out var b))
        {
            b = AddComp<RitualVesselLinkComponent>(second);
            b.Master = a.Master;
            b.Until = a.Until;
        }
        if (b.Until <= _time.CurTime || a.Master != b.Master)
            return false;
        if (a.Partner == second && b.Partner == first)
            return true;
        Unlink(first, a);
        Unlink(second, b);
        a.Partner = second;
        b.Partner = first;
        if (_solutions.TryGetDrainableSolution(first, out _, out var source)) a.LastVolume = source.Volume;
        if (_solutions.TryGetDrainableSolution(second, out _, out var destination)) b.LastVolume = destination.Volume;
        return true;
    }

    private void OnLinkVerbs(Entity<RitualVesselLinkComponent> ent, ref GetVerbsEvent<AlternativeVerb> args)
    {
        if (!args.CanAccess || !args.CanInteract || args.User != ent.Comp.Master)
            return;
        var user = args.User;
        if (ent.Comp.Partner != null)
            args.Verbs.Add(new AlternativeVerb
            {
                Text = Loc.GetString("ritual-vessels-unlink"),
                Act = () => { if (CanManage(user, ent)) Unlink(ent, ent.Comp); },
            });
        if (!ent.Comp.CanInitiate)
            return;
        foreach (var partner in _lookup.GetEntitiesInRange(Transform(ent).Coordinates, 2f))
        {
            if (partner == ent.Owner || !_solutions.TryGetDrainableSolution(partner, out _, out _) ||
                TryComp<RitualVesselLinkComponent>(partner, out var other) && other.Master != args.User)
                continue;
            args.Verbs.Add(new AlternativeVerb
            {
                Text = Loc.GetString("ritual-vessels-link", ("vessel", Name(partner))),
                Act = () =>
                {
                    if (CanManage(user, ent) && !TerminatingOrDeleted(partner) &&
                        _interaction.InRangeUnobstructed(user, partner) && _interaction.CanAccess(user, partner))
                        Link(ent, partner);
                },
            });
        }
    }

    private bool CanManage(EntityUid user, EntityUid vessel) => !TerminatingOrDeleted(vessel) &&
        TryComp<RitualVesselLinkComponent>(vessel, out var link) && link.Master == user && link.Until > _time.CurTime &&
        _blocker.CanInteract(user, vessel) && _interaction.InRangeUnobstructed(user, vessel) && _interaction.CanAccess(user, vessel);

    private void OnChanged(Entity<RitualVesselLinkComponent> ent, ref SolutionContainerChangedEvent args)
    {
        if (!_solutions.TryGetDrainableSolution(ent.Owner, out var sourceEntity, out var source) || !ReferenceEquals(args.Solution, source))
            return;
        var incoming = FixedPoint2.Max(FixedPoint2.Zero, source.Volume - ent.Comp.LastVolume);
        ent.Comp.LastVolume = source.Volume;
        if (ent.Comp.Transferring || incoming == 0 || ent.Comp.Until <= _time.CurTime || ent.Comp.Partner is not {} partner ||
            !TryComp<RitualVesselLinkComponent>(partner, out var other) || other.Partner != ent.Owner ||
            !_solutions.TryGetDrainableSolution(partner, out var destinationEntity, out var destination) ||
            _magic.IsSuppressed(ent) || _magic.IsSuppressed(partner) ||
            _magic.BlocksMagicPassage(_transform.GetMapCoordinates(ent), _transform.GetMapCoordinates(partner)))
            return;
        var amount = FixedPoint2.Min(incoming, destination.AvailableVolume);
        if (amount <= 0)
            return;
        ent.Comp.Transferring = true;
        other.Transferring = true;
        try
        {
            var moved = _solutions.SplitSolution(sourceEntity.Value, amount);
            if (!_solutions.TryAddSolution(destinationEntity.Value, moved))
                _solutions.TryAddSolution(sourceEntity.Value, moved);
        }
        finally
        {
            ent.Comp.LastVolume = source.Volume;
            other.LastVolume = destination.Volume;
            ent.Comp.Transferring = false;
            other.Transferring = false;
        }
    }

    private void Unlink(EntityUid uid, RitualVesselLinkComponent link)
    {
        if (link.Partner is {} partner && TryComp<RitualVesselLinkComponent>(partner, out var other) && other.Partner == uid)
        {
            other.Partner = null;
            if (!other.CanInitiate)
                RemCompDeferred<RitualVesselLinkComponent>(partner);
        }
        link.Partner = null;
    }

    private void OnLinkShutdown(Entity<RitualVesselLinkComponent> ent, ref ComponentShutdown args) => Unlink(ent, ent.Comp);

    public override void Update(float frameTime)
    {
        foreach (var link in EntityQuery<RitualVesselLinkComponent>().ToArray())
            if (link.Until <= _time.CurTime) RemCompDeferred<RitualVesselLinkComponent>(link.Owner);
        foreach (var animated in EntityQuery<RitualAnimatedComponent>().ToArray())
        {
            var uid = animated.Owner;
            if (animated.Until <= _time.CurTime || _containers.IsEntityInContainer(uid) ||
                animated.Shell && (animated.Equipment.Count == 0 || Deleted(animated.Equipment[0]) || Transform(animated.Equipment[0]).ParentUid != uid) ||
                TryComp<MobStateComponent>(uid, out var mob) && mob.CurrentState == MobState.Dead)
            {
                Finish(uid, animated);
                continue;
            }
            var suppressed = _magic.IsSuppressed(uid);
            if (suppressed == animated.Suppressed || !TryComp<HTNComponent>(uid, out var htn))
                continue;
            animated.Suppressed = suppressed;
            // Suspend the plan without replacing the owner's orders or selected attack target.
            var waiting = TryComp<MedievalCompanionComponent>(uid, out var companion) && companion.Order == "stay";
            _htn.SetHTNEnabled((uid, htn), !suppressed && !waiting);
        }
    }

    private void Finish(EntityUid uid, RitualAnimatedComponent animated)
    {
        if (animated.Finishing)
            return;
        animated.Finishing = true;
        if (animated.Shell)
        {
            // Also preserve items equipped or handed to the armour after its creation.
            var equipment = animated.Equipment.Concat(_inventory.GetHandOrInventoryEntities(uid)).Distinct().ToArray();
            foreach (var item in equipment)
            {
                if (Deleted(item) || Transform(item).ParentUid != uid) continue;
                _containers.TryRemoveFromContainer(item, force: true);
                _transform.SetCoordinates(item, Transform(uid).Coordinates);
            }
            QueueDel(uid);
        }
        else
        {
            if (TerminatingOrDeleted(uid))
                return;
            foreach (var added in animated.Added.AsEnumerable().Reverse())
                if (!added.Deleted) RemCompDeferred(uid, added);
            if (TryComp<PhysicsComponent>(uid, out _))
                _physics.SetBodyType(uid, animated.OriginalBodyType);
            RemCompDeferred<RitualAnimatedComponent>(uid);
        }
    }

    private void OnAnimatedShutdown(Entity<RitualAnimatedComponent> ent, ref ComponentShutdown args) => Finish(ent, ent.Comp);
}
