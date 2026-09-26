using System.Linq;
using System.Numerics;
using Content.Shared.DoAfter;
using Content.Shared.Damage;
using Content.Shared.Imperial.Medieval.BookAbilities;
using Content.Shared.Imperial.Medieval.Knowledge;
using Content.Shared.Movement.Events;
using Content.Shared.Nutrition.Components;

namespace Content.Server.Imperial.Medieval.BookAbilities;

public sealed partial class MedievalBookAbilitySystem
{
    private sealed class TamingChannel(EntityUid user, EntityUid beast)
    {
        public readonly EntityUid User = user;
        public readonly EntityUid Beast = beast;
        public DoAfterId? DoAfter;
        public EntityUid? CancelAction;
    }

    private readonly Dictionary<EntityUid, TamingChannel> _tamingChannels = new();
    private readonly Dictionary<EntityUid, EntityUid> _tamingParticipants = new();

    private void InitializeTaming()
    {
        SubscribeLocalEvent<LearnedKnowledgeComponent, BookTamingActionEvent>(OnTamingAction);
        SubscribeLocalEvent<BookTamingLockComponent, BookCancelTamingActionEvent>(OnCancelTamingAction);
        SubscribeLocalEvent<BookTamingLockComponent, BookTamingDoAfterEvent>(OnTamingFinished);
        SubscribeLocalEvent<BookTamingLockComponent, MoveInputEvent>(OnTamingMoveInput);
        SubscribeLocalEvent<BookTamingLockComponent, DamageChangedEvent>(OnTamingHurt);
        // The shared lock system owns Shutdown to refresh predicted movement. Removal is a
        // separate native lifecycle event, raised before the component is deleted on either participant.
        SubscribeLocalEvent<BookTamingLockComponent, ComponentRemove>(OnTamingParticipantRemoved);
        SubscribeLocalEvent<DoAfterComponent, ComponentShutdown>(OnTamingDoAfterShutdown);
    }

    private bool IsFood(EntityUid uid) => HasComp<EdibleComponent>(uid) || HasComp<FoodComponent>(uid);

    private void OnTamingAction(EntityUid uid, LearnedKnowledgeComponent comp, BookTamingActionEvent args)
    {
        if (args.Handled || !Knows(uid, "BookTaming")) return;
        var food = HeldRightFirst(uid).Where(IsFood).Select(item => (EntityUid?) item).FirstOrDefault();
        if (food is not { } heldFood || !StartTaming(uid, args.Target, heldFood))
        {
            _popup.PopupEntity(Loc.GetString("book-portable-taming-requirements"), uid, uid);
            return;
        }
        // Native action handling starts UseDelay now, including if this channel is later cancelled.
        args.Handled = true;
    }

    private bool StartTaming(EntityUid user, EntityUid beast, EntityUid food)
    {
        if (!_hands.IsHolding(user, food, out _) || !IsFood(food) || !Knows(user, "BookTaming") ||
            !_companions.CanTame(beast, user) || _companions.OwnedCount(user) >= 1 ||
            !_interaction.InRangeUnobstructed(user, beast) || !_interaction.CanAccess(user, beast) ||
            !TryComp<DamageableComponent>(beast, out var damage) || damage.TotalDamage < 20 ||
            _tamingParticipants.ContainsKey(user) || _tamingParticipants.ContainsKey(beast))
            return false;

        var channel = new TamingChannel(user, beast);
        _tamingChannels.Add(user, channel);
        _tamingParticipants.Add(user, user);
        _tamingParticipants.Add(beast, user);
        AddComp<BookTamingLockComponent>(user);
        AddComp<BookTamingLockComponent>(beast);
        _physics.SetLinearVelocity(user, Vector2.Zero);
        _physics.SetLinearVelocity(beast, Vector2.Zero);
        _companions.Pacify(beast, TimeSpan.FromSeconds(35));
        _actions.AddAction(user, ref channel.CancelAction, "ActionBookCancelTaming");

        if (!_doAfter.TryStartDoAfter(new DoAfterArgs(EntityManager, user, 30,
                new BookTamingDoAfterEvent(), user, beast)
            {
                BreakOnMove = true,
                BreakOnDamage = true,
                NeedHand = true,
                BreakOnHandChange = false,
                BreakOnDropItem = false,
                RequireCanInteract = true,
                CancelDuplicate = false,
            }, out var id))
        {
            ReleaseTaming(user, false);
            return false;
        }
        channel.DoAfter = id;
        QueueDel(food);
        if (_tamingChannels.ContainsKey(user))
            _popup.PopupEntity(Loc.GetString("book-portable-taming-started"), user, user);
        return true;
    }

    private void OnCancelTamingAction(EntityUid uid, BookTamingLockComponent comp, BookCancelTamingActionEvent args)
    {
        if (args.Handled || !_tamingChannels.ContainsKey(uid)) return;
        args.Handled = true;
        CancelTaming(uid);
    }

    private void OnTamingMoveInput(EntityUid uid, BookTamingLockComponent comp, ref MoveInputEvent args)
    {
        // NPC navigation also produces movement inputs. Only the player's input is a voluntary cancellation.
        if (_tamingChannels.ContainsKey(uid) && args.HasDirectionalMovement)
            CancelTaming(uid);
    }

    private void OnTamingHurt(EntityUid uid, BookTamingLockComponent comp, DamageChangedEvent args)
    {
        if (args.DamageIncreased && _tamingParticipants.TryGetValue(uid, out var user))
            CancelTaming(user);
    }

    private void OnTamingParticipantRemoved(EntityUid uid, BookTamingLockComponent comp, ComponentRemove args)
    {
        if (_tamingParticipants.TryGetValue(uid, out var user))
            CancelTaming(user);
    }

    private void OnTamingDoAfterShutdown(EntityUid uid, DoAfterComponent comp, ComponentShutdown args)
    {
        if (_tamingChannels.ContainsKey(uid))
            CancelTaming(uid);
    }

    private void OnTamingFinished(EntityUid uid, BookTamingLockComponent comp, BookTamingDoAfterEvent args)
    {
        if (args.Handled || !_tamingChannels.TryGetValue(uid, out var channel)) return;
        args.Handled = true;
        var success = !args.Cancelled && Exists(channel.Beast) && Knows(uid, "BookTaming") &&
            _interaction.InRangeUnobstructed(uid, channel.Beast) && _companions.Tame(channel.Beast, uid);
        ReleaseTaming(uid, !success);
        if (success)
            _popup.PopupEntity(Loc.GetString("book-portable-taming-success"), uid, uid);
    }

    private void CancelTaming(EntityUid user)
    {
        if (!_tamingChannels.TryGetValue(user, out var channel)) return;
        // Release before cancelling: native DoAfter.Cancel raises its result synchronously.
        ReleaseTaming(user, true);
        if (_doAfter.IsRunning(channel.DoAfter))
            _doAfter.Cancel(channel.DoAfter);
    }

    private void ReleaseTaming(EntityUid user, bool retaliate)
    {
        if (!_tamingChannels.Remove(user, out var channel)) return;
        _tamingParticipants.Remove(channel.User);
        _tamingParticipants.Remove(channel.Beast);
        if (channel.CancelAction is { } action && !TerminatingOrDeleted(action))
        {
            _actions.RemoveAction(action);
            QueueDel(action);
        }
        foreach (var participant in new[] { channel.User, channel.Beast })
        {
            if (TryComp<BookTamingLockComponent>(participant, out var locked) && locked.LifeStage < ComponentLifeStage.Stopping)
                RemComp<BookTamingLockComponent>(participant);
        }
        if (!TerminatingOrDeleted(channel.Beast))
            _companions.ReleaseTraining(channel.Beast,
                retaliate && !TerminatingOrDeleted(user) ? user : null);
        if (retaliate && !TerminatingOrDeleted(user))
            _popup.PopupEntity(Loc.GetString("book-portable-taming-cancelled"), user, user);
    }

    private void CleanupTaming()
    {
        foreach (var channel in _tamingChannels.Values.ToArray())
        {
            ReleaseTaming(channel.User, false);
            if (_doAfter.IsRunning(channel.DoAfter))
                _doAfter.Cancel(channel.DoAfter);
        }
        _tamingParticipants.Clear();
    }
}
