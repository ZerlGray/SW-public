using System.Linq;
using System.Numerics;
using Content.Server.Imperial.Medieval.Knowledge;
using Content.Server.Imperial.Medieval.UserInterface;
using Content.Shared.ActionBlocker;
using Content.Shared.Examine;
using Content.Shared.GameTicking;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Imperial.Medieval.MedievalMap;
using Content.Shared.Interaction;
using Content.Shared.Interaction.Events;
using Content.Shared.Verbs;
using Content.Shared.Tag;
using Robust.Server.GameObjects;
using Robust.Shared.Map;

namespace Content.Server.Imperial.Medieval.MedievalMap;


public sealed partial class MedievalMapSystem : EntitySystem
{
    [Dependency] private readonly UserInterfaceSystem _userInterfaceSystem = default!;
    [Dependency] private readonly MedievalKnowledgeSystem _knowledge = default!;
    [Dependency] private readonly SharedHandsSystem _hands = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly ExamineSystemShared _examine = default!;
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly TagSystem _tags = default!;
    [Dependency] private readonly ActionBlockerSystem _blocker = default!;
    [Dependency] private readonly MedievalUserInterfaceRateLimitSystem _uiRateLimit = default!;

    private sealed record Survey(EntityUid User, Vector2 WorldPosition, int WorldMap, string Description);
    private readonly Dictionary<EntityUid, Survey> _surveys = new();
    public const int MaximumAnnotations = 24;


    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<MedievalMapComponent, UseInHandEvent>(OnUseInHand);
        SubscribeLocalEvent<MedievalMapComponent, ActivateInWorldEvent>(OnActiveInWorld);
        SubscribeLocalEvent<MedievalMapComponent, GetVerbsEvent<AlternativeVerb>>(OnGetVerb);
        Subs.BuiEvents<MedievalMapComponent>(MedievalMapUIKey.Key, subs =>
        {
            subs.Event<MedievalMapAnnotateMessage>(OnAnnotate);
            subs.Event<BoundUIClosedEvent>(OnClosed);
        });
        SubscribeLocalEvent<MedievalMapComponent, ComponentShutdown>(OnShutdown);
        SubscribeLocalEvent<RoundRestartCleanupEvent>(_ => _surveys.Clear());
        _uiRateLimit.Register<MedievalMapComponent>("MedievalMapUi");
    }

    private void OnUseInHand(EntityUid uid, MedievalMapComponent component, UseInHandEvent args)
    {
        if (args.Handled) return;
        OpenMap(uid, component, args.User);
        args.Handled = true;
    }

    private void OnActiveInWorld(EntityUid uid, MedievalMapComponent component, ActivateInWorldEvent args)
    {
        if (args.Handled) return;
        OpenMap(uid, component, args.User);
        args.Handled = true;
    }

    private void OnGetVerb(EntityUid uid, MedievalMapComponent component, GetVerbsEvent<AlternativeVerb> args)
    {
        if (!args.CanAccess || !args.CanInteract) return;
        args.Verbs.Add(new AlternativeVerb()
        {
            Act = () =>
            {
                OpenMap(uid, component, args.User);
            },
            Text = Loc.GetString(component.OpenMapText),
            Priority = 3,
        });
    }

    #region Helpers

    private void OpenMap(EntityUid map, MedievalMapComponent component, EntityUid opener)
    {
        _userInterfaceSystem.TryOpenUi(map, MedievalMapUIKey.Key, opener);
        UpdateMap(map, component);
    }

    private void UpdateMap(EntityUid map, MedievalMapComponent component)
    {
        _surveys.TryGetValue(map, out var survey);
        if (survey != null && !Exists(survey.User))
        {
            _surveys.Remove(map);
            survey = null;
        }
        var state = new MedievalMapBoundUiState
        {
            Size = component.Size,
            MapTexturePath = component.MapTexturePath,
            Annotations = new(component.Annotations),
            Surveyor = survey == null ? null : GetNetEntity(survey.User),
            PendingDescription = survey?.Description ?? ""
        };
        _userInterfaceSystem.SetUiState(map, MedievalMapUIKey.Key, state);
    }

    public bool CanSurvey(EntityUid user, EntityUid map)
    {
        return TryComp<MedievalMapComponent>(map, out var component) &&
               component.Annotations.Count < MaximumAnnotations &&
               _knowledge.HasKnowledge(user, "BookCartography") &&
               _blocker.CanInteract(user, map) &&
               _hands.IsHolding(user, map, out _) &&
               _hands.EnumerateHeld(user).Any(item => item != map && _tags.HasTag(item, "Write")) &&
               Transform(user).MapID != MapId.Nullspace;
    }

    /// <summary>Called after completing the survey action; never accepts client-supplied world data.</summary>
    public bool BeginSurvey(EntityUid user, EntityUid map)
    {
        if (!CanSurvey(user, map)) return false;
        var position = _transform.GetMapCoordinates(user);
        var landmarks = _lookup.GetEntitiesInRange(Transform(user).Coordinates, 6f)
            .Where(entity => entity != user && Transform(entity).Anchored &&
                             _examine.InRangeUnOccluded(user, entity, 6f))
            .OrderBy(entity => (_transform.GetWorldPosition(entity) - position.Position).LengthSquared())
            .Select(entity => Name(entity)).Distinct().Take(8).ToArray();
        var description = Loc.GetString("book-cartography-observation",
            ("x", (int) position.X), ("y", (int) position.Y),
            ("landmarks", landmarks.Length == 0 ? Loc.GetString("book-cartography-no-landmarks") : string.Join(", ", landmarks)));
        _surveys[map] = new Survey(user, position.Position, (int) position.MapId, description);
        OpenMap(map, Comp<MedievalMapComponent>(map), user);
        return true;
    }

    public bool TryPlaceAnnotation(EntityUid user, EntityUid map, Vector2 position, string title)
    {
        if (!_surveys.TryGetValue(map, out var survey) || survey.User != user || !CanSurvey(user, map) ||
            !float.IsFinite(position.X) || !float.IsFinite(position.Y) ||
            position.X < 0 || position.X > 1 || position.Y < 0 || position.Y > 1)
            return false;

        var component = Comp<MedievalMapComponent>(map);
        title = (title ?? string.Empty).Trim();
        if (title.Length > 64) title = title[..64];
        if (title.Length == 0) title = Loc.GetString("book-cartography-default-title", ("number", component.Annotations.Count + 1));
        component.Annotations.Add(new MedievalMapAnnotation
        {
            Position = position,
            WorldPosition = survey.WorldPosition,
            WorldMap = survey.WorldMap,
            Title = title,
            Description = survey.Description
        });
        _surveys.Remove(map);
        UpdateMap(map, component);
        return true;
    }

    private void OnAnnotate(EntityUid uid, MedievalMapComponent component, MedievalMapAnnotateMessage args)
    {
        TryPlaceAnnotation(args.Actor, uid, args.Position, args.Title);
    }

    private void OnClosed(EntityUid uid, MedievalMapComponent component, BoundUIClosedEvent args)
    {
        if (_surveys.TryGetValue(uid, out var survey) && survey.User == args.Actor)
        {
            _surveys.Remove(uid);
            UpdateMap(uid, component);
        }
    }

    private void OnShutdown(EntityUid uid, MedievalMapComponent component, ComponentShutdown args)
    {
        _surveys.Remove(uid);
    }

    #endregion
}
