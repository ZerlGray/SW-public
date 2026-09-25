using System.Linq;
using System.Numerics;
using Content.Server.MagicBarrier.Components;
using Content.Server.MedievalPasport.Components;
using Content.Server.Imperial.Medieval.Knowledge;
using Content.Shared.Clothing;
using Content.Shared.Clothing.Components;
using Content.Shared.Clothing.EntitySystems;
using Content.Shared.Chat.Prototypes;
using Content.Shared.DoAfter;
using Content.Shared.Examine;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Humanoid;
using Content.Shared.IdentityManagement;
using Content.Shared.Imperial.Medieval.Factions.Components;
using Content.Shared.Imperial.Medieval.IdentityManagement;
using Content.Shared.Imperial.Medieval.Rituals;
using Content.Shared.Imperial.TTS;
using Content.Shared.Interaction;
using Content.Shared.Inventory;
using Content.Shared.Item;
using Content.Shared.Mobs.Components;
using Content.Shared.Mobs.Systems;
using Content.Shared.Imperial.Medieval.Knowledge;
using Content.Shared.Paper;
using Content.Shared.Popups;
using Content.Shared.Stacks;
using Content.Shared.Speech;
using Content.Shared.Speech.Components;
using Content.Shared.Storage;
using Content.Shared.Verbs;
using Robust.Shared.Containers;
using Robust.Shared.Audio;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics.Components;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization.Manager;
using Robust.Shared.Timing;

namespace Content.Server.Imperial.Medieval.Rituals;

public sealed partial class MedievalZaygoSystem : EntitySystem
{
    [Dependency] private readonly MedievalKnowledgeSystem _knowledge = default!;
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly SharedContainerSystem _containers = default!;
    [Dependency] private readonly SharedHandsSystem _hands = default!;
    [Dependency] private readonly SharedDoAfterSystem _doAfter = default!;
    [Dependency] private readonly SharedInteractionSystem _interaction = default!;
    [Dependency] private readonly SharedPopupSystem _popup = default!;
    [Dependency] private readonly SharedStackSystem _stacks = default!;
    [Dependency] private readonly InventorySystem _inventory = default!;
    [Dependency] private readonly SharedItemSystem _items = default!;
    [Dependency] private readonly ClothingSystem _clothing = default!;
    [Dependency] private readonly MetaDataSystem _metadata = default!;
    [Dependency] private readonly ISerializationManager _serialization = default!;
    [Dependency] private readonly IGameTiming _time = default!;
    [Dependency] private readonly MobStateSystem _mobs = default!;
    [Dependency] private readonly IdentitySystem _identities = default!;

    public override void Initialize()
    {
        SubscribeLocalEvent<MedievalRitualValidateEvent>(Validate);
        SubscribeLocalEvent<MedievalRitualExecuteEvent>(Execute);
        SubscribeLocalEvent<ZaygoWaxMaskComponent, AfterInteractEvent>(OnCapture);
        SubscribeLocalEvent<ZaygoWaxMaskComponent, ZaygoCaptureDoAfterEvent>(OnCaptured);
        SubscribeLocalEvent<ZaygoDisguiseComponent, GetVerbsEvent<AlternativeVerb>>(OnRemoveDisguise);
        SubscribeLocalEvent<ZaygoTemporaryDocumentComponent, ExaminedEvent>(OnDocumentExamined);
    }

    private void Validate(MedievalRitualValidateEvent args)
    {
        var context = args.Context;
        if (!context.Ritual.Effect.StartsWith("Zaygo", StringComparison.Ordinal)) return;
        args.Handled = true;
        switch (context.Ritual.Effect)
        {
            case "ZaygoTheft2":
                if (!context.Participants.Contains(context.Target) || !_mobs.IsAlive(context.Target))
                    args.Error = "medieval-ritual-target-participant";
                break;
            case "ZaygoIdentity3":
                var mask = context.Offerings.FirstOrDefault(HasComp<ZaygoCapturedIdentityComponent>);
                if (!mask.IsValid() || !HasComp<HumanoidAppearanceComponent>(context.Target) || !context.Participants.Contains(context.Target) || !_mobs.IsAlive(context.Target))
                    args.Error = "medieval-zaygo-need-likeness";
                else
                {
                    var captured = Comp<ZaygoCapturedIdentityComponent>(mask);
                    var candidates = context.ReservedObjects.Count > 0 ? context.ReservedObjects :
                        _lookup.GetEntitiesInRange(_transform.GetMapCoordinates(context.Center), 3);
                    var paperCarriers = candidates.Where(e => !context.Offerings.Contains(e) && !HasComp<LearnableBookComponent>(e) &&
                        Transform(e).ParentUid == Transform(context.Center).ParentUid && !Transform(e).Anchored &&
                        TryComp<PaperComponent>(e, out var paper) && string.IsNullOrEmpty(paper.Content) && !paper.EditingDisabled &&
                        (!TryComp<MedievalRitualOfferingReservedComponent>(e, out var reservation) || reservation.Center == context.Center))
                        .OrderBy(e => e.Id).Take(captured.Documents.Count).ToArray();
                    if (paperCarriers.Length < captured.Documents.Count)
                    { args.Error = "medieval-zaygo-need-paper"; break; }
                    context.Data["mask"] = mask;
                    context.Data["papers"] = paperCarriers;
                    context.ReservedObjects.UnionWith(paperCarriers);
                }
                break;
            case "ZaygoPlace4":
                if (!TryComp<ZaygoTheftMarkerComponent>(context.Target, out var marker) || marker.Owner != context.Caster)
                { args.Error = "medieval-zaygo-need-marker"; break; }
                var kinds = context.Offerings.Where(HasComp<RitualOfferingComponent>)
                    .Select(uid => Comp<RitualOfferingComponent>(uid).Kind).ToHashSet();
                if (kinds.Count == 0) { args.Error = "medieval-zaygo-need-categories"; break; }
                var exchange = BuildExchange(context, kinds);
                if (exchange.Count == 0) args.Error = "medieval-zaygo-empty-area";
                else context.Data["exchange"] = exchange;
                break;
            default: args.Handled = false; break;
        }
    }

    private void Execute(MedievalRitualExecuteEvent args)
    {
        var context = args.Context;
        switch (context.Ritual.Effect)
        {
            case "ZaygoTheft2":
                EnsureComp<ZaygoTheftBlessingComponent>(context.Target).Charges = 1;
                break;
            case "ZaygoIdentity3":
                if (context.Data.TryGetValue("mask", out var value) && value is EntityUid mask &&
                    TryComp<ZaygoCapturedIdentityComponent>(mask, out var captured))
                    ApplyIdentity(context.Target, captured, (EntityUid[])context.Data["papers"]);
                break;
            case "ZaygoPlace4":
                if (context.Data.TryGetValue("exchange", out var plan) && plan is List<ExchangeMove> moves)
                {
                    // All decisions are made before moving anything. Containers move with contents.
                    foreach (var move in moves.Where(m => m.Extract))
                        if (Exists(move.Entity) && _containers.TryGetContainingContainer(move.Entity, out var container))
                            _containers.Remove(move.Entity, container, force: true);
                    foreach (var move in moves)
                        if (Exists(move.Entity) && Transform(move.Entity).Anchored) _transform.Unanchor(move.Entity);
                    foreach (var move in moves)
                    {
                        if (!Exists(move.Entity)) continue;
                        _transform.SetMapCoordinates(move.Entity, move.Destination);
                    }
                    foreach (var move in moves)
                        if (move.Anchored && Exists(move.Entity)) _transform.AnchorEntity(move.Entity);
                    QueueDel(context.Target);
                }
                break;
        }
    }

    private List<ExchangeMove> BuildExchange(MedievalRitualContext context, HashSet<string> kinds)
    {
        var source = _transform.GetMapCoordinates(context.Target);
        var destination = _transform.GetMapCoordinates(context.Center);
        var radius = (int) context.Ritual.Radius;
        var sourceCell = source.Position.Floored();
        var mapBridge = source.MapId == destination.MapId ? Vector2i.Zero : new Vector2i(1000000, 0);
        var destinationCell = destination.Position.Floored() + mapBridge;
        var all = _lookup.GetEntitiesInRange(source, radius + 1, LookupFlags.All)
            .Union(_lookup.GetEntitiesInRange(destination, radius + 1, LookupFlags.All)).ToHashSet();
        var bodies = new List<RitualTheftPlanner.Body>();
        var positions = new Dictionary<int, MapCoordinates>();
        var anchored = new Dictionary<int, bool>();
        var extractions = new Dictionary<EntityUid, EntityUid>();
        foreach (var uid in all.OrderBy(uid => uid.Id))
        {
            if (uid == context.Target || uid == context.Center || context.Offerings.Contains(uid) ||
                HasComp<MapComponent>(uid) || HasComp<MapGridComponent>(uid) ||
                !TryComp<TransformComponent>(uid, out var xform) ||
                _containers.TryGetContainingContainer((uid, xform, null), out _)) continue;
            if (!HasComp<ItemComponent>(uid) && !HasComp<MobStateComponent>(uid) && !HasComp<PhysicsComponent>(uid)) continue;
            var position = _transform.GetMapCoordinates(uid);
            var isSource = position.MapId == source.MapId && Vector2.DistanceSquared(position.Position, source.Position) <= radius * radius;
            var category = Category(uid);
            var protectedObject = IsProtected(uid) || ContainsProtected(uid);
            var selected = isSource && category != null && kinds.Contains(category) && !protectedObject;
            if (selected)
                foreach (var excluded in ExcludedDescendants(uid, kinds)) extractions[excluded] = uid;
            var solid = HasComp<MobStateComponent>(uid) || TryComp<PhysicsComponent>(uid, out var physics) && physics.CanCollide && physics.Hard;
            var cell = position.Position.Floored() + (position.MapId == source.MapId ? Vector2i.Zero : mapBridge);
            bodies.Add(new(uid.Id, cell, selected, solid, protectedObject,
                isSource && HasComp<MobStateComponent>(uid) && !selected));
            positions[uid.Id] = position;
            anchored[uid.Id] = xform.Anchored;
        }
        // Model excluded passengers as source inhabitants in the dry-run plan. A reverse
        // wall must find them a free source cell before their container can be stolen.
        foreach (var (passenger, _) in extractions)
        {
            if (positions.ContainsKey(passenger.Id)) continue;
            var position = _transform.GetMapCoordinates(passenger);
            bodies.Add(new(passenger.Id, position.Position.Floored(), false, true, false, true));
            positions[passenger.Id] = position;
            anchored[passenger.Id] = false;
        }
        var result = RitualTheftPlanner.Plan(bodies, destinationCell - sourceCell, sourceCell, radius);
        foreach (var (passenger, carrier) in extractions)
        {
            if (!result.ContainsKey(carrier.Id)) continue;
            // If no reverse obstruction displaced the passenger, the vacated source cell is safe.
            result.TryAdd(passenger.Id, positions[passenger.Id].Position.Floored());
        }
        var moves = new List<ExchangeMove>();
        foreach (var (id, cell) in result)
        {
            var old = positions[id];
            var targetMap = source.MapId;
            var targetCell = cell;
            if (mapBridge != Vector2i.Zero && cell.X > mapBridge.X / 2)
            { targetCell -= mapBridge; targetMap = destination.MapId; }
            var fraction = old.Position - (Vector2) old.Position.Floored();
            var entity = new EntityUid(id);
            moves.Add(new(entity, new MapCoordinates((Vector2) targetCell + fraction, targetMap), anchored[id],
                extractions.ContainsKey(entity)));
        }
        return moves;
    }

    private bool ContainsProtected(EntityUid root)
    {
        var children = Transform(root).ChildEnumerator;
        while (children.MoveNext(out var child))
        {
            if (IsProtected(child) || ContainsProtected(child))
                return true;
        }
        return false;
    }

    private IEnumerable<EntityUid> ExcludedDescendants(EntityUid root, HashSet<string> kinds)
    {
        var children = Transform(root).ChildEnumerator;
        while (children.MoveNext(out var child))
        {
            if (HasComp<MobStateComponent>(child) && !kinds.Contains(Category(child) ?? string.Empty))
            {
                yield return child; // Their worn and carried possessions remain with them.
                continue;
            }
            foreach (var descendant in ExcludedDescendants(child, kinds)) yield return descendant;
        }
    }

    private bool IsProtected(EntityUid uid)
    {
        if (HasComp<RitualTransportProtectedComponent>(uid) || HasComp<MagicBarrierComponent>(uid) ||
            HasComp<MapComponent>(uid) || HasComp<MapGridComponent>(uid) || HasComp<MedievalRitualCenterComponent>(uid)) return true;
        var id = MetaData(uid).EntityPrototype?.ID ?? string.Empty;
        return id.Contains("WallRock", StringComparison.OrdinalIgnoreCase) ||
               id.Contains("Unbreakable", StringComparison.OrdinalIgnoreCase) ||
               id.Contains("Floor", StringComparison.OrdinalIgnoreCase) || id.Contains("Spawner", StringComparison.OrdinalIgnoreCase) ||
               id.Contains("SpawnPoint", StringComparison.OrdinalIgnoreCase) || id.Contains("BarrierMagic", StringComparison.OrdinalIgnoreCase);
    }

    private string? Category(EntityUid uid)
    {
        if (HasComp<RitualAnimatedComponent>(uid) && HasComp<StorageComponent>(uid)) return "furniture";
        if (HasComp<MobStateComponent>(uid)) return HasComp<HumanoidAppearanceComponent>(uid) ? "people" : "beasts";
        var id = MetaData(uid).EntityPrototype?.ID ?? string.Empty;
        if (new[] { "Wall", "Window", "Door", "Airlock", "Fence", "Palisade" }.Any(part => id.Contains(part, StringComparison.OrdinalIgnoreCase))) return "structures";
        if (HasComp<StorageComponent>(uid) || Transform(uid).Anchored) return "furniture";
        return HasComp<ItemComponent>(uid) ? "goods" : null;
    }

    /// <summary>Called by the shared offering menu's single ItemComponent verb subscription.</summary>
    public void AddPreparationVerbs(EntityUid uid, ItemComponent item, GetVerbsEvent<AlternativeVerb> args)
    {
        if (!args.CanAccess || !args.CanInteract) return;
        var user = args.User;
        var id = MetaData(uid).EntityPrototype?.ID ?? string.Empty;
        if (_knowledge.HasKnowledge(user, "RitualZaygoPlace4") && id.Contains("Revent", StringComparison.OrdinalIgnoreCase))
            args.Verbs.Add(new AlternativeVerb { Text = Loc.GetString("medieval-zaygo-make-marker"), Act = () =>
            {
                if (!Exists(uid) || !Exists(user) || !_knowledge.HasKnowledge(user, "RitualZaygoPlace4") ||
                    !_interaction.InRangeUnobstructed(user, uid)) return;
                var coordinates = Transform(user).Coordinates;
                ConsumeOne(uid);
                var coin = Spawn("MedievalRitualTheftCoin", coordinates);
                Comp<ZaygoTheftMarkerComponent>(coin).Owner = user;
                _hands.TryPickupAnyHand(user, coin);
            }});
        if (_knowledge.HasKnowledge(user, "RitualZaygoIdentity3") && TryComp<PaperComponent>(uid, out var paper) && string.IsNullOrEmpty(paper.Content))
            args.Verbs.Add(new AlternativeVerb { Text = Loc.GetString("medieval-zaygo-make-mask"), Act = () =>
            {
                if (!Exists(uid) || !Exists(user) || !_knowledge.HasKnowledge(user, "RitualZaygoIdentity3") ||
                    !_interaction.InRangeUnobstructed(user, uid) || !TryComp<PaperComponent>(uid, out var currentPaper) ||
                    !string.IsNullOrEmpty(currentPaper.Content) || HasComp<LearnableBookComponent>(uid)) return;
                var mask = Spawn("MedievalRitualWaxMask", Transform(user).Coordinates);
                QueueDel(uid);
                _hands.TryPickupAnyHand(user, mask);
            }});
    }

    private void ConsumeOne(EntityUid uid)
    {
        if (TryComp<StackComponent>(uid, out var stack)) _stacks.Use(uid, 1, stack);
        else QueueDel(uid);
    }

    private void OnCapture(EntityUid uid, ZaygoWaxMaskComponent component, AfterInteractEvent args)
    {
        if (args.Handled || !args.CanReach || args.Target is not {} target ||
            !HasComp<HumanoidAppearanceComponent>(target) || !_knowledge.HasKnowledge(args.User, "RitualZaygoIdentity3")) return;
        args.Handled = _doAfter.TryStartDoAfter(new DoAfterArgs(EntityManager, args.User, 5,
            new ZaygoCaptureDoAfterEvent(), uid, target: target, used: uid)
        { BreakOnDamage = true, BreakOnMove = true, NeedHand = true, Hidden = true });
    }

    private void OnCaptured(EntityUid uid, ZaygoWaxMaskComponent component, ZaygoCaptureDoAfterEvent args)
    {
        if (args.Cancelled || args.Handled || args.Target is not {} target ||
            !TryComp<HumanoidAppearanceComponent>(target, out var appearance) ||
            !_knowledge.HasKnowledge(args.User, "RitualZaygoIdentity3")) return;
        args.Handled = true;
        var captured = EnsureComp<ZaygoCapturedIdentityComponent>(uid);
        captured.DisplayName = Identity.Name(target, EntityManager);
        captured.Visual = TryComp<ZaygoDisguiseComponent>(target, out var apparent)
            ? _serialization.CreateCopy(apparent.Visual, notNullableOverride: true) : ZaygoVisualSnapshot.Capture(appearance);
        if (TryComp<IdentityRequiresKnowledgeComponent>(target, out var identity))
        { captured.Identifier = identity.Identifier; captured.HideUnknown = identity.HideUnknown; }
        if (apparent != null) {captured.Identifier = apparent.Identifier; captured.HideUnknown = apparent.HideUnknown;}
        captured.Voice = TryComp<TTSProviderComponent>(target, out var voice) ? voice.Voice : null;
        captured.Speech = CaptureSpeech(target);
        captured.Vocal = CaptureVocal(target);
        captured.Faction = TryComp<MedievalFactionMemberComponent>(target, out var faction) ? faction.Faction.Id : null;
        captured.Leader = faction?.MenuAccess == FactionMenuAccess.Full;
        captured.Attacked = faction?.AttackedFactions.Select(p => p.Id).ToList() ?? new();
        if (apparent != null)
        {captured.Faction = apparent.Faction; captured.Leader = apparent.FactionLeader; captured.Attacked = new(apparent.AttackedFactions);}
        captured.Clothes.Clear(); captured.Documents.Clear();
        if (_inventory.TryGetSlots(target, out var slots))
            foreach (var slot in slots)
                if (_inventory.TryGetSlotEntity(target, slot.Name, out var worn))
                {
                    if (TryComp<ClothingComponent>(worn, out var cloth))
                        captured.Clothes[slot.Name] = CaptureItem(worn.Value, cloth);
                    CaptureDocuments(worn.Value, captured.Documents);
                }
        _metadata.SetEntityName(uid, Loc.GetString("medieval-zaygo-captured-mask", ("name", captured.DisplayName)));
        EnsureComp<RitualOfferingComponent>(uid).Kind = "mask";
        _popup.PopupEntity(Loc.GetString("medieval-zaygo-captured"), uid, args.User);
    }

    private ItemLikeness CaptureItem(EntityUid uid, ClothingComponent? clothing = null) => new()
    {
        Name = Name(uid), Description = Description(uid), Prototype = TryComp<ZaygoItemAppearanceComponent>(uid, out var facade) ? facade.Prototype.Id : MetaData(uid).EntityPrototype?.ID,
        Clothing = clothing == null ? null : _serialization.CreateCopy(clothing, notNullableOverride: true),
        Item = TryComp<ItemComponent>(uid, out var item) ? _serialization.CreateCopy(item, notNullableOverride: true) : null,
    };

    private void CaptureDocuments(EntityUid root, List<DocumentLikeness> documents)
    {
        if (TryComp<MedievalPasportComponent>(root, out var passport))
            documents.Add(new(Name(root), Description(root), null, _serialization.CreateCopy(passport, notNullableOverride: true), MetaData(root).EntityPrototype?.ID));
        else if (TryComp<PaperComponent>(root, out var paper))
            documents.Add(new(Name(root), Description(root), paper.Content, null, MetaData(root).EntityPrototype?.ID));
        var children = Transform(root).ChildEnumerator;
        while (children.MoveNext(out var child)) CaptureDocuments(child, documents);
    }

    private void ApplyIdentity(EntityUid target, ZaygoCapturedIdentityComponent captured, IReadOnlyList<EntityUid> papers)
    {
        RemoveIdentity(target);
        var guise = EnsureComp<ZaygoDisguiseComponent>(target);
        guise.DisplayName = captured.DisplayName; guise.Identifier = captured.Identifier;
        guise.HideUnknown = captured.HideUnknown; guise.Visual = captured.Visual;
        guise.Faction = captured.Faction; guise.FactionLeader = captured.Leader; guise.AttackedFactions = new(captured.Attacked);
        Dirty(target, guise);
        _identities.QueueIdentityUpdate(target);
        var lifetime = EnsureComp<ZaygoDisguiseLifetimeComponent>(target);
        lifetime.Until = _time.CurTime + TimeSpan.FromMinutes(15);
        lifetime.OriginalVoice = TryComp<TTSProviderComponent>(target, out var voice) ? voice.Voice : null;
        lifetime.OriginalSpeech = CaptureSpeech(target);
        lifetime.OriginalVocal = CaptureVocal(target);
        if (captured.Voice != null)
        { var tts = EnsureComp<TTSProviderComponent>(target); tts.Voice = captured.Voice; Dirty(target, tts); }
        else RemComp<TTSProviderComponent>(target);
        ApplyVoicePresentation(target, captured.Speech, captured.Vocal);
        // A real cuirass must not remain visible when the captured person wore no armor.
        // It keeps every gameplay component while its presented clothing layer is empty.
        if (_inventory.TryGetSlots(target, out var actualSlots))
            foreach (var actualSlot in actualSlots)
            {
                if (captured.Clothes.ContainsKey(actualSlot.Name) || !_inventory.TryGetSlotEntity(target, actualSlot.Name, out var extra) || !TryComp<ClothingComponent>(extra, out var extraClothing)) continue;
                RestoreItem(extra.Value);
                var restoration = EnsureComp<ZaygoItemRestorationComponent>(extra.Value);
                restoration.Original = CaptureItem(extra.Value, extraClothing);
                restoration.GuisedBy = target;
                restoration.Until = lifetime.Until;
                lifetime.Altered.Add(extra.Value);
                _clothing.CopyVisuals(extra.Value, new ClothingComponent());
                _items.CopyVisuals(extra.Value, new ItemComponent());
                _metadata.SetEntityName(extra.Value, string.Empty);
                _metadata.SetEntityDescription(extra.Value, string.Empty);
                var invisible = EnsureComp<ZaygoItemAppearanceComponent>(extra.Value);
                invisible.Invisible = true;
                Dirty(extra.Value, invisible);
            }
        foreach (var (slot, likeness) in captured.Clothes)
        {
            EntityUid item;
            if (_inventory.TryGetSlotEntity(target, slot, out var current)) item = current.Value;
            else
            {
                item = Spawn("MedievalRitualBlankGarment", Transform(target).Coordinates);
                if (!_inventory.TryEquip(target, item, slot, silent: true, force: true)) { QueueDel(item); continue; }
                lifetime.Created.Add(item);
            }
            RestoreItem(item);
            var restoration = EnsureComp<ZaygoItemRestorationComponent>(item);
            restoration.Original = CaptureItem(item, TryComp<ClothingComponent>(item, out var originalClothing) ? originalClothing : null);
            restoration.Until = lifetime.Until;
            restoration.GuisedBy = target;
            lifetime.Altered.Add(item);
            if (likeness.Clothing != null) _clothing.CopyVisuals(item, likeness.Clothing);
            if (likeness.Item != null) _items.CopyVisuals(item, likeness.Item);
            _metadata.SetEntityName(item, likeness.Name); _metadata.SetEntityDescription(item, likeness.Description);
            if (likeness.Prototype != null)
            { var visual = EnsureComp<ZaygoItemAppearanceComponent>(item); visual.Prototype = likeness.Prototype; Dirty(item, visual); }
        }
        for (var index = 0; index < captured.Documents.Count; index++)
        {
            var document = captured.Documents[index];
            var copy = papers[index];
            var paper = Comp<PaperComponent>(copy);
            var restoration = EnsureComp<ZaygoItemRestorationComponent>(copy);
            restoration.Original = CaptureItem(copy);
            restoration.Until = lifetime.Until;
            restoration.GuisedBy = target;
            restoration.PaperContent = paper.Content;
            restoration.EditingDisabled = paper.EditingDisabled;
            lifetime.Altered.Add(copy);
            paper.Content = document.Text ?? PassportText(document.Passport!);
            paper.EditingDisabled = true;
            Dirty(copy, paper);
            _metadata.SetEntityName(copy, document.Name); _metadata.SetEntityDescription(copy, document.Description);
            var facade = EnsureComp<ZaygoTemporaryDocumentComponent>(copy);
            facade.Passport = document.Passport;
            if (document.Prototype != null)
            { var visual = EnsureComp<ZaygoItemAppearanceComponent>(copy); visual.Prototype = document.Prototype; Dirty(copy, visual); }
            _hands.TryPickupAnyHand(target, copy);
        }
    }

    private static string PassportText(MedievalPasportComponent passport) =>
        $"Имя: {passport.PersonName}\nПол: {passport.PersonGender}\nВозраст: {passport.PersonAge}\nДолжность: {passport.PersonJob}\nРаса: {passport.PersonRace}";

    private ZaygoSpeechPresentation? CaptureSpeech(EntityUid uid) =>
        TryComp<SpeechComponent>(uid, out var speech) ? new(speech.SpeechSounds, speech.AudioParams) : null;

    private ZaygoVocalPresentation? CaptureVocal(EntityUid uid) =>
        TryComp<VocalComponent>(uid, out var vocal)
            ? new(vocal.Sounds == null ? null : new(vocal.Sounds), vocal.EmoteSounds,
                _serialization.CreateCopy(vocal.Wilhelm, notNullableOverride: true), vocal.WilhelmProbability)
            : null;

    private void ApplyVoicePresentation(EntityUid uid, ZaygoSpeechPresentation? speech, ZaygoVocalPresentation? vocal, bool restoring = false)
    {
        // Sound presentation never adds speech, scream actions, or another body's allowed emotes.
        if ((!restoring || speech != null) && TryComp<SpeechComponent>(uid, out var actualSpeech))
        {
            actualSpeech.SpeechSounds = speech?.Sounds;
            if (speech != null) actualSpeech.AudioParams = speech.Parameters;
            Dirty(uid, actualSpeech);
        }
        if ((!restoring || vocal != null) && TryComp<VocalComponent>(uid, out var actualVocal))
        {
            actualVocal.Sounds = vocal?.Sounds == null ? null : new(vocal.Sounds);
            // Use the captured person's resolved voice, even when the real body has another sex.
            actualVocal.EmoteSounds = vocal?.Emotes;
            actualVocal.WilhelmProbability = vocal?.WilhelmProbability ?? 0;
            if (vocal != null) actualVocal.Wilhelm = _serialization.CreateCopy(vocal.Wilhelm, notNullableOverride: true);
            Dirty(uid, actualVocal);
        }
    }

    private void OnDocumentExamined(EntityUid uid, ZaygoTemporaryDocumentComponent comp, ExaminedEvent args)
    {
        if (comp.Passport is { } passport) args.PushText(PassportText(passport));
    }

    private void OnRemoveDisguise(EntityUid uid, ZaygoDisguiseComponent component, GetVerbsEvent<AlternativeVerb> args)
    {
        if (args.User != uid || !args.CanInteract) return;
        args.Verbs.Add(new AlternativeVerb { Text = Loc.GetString("medieval-zaygo-remove-disguise"), Act = () => RemoveIdentity(uid) });
    }

    private void RemoveIdentity(EntityUid uid)
    {
        if (TryComp<ZaygoDisguiseLifetimeComponent>(uid, out var life))
        {
            if (life.OriginalVoice != null)
            { var voice = EnsureComp<TTSProviderComponent>(uid); voice.Voice = life.OriginalVoice; Dirty(uid, voice); }
            else if (life.OriginalVoice == null) RemComp<TTSProviderComponent>(uid);
            ApplyVoicePresentation(uid, life.OriginalSpeech, life.OriginalVocal, restoring: true);
            foreach (var altered in life.Altered)
                if (TryComp<ZaygoItemRestorationComponent>(altered, out var restoration) && restoration.GuisedBy == uid)
                    RestoreItem(altered);
            foreach (var created in life.Created)
                if (Exists(created)) QueueDel(created);
            RemComp<ZaygoDisguiseLifetimeComponent>(uid);
        }
        RemComp<ZaygoDisguiseComponent>(uid);
        _identities.QueueIdentityUpdate(uid);
    }

    private void RestoreItem(EntityUid uid)
    {
        if (!TryComp<ZaygoItemRestorationComponent>(uid, out var restoration)) return;
        var original = restoration.Original;
        if (original.Clothing != null) _clothing.CopyVisuals(uid, original.Clothing);
        if (original.Item != null) _items.CopyVisuals(uid, original.Item);
        _metadata.SetEntityName(uid, original.Name);
        _metadata.SetEntityDescription(uid, original.Description);
        if (restoration.PaperContent != null && TryComp<PaperComponent>(uid, out var paper))
        {
            paper.Content = restoration.PaperContent;
            paper.EditingDisabled = restoration.EditingDisabled;
            Dirty(uid, paper);
        }
        RemComp<ZaygoTemporaryDocumentComponent>(uid);
        RemComp<ZaygoItemAppearanceComponent>(uid);
        RemComp<ZaygoItemRestorationComponent>(uid);
    }

    public override void Update(float frameTime)
    {
        var guises = EntityQueryEnumerator<ZaygoDisguiseLifetimeComponent>();
        while (guises.MoveNext(out var uid, out var lifetime))
            if (_time.CurTime >= lifetime.Until) RemoveIdentity(uid);
        var items = EntityQueryEnumerator<ZaygoItemRestorationComponent>();
        while (items.MoveNext(out var uid, out var restoration))
        {
            if (_time.CurTime < restoration.Until) continue;
            RestoreItem(uid);
        }
    }

    private sealed record ExchangeMove(EntityUid Entity, MapCoordinates Destination, bool Anchored, bool Extract = false);
}

[RegisterComponent]
public sealed partial class ZaygoTheftBlessingComponent : Component { public int Charges; }
[RegisterComponent]
public sealed partial class ZaygoCapturedIdentityComponent : Component
{
    public string DisplayName = string.Empty;
    public int Identifier;
    public bool HideUnknown;
    public ZaygoVisualSnapshot Visual = new();
    public string? Voice;
    public ZaygoSpeechPresentation? Speech;
    public ZaygoVocalPresentation? Vocal;
    public string? Faction;
    public bool Leader;
    public List<string> Attacked = new();
    public Dictionary<string, ItemLikeness> Clothes = new();
    public List<DocumentLikeness> Documents = new();
}
public sealed class ItemLikeness
{
    public string Name = string.Empty;
    public string Description = string.Empty;
    public string? Prototype;
    public ClothingComponent? Clothing;
    public ItemComponent? Item;
}
public sealed record DocumentLikeness(string Name, string Description, string? Text, MedievalPasportComponent? Passport, string? Prototype);
public sealed record ZaygoSpeechPresentation(ProtoId<SpeechSoundsPrototype>? Sounds, AudioParams Parameters);
public sealed record ZaygoVocalPresentation(Dictionary<Sex, ProtoId<EmoteSoundsPrototype>>? Sounds,
    ProtoId<EmoteSoundsPrototype>? Emotes, SoundSpecifier Wilhelm, float WilhelmProbability);
[RegisterComponent]
public sealed partial class ZaygoDisguiseLifetimeComponent : Component
{
    public TimeSpan Until;
    public string? OriginalVoice;
    public ZaygoSpeechPresentation? OriginalSpeech;
    public ZaygoVocalPresentation? OriginalVocal;
    public List<EntityUid> Created = new();
    public List<EntityUid> Altered = new();
}
[RegisterComponent]
public sealed partial class ZaygoItemRestorationComponent : Component
{
    public ItemLikeness Original = new();
    public TimeSpan Until;
    public EntityUid GuisedBy;
    public string? PaperContent;
    public bool EditingDisabled;
}
[RegisterComponent]
public sealed partial class ZaygoTemporaryDocumentComponent : Component { public MedievalPasportComponent? Passport; }
