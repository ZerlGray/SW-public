using Content.Shared.Administration.Logs;
using Content.Shared.ActionBlocker;
using Content.Shared.Database;
using Content.Shared.IdentityManagement;
using Content.Shared.Imperial.Medieval.Factions;
using Content.Shared.Imperial.Medieval.IdentityManagement;
using Content.Shared.Interaction;
using Content.Shared.Mind;
using Content.Shared.Mind.Components;
using Content.Shared.Players;
using Content.Shared.Popups;
using Robust.Server.Player;
using Robust.Shared.Player;

namespace Content.Server.Imperial.Medieval.IdentityManagement;

public sealed class MedievalIdentitySystem : SharedMedievalIdentitySystem
{
    [Dependency] private readonly ISharedAdminLogManager _adminLogger = default!;
    [Dependency] private readonly SharedMindSystem _mindSystem = default!;
    [Dependency] private readonly IPlayerManager _playerManager = default!;
    [Dependency] private readonly SharedPopupSystem _popup = default!;
    [Dependency] private readonly SharedInteractionSystem _interaction = default!;
    [Dependency] private readonly ActionBlockerSystem _actionBlocker = default!;

    private int _nextId = 1;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<IdentityRequiresKnowledgeComponent, ComponentInit>(OnComponentInit, before: new[] { typeof(SharedMedievalFactionsSystem) });
        SubscribeLocalEvent<IdentityRequiresKnowledgeComponent, PlayerAttachedEvent>(OnPlayerAttached);
        SubscribeNetworkEvent<MedievalIntroduceIdentityRequest>(OnIntroduceIdentity);
    }

    private void OnIntroduceIdentity(MedievalIntroduceIdentityRequest request, EntitySessionEventArgs args)
    {
        if (args.SenderSession.AttachedEntity is not { } introducer ||
            !TryGetEntity(request.Observer, out var observer))
        {
            return;
        }

        Introduce(introducer, observer.Value);
    }

    public bool Introduce(EntityUid introducer, EntityUid observer)
    {
        if (!_actionBlocker.CanInteract(introducer, observer) ||
            !_interaction.InRangeAndAccessible(introducer, observer) ||
            !TryComp<IdentityRequiresKnowledgeComponent>(observer, out var observerComp) ||
            !CanIntroduce(introducer, observer, observerComp) ||
            !TryComp<IdentityRequiresKnowledgeComponent>(introducer, out var introducerComp))
        {
            return false;
        }

        observerComp.KnownIds.Add(introducerComp.Identifier);
        Dirty(observer, observerComp);

        var introducerName = Identity.Name(introducer, EntityManager, introducer);
        _popup.PopupEntity(
            Loc.GetString("imperial-hm-identity-introduce", ("name", introducerName)),
            introducer,
            introducer);
        _popup.PopupEntity(
            Loc.GetString("imperial-hm-identity-introduction", ("name", introducerName)),
            introducer,
            observer);

        return true;
    }

    public bool IntroduceSilently(EntityUid introducer, EntityUid observer)
    {
        if (!TryComp<IdentityRequiresKnowledgeComponent>(observer, out var observerComp) ||
            !CanIntroduce(introducer, observer, observerComp) ||
            !TryComp<IdentityRequiresKnowledgeComponent>(introducer, out var introducerComp))
        {
            return false;
        }

        observerComp.KnownIds.Add(introducerComp.Identifier);
        Dirty(observer, observerComp);
        return true;
    }

    private void OnComponentInit(EntityUid uid, IdentityRequiresKnowledgeComponent component, ComponentInit args)
    {
        component.Identifier = _nextId;
        _nextId++;
        Dirty(uid, component);
    }

    private void OnPlayerAttached(EntityUid uid, IdentityRequiresKnowledgeComponent component, PlayerAttachedEvent args)
    {
        if (!_playerManager.TryGetSessionByEntity(uid, out var session))
            return;
        var mindUid = session.GetMind();
        if (!TryComp<MindComponent>(mindUid, out var mind))
            return;
        _adminLogger.Add(LogType.EventRan, LogImpact.Low, $"Player {session.Name} attached to entity with identity id: {component.Identifier}");
    }
}
