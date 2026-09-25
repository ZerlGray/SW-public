using System.Linq;
using Content.Shared.DoAfter;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Imperial.Medieval.Additions;
using Content.Shared.Imperial.Medieval.BookAbilities;
using Content.Shared.Interaction;
using Content.Shared.Item;
using Content.Shared.Mobs.Components;
using Content.Shared.Storage;
using Content.Shared.Verbs;
using Robust.Shared.Containers;

namespace Content.Server.Imperial.Medieval.Rituals;

/// <summary>A prepared prayer selects a real item from a closed bag; no stripping window or victim popup.</summary>
public sealed class MedievalZaygoTheftSystem : EntitySystem
{
    [Dependency] private readonly SharedHandsSystem _hands = default!;
    [Dependency] private readonly SharedContainerSystem _containers = default!;
    [Dependency] private readonly SharedInteractionSystem _interaction = default!;
    [Dependency] private readonly SharedDoAfterSystem _doAfter = default!;
    [Dependency] private readonly SharedItemSystem _items = default!;
    [Dependency] private readonly AntiStealAfkSystem _afk = default!;

    public override void Initialize()
    {
        SubscribeLocalEvent<ZaygoTheftBlessingComponent, ZaygoTheftDoAfterEvent>(OnStolen);
    }

    private IEnumerable<EntityUid> Bags(EntityUid root)
    {
        if (HasComp<StorageComponent>(root)) yield return root;
        if (!HasComp<MobStateComponent>(root) || !TryComp<ContainerManagerComponent>(root, out var manager)) yield break;
        foreach (var container in manager.Containers.Values)
            foreach (var held in container.ContainedEntities)
                if (HasComp<StorageComponent>(held)) yield return held;
    }

    public void AddTheftVerbs(EntityUid uid, GetVerbsEvent<AlternativeVerb> args)
    {
        if (!args.CanAccess || !args.CanInteract || args.User == uid ||
            !TryComp<ZaygoTheftBlessingComponent>(args.User, out var blessing) || blessing.Charges <= 0) return;
        var user = args.User;
        foreach (var bag in Bags(uid))
        {
            var storage = Comp<StorageComponent>(bag);
            foreach (var item in storage.Container.ContainedEntities.ToArray())
            {
                if (!SmallItem(item)) continue;
                var capturedBag = bag;
                var capturedItem = item;
                args.Verbs.Add(new AlternativeVerb
                {
                    Text = Loc.GetString("book-zaygo-take", ("item", Name(item)), ("bag", Name(bag))),
                    Act = () => TrySteal(user, uid, capturedBag, capturedItem)
                });
            }
        }
    }

    private bool SmallItem(EntityUid uid) => TryComp<ItemComponent>(uid, out var item) &&
        _items.GetSizePrototype(item.Size) <= _items.GetSizePrototype("Small") && !HasComp<MobStateComponent>(uid);

    private bool Allowed(EntityUid user, EntityUid root, EntityUid bag, EntityUid item)
    {
        if (!Exists(root) || !Exists(bag) || !Exists(item) || !SmallItem(item) ||
            !TryComp<ZaygoTheftBlessingComponent>(user, out var blessing) || blessing.Charges <= 0 ||
            !_interaction.InRangeUnobstructed(user, root, 1.5f) ||
            !Bags(root).Contains(bag) || !TryComp<StorageComponent>(bag, out var storage) ||
            !storage.Container.Contains(item) || !_hands.TryGetEmptyHand(user, out _)) return false;
        // Also protect a bag targeted directly while it is worn/carried by an SSD character.
        var ancestor = bag;
        for (var depth = 0; depth < 16; depth++)
        {
            if (HasComp<MobStateComponent>(ancestor) && !_afk.TryStrip(user, ancestor)) return false;
            if (!_containers.TryGetContainingContainer(ancestor, out var container)) break;
            ancestor = container.Owner;
        }
        return _afk.TryStrip(user, root);
    }

    public bool TrySteal(EntityUid user, EntityUid root, EntityUid bag, EntityUid item)
    {
        if (!Allowed(user, root, bag, item)) return false;
        return _doAfter.TryStartDoAfter(new DoAfterArgs(EntityManager, user, 2,
            new ZaygoTheftDoAfterEvent { Root = GetNetEntity(root), Bag = GetNetEntity(bag), Item = GetNetEntity(item) },
            user, root) { BreakOnMove = true, BreakOnDamage = true, NeedHand = true, Hidden = true });
    }

    private void OnStolen(EntityUid uid, ZaygoTheftBlessingComponent comp, ZaygoTheftDoAfterEvent args)
    {
        if (args.Cancelled || args.Handled) return;
        args.Handled = true;
        var root = GetEntity(args.Root);
        var bag = GetEntity(args.Bag);
        var item = GetEntity(args.Item);
        if (!Allowed(uid, root, bag, item)) return;
        var container = Comp<StorageComponent>(bag).Container;
        if (!_containers.Remove(item, container)) return;
        if (!_hands.TryPickupAnyHand(uid, item, animateUser: false, animate: false))
        {
            _containers.Insert(item, container);
            return;
        }
        comp.Charges--;
    }
}
