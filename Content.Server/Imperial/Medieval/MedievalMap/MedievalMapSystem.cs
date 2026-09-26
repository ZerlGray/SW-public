using System.Linq;
using Content.Server.Imperial.Medieval.Knowledge;
using Content.Server.Imperial.Medieval.UserInterface;
using Content.Shared.ActionBlocker;
using Content.Shared.Examine;
using Content.Shared.GameTicking;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Imperial.Medieval.CartographerTable;
using Content.Shared.Imperial.Medieval.Knowledge;
using Content.Shared.Imperial.Medieval.MedievalMap;
using Content.Shared.Interaction;
using Content.Shared.Interaction.Events;
using Content.Shared.Paper;
using Content.Shared.Verbs;
using Content.Shared.Tag;
using Robust.Server.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;

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

    private sealed record Survey(EntityUid User, MedievalMapAnnotation Annotation);
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
        if (survey != null && (!Exists(survey.User) || !component.Annotations.Contains(survey.Annotation)))
        {
            _surveys.Remove(map);
            survey = null;
        }
        var geography = GetStoredGeography(component);
        var state = new MedievalMapBoundUiState
        {
            Size = component.Size,
            MapTexturePath = component.MapTexturePath,
            IsSurveyMap = component.IsSurveyMap,
            Annotations = new(component.Annotations),
            Surveyor = survey == null ? null : GetNetEntity(survey.User),
            PendingDescription = survey?.Annotation.Description ?? "",
            PendingAnnotationIndex = survey == null ? -1 : component.Annotations.IndexOf(survey.Annotation),
            Geography = geography,
            DisplayedWorldMap = geography == null ? null : (int) Transform(component.SurveyedMap!.Value).MapID
        };
        _userInterfaceSystem.SetUiState(map, MedievalMapUIKey.Key, state);
    }

    public bool CanSurvey(EntityUid user, EntityUid source)
    {
        if (!CanWriteWithPen(user, source)) return false;
        if (TryComp<MedievalMapComponent>(source, out var component))
        {
            return component.IsSurveyMap && component.SurveyedMap == Transform(user).MapUid &&
                   component.Annotations.Count < MaximumAnnotations;
        }
        return IsBlankPaper(source);
    }

    private bool CanWriteOnMap(EntityUid user, EntityUid map)
    {
        return CanWriteWithPen(user, map) && TryComp<MedievalMapComponent>(map, out var component) &&
               component.IsSurveyMap && component.SurveyedMap == Transform(user).MapUid;
    }

    private bool IsBlankPaper(EntityUid source)
    {
        return TryComp<PaperComponent>(source, out var paper) && _tags.HasTag(source, "Paper") &&
               !_tags.HasTag(source, "Book") && !HasComp<LearnableBookComponent>(source) &&
               !HasComp<BookManuscriptComponent>(source) && !paper.EditingDisabled &&
               string.IsNullOrWhiteSpace(paper.Content) && paper.StampedBy.Count == 0 && paper.StampState == null;
    }

    private bool CanWriteWithPen(EntityUid user, EntityUid map)
    {
        return Exists(user) && Exists(map) &&
               _knowledge.HasKnowledge(user, "BookCartography") &&
               _blocker.CanInteract(user, map) &&
               _hands.IsHolding(user, map, out _) &&
               _hands.EnumerateHeld(user).Any(item => item != map && _tags.HasTag(item, "Write")) &&
               Transform(user).MapID != MapId.Nullspace && Transform(user).MapUid != null;
    }

    private MedievalCartographerBoundUserInterfaceState? GetStoredGeography(MedievalMapComponent component)
    {
        if (!component.IsSurveyMap || component.SurveyedMap is not { } world || !Exists(world) ||
            !HasComp<MapComponent>(world) || Transform(world).MapID == MapId.Nullspace)
            return null;

        return new MedievalCartographerBoundUserInterfaceState(
            GetNetCoordinates(new EntityCoordinates(world, component.SurveyCenter)), Angle.Zero,
            component.SurveyRange, false, new());
    }

    private MedievalCartographerBoundUserInterfaceState? GetGeography(EntityUid reference)
    {
        var xform = Transform(reference);
        if (xform.MapID == MapId.Nullspace || xform.MapUid is not { } world)
            return null;

        var center = _transform.GetMapCoordinates(reference).Position;
        var range = 256f;
        // Prefer the terrain grid rather than a small boat the map's reader may be aboard.
        var gridUid = HasComp<MapGridComponent>(world) ? world : xform.GridUid;
        if (TryComp<MapGridComponent>(gridUid, out var grid))
        {
            var bounds = _transform.GetWorldMatrix(gridUid!.Value).TransformBox(grid.LocalAABB);
            if (float.IsFinite(bounds.Center.X) && float.IsFinite(bounds.Center.Y) &&
                float.IsFinite(bounds.Width) && float.IsFinite(bounds.Height) && bounds.Width > 0 && bounds.Height > 0)
            {
                center = bounds.Center;
                range = Math.Clamp(MathF.Max(bounds.Width, bounds.Height) * 0.5f + 8f, 32f, 2048f);
            }
        }

        // Reuse the cartographic table's geometry renderer, without its live radar contacts.
        return new MedievalCartographerBoundUserInterfaceState(
            GetNetCoordinates(new EntityCoordinates(world, center)), Angle.Zero, range, false, new());
    }

    /// <summary>Called after completing the survey action; never accepts client-supplied world data.</summary>
    public bool BeginSurvey(EntityUid user, EntityUid source)
    {
        if (!CanSurvey(user, source)) return false;
        var map = source;
        if (!TryComp<MedievalMapComponent>(map, out var component))
        {
            var geography = GetGeography(user);
            if (geography == null || !_hands.IsHolding(user, source, out var hand)) return false;
            map = Spawn("MedievalSurveyMap", Transform(user).Coordinates);
            component = Comp<MedievalMapComponent>(map);
            component.SurveyedMap = Transform(user).MapUid;
            component.SurveyCenter = geography.Coordinates.Position;
            component.SurveyRange = geography.MaxRange;

            // Replace only after the new map can occupy the sheet's hand; keep the sheet on failure.
            if (!_hands.TryDrop(user, source, doDropInteraction: false))
            {
                Del(map);
                return false;
            }
            if (!_hands.TryPickup(user, map, hand, animate: false))
            {
                _hands.TryPickup(user, source, hand, checkActionBlocker: false, animate: false);
                Del(map);
                return false;
            }
            Del(source);
        }
        var position = _transform.GetMapCoordinates(user);
        var landmarks = _lookup.GetEntitiesInRange(Transform(user).Coordinates, 6f)
            .Where(entity => entity != user && Transform(entity).Anchored &&
                             _examine.InRangeUnOccluded(user, entity, 6f))
            .OrderBy(entity => (_transform.GetWorldPosition(entity) - position.Position).LengthSquared())
            .Select(entity => Name(entity)).Distinct().Take(8).ToArray();
        var description = Loc.GetString("book-cartography-observation",
            ("x", (int) position.X), ("y", (int) position.Y),
            ("landmarks", landmarks.Length == 0 ? Loc.GetString("book-cartography-no-landmarks") : string.Join(", ", landmarks)));
        var annotation = new MedievalMapAnnotation
        {
            WorldPosition = position.Position,
            WorldMap = (int) position.MapId,
            Title = Loc.GetString("book-cartography-default-title", ("number", component.Annotations.Count + 1)),
            Description = description
        };
        component.Annotations.Add(annotation);
        _surveys[map] = new Survey(user, annotation);
        OpenMap(map, component, user);
        return true;
    }

    public bool TryRenameSurveyAnnotation(EntityUid user, EntityUid map, string title)
    {
        if (!_surveys.TryGetValue(map, out var survey) || survey.User != user || !CanWriteOnMap(user, map))
            return false;

        var component = Comp<MedievalMapComponent>(map);
        if (!component.Annotations.Contains(survey.Annotation)) return false;
        title = (title ?? string.Empty).Trim();
        if (title.Length > 64) title = title[..64];
        if (title.Length != 0) survey.Annotation.Title = title;
        _surveys.Remove(map);
        UpdateMap(map, component);
        return true;
    }

    private void OnAnnotate(EntityUid uid, MedievalMapComponent component, MedievalMapAnnotateMessage args)
    {
        TryRenameSurveyAnnotation(args.Actor, uid, args.Title);
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
