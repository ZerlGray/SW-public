using System.Linq;
using Content.Server.Botany.Components;
using Content.Server.SpikeTrap.Components;
using Content.Shared._RD.Weight.Components;
using Content.Shared._RD.Weight.Systems;
using Content.Shared.DoAfter;
using Content.Shared.Imperial.Medieval.BookAbilities;
using Content.Shared.Imperial.Medieval.Knowledge;
using Content.Shared.Interaction;
using Content.Shared.Interaction.Events;
using Content.Shared.Mobs.Components;
using Content.Shared.Storage.Components;
using Robust.Shared.Containers;

namespace Content.Server.Imperial.Medieval.BookAbilities;

public sealed partial class MedievalBookAbilitySystem
{
    [Dependency] private readonly RDWeightSystem _packedWeight = default!;

    private void InitializePacking()
    {
        SubscribeLocalEvent<LearnedKnowledgeComponent, BookPorterActionEvent>(OnPackCrateAction);
        SubscribeLocalEvent<LearnedKnowledgeComponent, BookPackWorkbenchActionEvent>(OnPackWorkbenchAction);
        SubscribeLocalEvent<LearnedKnowledgeComponent, BookTrapDisarmActionEvent>(OnPackTrapAction);
        SubscribeLocalEvent<LearnedKnowledgeComponent, BookTransplantActionEvent>(OnPackRootsAction);
        SubscribeLocalEvent<BookPackedObjectComponent, UseInHandEvent>(OnUnpackInHand);
        SubscribeLocalEvent<BookPackedObjectComponent, ActivateInWorldEvent>(OnUnpackInWorld);
        SubscribeLocalEvent<BookPackedObjectComponent, BookUnpackDoAfterEvent>(OnUnpackFinished);
    }

    private void OnPackCrateAction(EntityUid uid, LearnedKnowledgeComponent comp, BookPorterActionEvent args)
    {
        if (args.Handled || !CanPackCrate(args.Target)) return;
        args.Handled = Start(uid, args.Target, "BookPorter", 6,
            () => CanPackCrate(args.Target), () => Pack(uid, args.Target, "BookPorter"));
    }

    private void OnPackWorkbenchAction(EntityUid uid, LearnedKnowledgeComponent comp, BookPackWorkbenchActionEvent args)
    {
        if (args.Handled || !CanPackWorkbench(args.Target)) return;
        args.Handled = Start(uid, args.Target, "BookPackWorkbench", 20,
            () => CanPackWorkbench(args.Target), () => Pack(uid, args.Target, "BookPackWorkbench"));
    }

    private void OnPackTrapAction(EntityUid uid, LearnedKnowledgeComponent comp, BookTrapDisarmActionEvent args)
    {
        if (args.Handled) return;
        var target = TryComp<SpikeTrapVisualComponent>(args.Target, out var visual)
            ? visual.Controller
            : args.Target;
        if (!CanPackTrap(target)) return;
        // The visible spikes are replaced whenever the trap fires. Keep the persistent controller
        // as the work target so an ordinary animation change cannot cancel the disarming attempt.
        args.Handled = Start(uid, target, "BookTrapDisarm", 8,
            () => CanPackTrap(target), () => Pack(uid, target, "BookTrapDisarm"));
    }

    private void OnPackRootsAction(EntityUid uid, LearnedKnowledgeComponent comp, BookTransplantActionEvent args)
    {
        if (args.Handled || !CanPackRoots(args.Target)) return;
        args.Handled = Start(uid, args.Target, "BookTransplant", 15,
            () => CanPackRoots(args.Target), () => Pack(uid, args.Target, "BookTransplant"));
    }

    private bool IsUnpackedObject(EntityUid target) => Exists(target) &&
        !_containers.TryGetContainingContainer(target, out _) && !ContainsPerson(target);

    private bool CanPackCrate(EntityUid target) => IsUnpackedObject(target) &&
        HasComp<EntityStorageComponent>(target) && !Transform(target).Anchored;

    private bool CanPackTrap(EntityUid target) => IsUnpackedObject(target) && HasComp<SpikeTrapComponent>(target);

    private bool CanPackRoots(EntityUid target) => IsUnpackedObject(target) &&
        TryComp<PlantHolderComponent>(target, out var plant) && plant.Seed != null && !plant.Dead;

    private bool ContainsPerson(EntityUid uid)
    {
        if (HasComp<MobStateComponent>(uid)) return true;
        return TryComp<ContainerManagerComponent>(uid, out var manager) &&
            manager.Containers.Values.Any(c => c.ContainedEntities.Any(ContainsPerson));
    }

    private bool CanPackWorkbench(EntityUid target) =>
        IsUnpackedObject(target) && HasComp<BookPackableWorkbenchComponent>(target);

    private void Pack(EntityUid user, EntityUid target, string ability)
    {
        if (!IsUnpackedObject(target)) return;
        var prototype = ability switch
        {
            "BookPorter" => "MedievalBookPackedCrate",
            "BookPackWorkbench" => "MedievalBookPackedWorkbench",
            "BookTrapDisarm" => "MedievalBookPackedTrap",
            "BookTransplant" => "MedievalBookPackedRoots",
            _ => null
        };
        if (prototype == null) return;

        var bundle = Spawn(prototype, Transform(target).Coordinates);
        var packed = Comp<BookPackedObjectComponent>(bundle);
        packed.WasAnchored = Transform(target).Anchored;
        packed.WorldRotation = _transform.GetWorldRotation(target);
        packed.Ability = ability;
        _meta.SetEntityName(bundle, Loc.GetString("book-portable-name", ("name", Name(target))));
        var container = _containers.EnsureContainer<ContainerSlot>(bundle, "packed-object");
        if (packed.WasAnchored) _transform.Unanchor(target);
        if (!_containers.Insert(target, container))
        {
            if (packed.WasAnchored) _transform.AnchorEntity(target, Transform(target));
            QueueDel(bundle);
            return;
        }

        // A packed station cannot finish someone else's outstanding craft from inside the bundle.
        foreach (var doAfter in EntityQuery<DoAfterComponent>())
        {
            foreach (var work in doAfter.DoAfters.Values.ToArray())
            {
                if (!work.Completed && !work.Cancelled && (work.Args.Target == target || work.Args.EventTarget == target))
                    _doAfter.Cancel(work.Id);
            }
        }
        if (TryComp<SpikeTrapComponent>(target, out var trap))
        {
            trap.Enabled = false;
            if (trap.ActiveTrapEntity is { } active) QueueDel(active);
            if (trap.DeactiveTrapEntity is { } inactive) QueueDel(inactive);
            trap.ActiveTrapEntity = null;
            trap.DeactiveTrapEntity = null;
        }

        // Keep the real weight tree. Zero-weight bridge components let old containers without
        // weight metadata pass their contents' mass through; no mass is copied onto the wrapper.
        EnsurePackedWeightTree(target);
        _packedWeight.Refresh(bundle);
        _hands.TryPickupAnyHand(user, bundle);
    }

    private void EnsurePackedWeightTree(EntityUid target)
    {
        var children = Transform(target).ChildEnumerator;
        while (children.MoveNext(out var child))
            EnsurePackedWeightTree(child);
        EnsureComp<RDWeightComponent>(target);
        _packedWeight.Refresh(target);
    }

    private void OnUnpackInHand(EntityUid uid, BookPackedObjectComponent comp, UseInHandEvent args)
    {
        if (!args.Handled)
            args.Handled = TryStartUnpacking(args.User, uid);
    }

    private void OnUnpackInWorld(EntityUid uid, BookPackedObjectComponent comp, ActivateInWorldEvent args)
    {
        if (!args.Handled)
            args.Handled = TryStartUnpacking(args.User, uid);
    }

    private bool TryStartUnpacking(EntityUid user, EntityUid bundle)
    {
        if (!_interaction.CanAccess(user, bundle) ||
            !_containers.TryGetContainer(bundle, "packed-object", out var contents) || contents.ContainedEntities.Count != 1)
            return false;
        return _doAfter.TryStartDoAfter(new DoAfterArgs(EntityManager, user, 5,
            new BookUnpackDoAfterEvent(), bundle, bundle)
        {
            NeedHand = true,
            BreakOnMove = true,
            BreakOnDamage = true,
            BreakOnHandChange = false,
            BreakOnDropItem = false,
        });
    }

    private void OnUnpackFinished(EntityUid uid, BookPackedObjectComponent comp, BookUnpackDoAfterEvent args)
    {
        if (args.Handled || args.Cancelled || !_interaction.CanAccess(args.User, uid) ||
            !_interaction.InRangeUnobstructed(args.User, uid)) return;
        args.Handled = true;
        Unpack(args.User, uid);
    }

    private void Unpack(EntityUid user, EntityUid bundle)
    {
        if (!_containers.TryGetContainer(bundle, "packed-object", out var container) || container.ContainedEntities.Count != 1)
            return;
        var target = container.ContainedEntities[0];
        var packed = Comp<BookPackedObjectComponent>(bundle);
        if (!_containers.Remove(target, container)) return;
        _transform.SetCoordinates(target, Transform(user).Coordinates);
        _transform.AttachToGridOrMap(target);
        // Inserting an object in a held bundle resets its local rotation and follows the carrier.
        // Restore its original world-facing direction after attaching it to the destination grid.
        _transform.SetWorldRotation(target, packed.WorldRotation);
        if (packed.WasAnchored) _transform.AnchorEntity(target, Transform(target));
        if (TryComp<SpikeTrapComponent>(target, out var trap))
        {
            trap.Enabled = true;
            trap.Ready = false;
            trap.Cooldown = 2f;
        }
        _packedWeight.Refresh(target);
        QueueDel(bundle);
    }
}
