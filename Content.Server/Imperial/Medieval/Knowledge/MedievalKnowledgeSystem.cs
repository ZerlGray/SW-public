using System.Linq;
using Content.Server.Store.Components;
using Content.Shared.Actions;
using Content.Shared.DoAfter;
using Content.Shared.Examine;
using Content.Shared.Ghost;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Imperial.Medieval.Knowledge;
using Content.Shared.Imperial.Medieval.Language;
using Content.Shared.Imperial.Medieval.Skills;
using Content.Shared.Imperial.Medieval.Trading;
using Content.Shared.Interaction;
using Content.Shared.Mind;
using Content.Shared.Mind.Components;
using Content.Shared.Paper;
using Content.Shared.Popups;
using Content.Shared.Tag;
using Content.Shared.UserInterface;
using Content.Shared.Verbs;
using Robust.Shared.Prototypes;

namespace Content.Server.Imperial.Medieval.Knowledge;

/// <summary>Character knowledge and the single transferable lesson owned by each rare original.</summary>
public sealed partial class MedievalKnowledgeSystem : EntitySystem
{
    [Dependency] private readonly IPrototypeManager _prototypes = default!;
    [Dependency] private readonly SharedMindSystem _minds = default!;
    [Dependency] private readonly ActionContainerSystem _actionContainer = default!;
    [Dependency] private readonly SharedActionsSystem _actions = default!;
    [Dependency] private readonly SharedLanguageSystem _languages = default!;
    [Dependency] private readonly SharedSkillsSystem _skills = default!;
    [Dependency] private readonly SharedDoAfterSystem _doAfter = default!;
    [Dependency] private readonly SharedInteractionSystem _interaction = default!;
    [Dependency] private readonly SharedHandsSystem _hands = default!;
    [Dependency] private readonly SharedPopupSystem _popup = default!;
    [Dependency] private readonly TagSystem _tags = default!;
    [Dependency] private readonly PaperSystem _paper = default!;
    [Dependency] private readonly MetaDataSystem _metadata = default!;
    [Dependency] private readonly SharedUserInterfaceSystem _ui = default!;

    public override void Initialize()
    {
        InitializeSpawning();
        SubscribeLocalEvent<InitialKnowledgeComponent, ComponentStartup>(OnInitialKnowledge);
        SubscribeLocalEvent<MindContainerComponent, MindAddedMessage>(OnMindAdded);
        SubscribeLocalEvent<MindContainerComponent, MindRemovedMessage>(OnMindRemoved);
        SubscribeLocalEvent<LearnableBookComponent, MapInitEvent>(OnBookInit, after: [typeof(PaperSystem)]);
        SubscribeLocalEvent<LearnableBookComponent, BeforeActivatableUIOpenEvent>(OnBookOpened, after: [typeof(PaperSystem)]);
        SubscribeLocalEvent<LearnableBookComponent, GetVerbsEvent<AlternativeVerb>>(OnBookVerbs);
        SubscribeLocalEvent<LearnableBookComponent, ExaminedEvent>(OnBookExamined);
        SubscribeLocalEvent<LearnableBookComponent, StudyBookMessage>(OnStudyMessage);
        SubscribeLocalEvent<LearnableBookComponent, StudyBookDoAfterEvent>(OnStudyFinished);
        SubscribeLocalEvent<PaperComponent, TranslateBookMessage>(OnTranslateMessage);
        SubscribeLocalEvent<BookManuscriptComponent, TranslateBookDoAfterEvent>(OnTranslationFinished);
        SubscribeLocalEvent<BookManuscriptComponent, ComponentShutdown>(OnManuscriptShutdown);
    }

    public bool HasKnowledge(EntityUid user, string knowledgeId)
    {
        var owner = _minds.TryGetMind(user, out var mind, out _) ? mind : user;
        return TryComp<LearnedKnowledgeComponent>(owner, out var learned) && learned.Knowledge.Contains(knowledgeId);
    }

    public bool GrantKnowledge(EntityUid user, string knowledgeId)
    {
        if (!_prototypes.TryIndex<MedievalKnowledgePrototype>(knowledgeId, out var prototype))
            return false;

        var owner = _minds.TryGetMind(user, out var mind, out _) ? mind : user;
        var learned = EnsureComp<LearnedKnowledgeComponent>(owner);
        if (!learned.Knowledge.Add(knowledgeId))
            return false;

        Dirty(owner, learned);
        ApplyKnowledge(user, owner, learned, prototype);
        return true;
    }

    private void ApplyKnowledge(EntityUid body, EntityUid owner, LearnedKnowledgeComponent learned, MedievalKnowledgePrototype prototype)
    {
        if (HasComp<GhostComponent>(body))
            return;

        var mirror = EnsureComp<LearnedKnowledgeComponent>(body);
        mirror.Knowledge.Add(prototype.ID);
        Dirty(body, mirror);
        if (prototype.Language is {} language && TryComp<LanguageSpeakerComponent>(body, out var speaker))
        {
            if (!speaker.Languages.TryGetValue(language, out var level) || level < LanguageKnowledge.Speak)
            {
                mirror.OriginalLanguages.TryAdd(language, speaker.Languages.TryGetValue(language, out var originalLevel) ? originalLevel : null);
                _languages.AddSpokenLanguage(body, language, LanguageKnowledge.Speak, speaker);
                Dirty(body, speaker);
            }
        }

        foreach (var action in prototype.Actions)
        {
            if (learned.GrantedActions.Add(action.Id))
                _actionContainer.AddAction(owner, action.Id);
        }
        if (prototype.Actions.Count > 0)
            _actions.GrantContainedActions(body, owner);
    }

    private void OnInitialKnowledge(Entity<InitialKnowledgeComponent> ent, ref ComponentStartup args)
    {
        foreach (var knowledge in ent.Comp.Knowledge)
            GrantKnowledge(ent, knowledge);
    }

    private void OnMindAdded(EntityUid uid, MindContainerComponent comp, MindAddedMessage args)
    {
        var saved = EnsureComp<LearnedKnowledgeComponent>(args.Mind.Owner);
        // Profession grants can precede attachment of the newly created mind.
        if (TryComp<LearnedKnowledgeComponent>(uid, out var existing))
        {
            saved.Knowledge.UnionWith(existing.Knowledge);
            if (existing.GrantedActions.Count > 0)
            {
                _actions.RemoveProvidedActions(uid, uid);
                existing.GrantedActions.Clear();
            }
        }
        foreach (var id in saved.Knowledge.ToArray())
        {
            if (_prototypes.TryIndex<MedievalKnowledgePrototype>(id, out var prototype))
                ApplyKnowledge(uid, args.Mind.Owner, saved, prototype);
        }
        Dirty(args.Mind.Owner, saved);
    }

    private void OnMindRemoved(EntityUid uid, MindContainerComponent comp, MindRemovedMessage args)
    {
        // The body may not own an ActionContainer; remove its mind-provided actions explicitly.
        _actions.RemoveProvidedActions(uid, args.Mind.Owner);
        if (TryComp<LearnedKnowledgeComponent>(uid, out var mirror))
        {
            if (TryComp<LanguageSpeakerComponent>(uid, out var speaker))
            {
                foreach (var (language, level) in mirror.OriginalLanguages)
                {
                    if (level.HasValue)
                        speaker.Languages[language] = level.Value;
                    else
                        speaker.Languages.Remove(language);
                }
                if (speaker.CurrentLanguage != null && !speaker.Languages.ContainsKey(speaker.CurrentLanguage))
                    _languages.SelectDefaultLanguage(uid, speaker);
                Dirty(uid, speaker);
            }
            mirror.OriginalLanguages.Clear();
            mirror.Knowledge.Clear();
            Dirty(uid, mirror);
        }
    }

    /// <summary>Deliberately excludes translator implants, ghosts and Universal fallback.</summary>
    public bool PersonallyUnderstands(EntityUid user, string language)
    {
        return TryComp<LanguageSpeakerComponent>(user, out var speaker) && speaker.Languages.ContainsKey(language);
    }

    private bool Accessible(EntityUid user, EntityUid book)
    {
        return !Deleted(book) && !HasComp<GhostComponent>(user) &&
               _interaction.InRangeUnobstructed(user, book) && _interaction.CanAccess(user, book);
    }

    private void OnBookInit(Entity<LearnableBookComponent> ent, ref MapInitEvent args)
    {
        if (_prototypes.TryIndex<MedievalKnowledgePrototype>(ent.Comp.Knowledge, out var knowledge) && knowledge.Tier >= 4 && ent.Comp.Original)
        {
            ent.Comp.Encrypted = true;
            Dirty(ent);
        }
        if (!ent.Comp.Original)
        {
            RemComp<CurrencyComponent>(ent);
            RemComp<MedievalCurrencyComponent>(ent);
        }
        if (TryComp<ActivatableUIComponent>(ent, out var activatable))
            activatable.VerbText = "knowledge-read";
        RefreshBookContent(ent);
    }

    private void OnBookExamined(Entity<LearnableBookComponent> ent, ref ExaminedEvent args)
    {
        if (!_prototypes.TryIndex<MedievalKnowledgePrototype>(ent.Comp.Knowledge, out var knowledge))
            return;
        var language = _prototypes.TryIndex<LanguagePrototype>(ent.Comp.Language, out var lang) ? lang.LocalizedName : ent.Comp.Language;
        args.PushMarkup(Loc.GetString("knowledge-book-examine", ("tier", knowledge.Tier), ("language", language)));
        args.PushMarkup(Loc.GetString(ent.Comp.Original ? "knowledge-book-original" : "knowledge-book-translation"));
        if (ent.Comp.Spent)
            args.PushMarkup(Loc.GetString("knowledge-book-spent"));
        else if (ent.Comp.Encrypted)
            args.PushMarkup(Loc.GetString("knowledge-book-encrypted"));
    }

    private void OnBookVerbs(Entity<LearnableBookComponent> ent, ref GetVerbsEvent<AlternativeVerb> args)
    {
        if (!args.CanInteract || !args.CanAccess)
            return;
        var user = args.User;
        args.Verbs.Add(new AlternativeVerb { Text = Loc.GetString("knowledge-study"), Act = () => TryStudy(user, ent) });
    }

    private void OnStudyMessage(Entity<LearnableBookComponent> ent, ref StudyBookMessage args) => TryStudy(args.Actor, ent);

    private string? StudyFailure(EntityUid user, Entity<LearnableBookComponent> book)
    {
        if (!Accessible(user, book) || !_skills.CanRead(user))
            return "knowledge-cannot-read";
        if (book.Comp.Spent || book.Comp.TranslationTarget != null)
            return "knowledge-book-spent";
        if (book.Comp.Encrypted)
            return "knowledge-book-encrypted";
        if (!PersonallyUnderstands(user, book.Comp.Language))
            return "knowledge-unknown-language";
        if (!_prototypes.TryIndex<MedievalKnowledgePrototype>(book.Comp.Knowledge, out var knowledge))
            return "knowledge-cannot-read";
        if (HasKnowledge(user, book.Comp.Knowledge))
            return "knowledge-already-known";
        if (knowledge.Language is {} taught && TryComp<LanguageSpeakerComponent>(user, out var speaker) &&
            speaker.Languages.TryGetValue(taught, out var level) && level >= LanguageKnowledge.Speak)
            return "knowledge-already-known";
        return null;
    }

    public bool TryStudy(EntityUid user, Entity<LearnableBookComponent> book)
    {
        if (StudyFailure(user, book) is {} failure)
        {
            _popup.PopupEntity(Loc.GetString(failure), book, user);
            return false;
        }
        if (book.Comp.Reading != null)
            return false;
        var args = new DoAfterArgs(EntityManager, user, TimeSpan.FromSeconds(book.Comp.StudySeconds), new StudyBookDoAfterEvent(), book, book)
        {
            BreakOnDamage = true, BreakOnMove = true, NeedHand = true, DistanceThreshold = 1.5f,
        };
        if (!_doAfter.TryStartDoAfter(args, out var id))
            return false;
        book.Comp.Reading = id;
        return true;
    }

    private void OnStudyFinished(Entity<LearnableBookComponent> ent, ref StudyBookDoAfterEvent args)
    {
        ent.Comp.Reading = null;
        if (args.Cancelled || args.Handled || StudyFailure(args.User, ent) != null)
            return;
        if (!GrantKnowledge(args.User, ent.Comp.Knowledge))
            return;
        ent.Comp.Spent = true;
        Dirty(ent);
        RefreshBookContent(ent);
        args.Handled = true;
        _popup.PopupEntity(Loc.GetString("knowledge-learned", ("knowledge", Loc.GetString(_prototypes.Index<MedievalKnowledgePrototype>(ent.Comp.Knowledge).Name))), ent, args.User);
    }

    private bool CanTranscribe(EntityUid writer, EntityUid target, EntityUid source, string language)
    {
        if (!Accessible(writer, target) || !Accessible(writer, source) || !_skills.CanRead(writer) ||
            !HasKnowledge(writer, "BookDecipherer") || !PersonallyUnderstands(writer, language) ||
            !_tags.HasTag(target, "Book") || !_hands.EnumerateHeld(writer).Any(item => _tags.HasTag(item, "Write")) ||
            !TryComp<PaperComponent>(target, out var paper) || paper.EditingDisabled || paper.StampedBy.Count != 0 ||
            !TryComp<LearnableBookComponent>(source, out var original) || !PersonallyUnderstands(writer, original.Language) ||
            original.Spent || !original.Original || original.Reading != null || HasComp<LearnableBookComponent>(target))
            return false;
        var attempt = new PaperWriteAttemptEvent(target);
        RaiseLocalEvent(writer, ref attempt);
        return !attempt.Cancelled && _prototypes.HasIndex<MedievalKnowledgePrototype>(original.Knowledge);
    }

    private void OnTranslateMessage(Entity<PaperComponent> ent, ref TranslateBookMessage args)
    {
        TryTranslate(args.Actor, ent.Owner, GetEntity(args.Source), args.Language);
    }

    public bool TryTranslate(EntityUid writer, EntityUid target, EntityUid source, string language)
    {
        if (source == target || !CanTranscribe(writer, target, source, language) ||
            Comp<LearnableBookComponent>(source).TranslationTarget != null || HasComp<BookManuscriptComponent>(target))
        {
            _popup.PopupEntity(Loc.GetString("knowledge-translation-requirements"), target, writer);
            return false;
        }
        var original = Comp<LearnableBookComponent>(source);
        var manuscript = AddComp<BookManuscriptComponent>(target);
        manuscript.Source = source;
        manuscript.Writer = writer;
        manuscript.Language = language;
        original.TranslationTarget = target;
        var doAfter = new DoAfterArgs(EntityManager, writer, TimeSpan.FromSeconds(original.TranslationSeconds), new TranslateBookDoAfterEvent(), target, target, source)
        {
            BreakOnDamage = true, BreakOnMove = true, NeedHand = true, BreakOnHandChange = true, DistanceThreshold = 1.5f,
        };
        if (_doAfter.TryStartDoAfter(doAfter, out var id))
        {
            manuscript.Writing = id;
            OpenBook(writer, (source, original));
            return true;
        }
        original.TranslationTarget = null;
        RemComp<BookManuscriptComponent>(target);
        return false;
    }

    private void OnTranslationFinished(Entity<BookManuscriptComponent> ent, ref TranslateBookDoAfterEvent args)
    {
        var source = ent.Comp.Source;
        if (!TryComp<LearnableBookComponent>(source, out var original))
        {
            RemCompDeferred<BookManuscriptComponent>(ent);
            return;
        }
        var reserved = original.TranslationTarget == ent.Owner;
        if (reserved)
            original.TranslationTarget = null;
        if (!args.Cancelled && !args.Handled && reserved && CanTranscribe(args.User, ent, source, ent.Comp.Language))
        {
            // Commit the single lesson only after all validation. Plain text copying cannot reach this path.
            original.Spent = true;
            Dirty(source, original);
            RefreshBookContent((source, original));
            var edition = AddComp<LearnableBookComponent>(ent);
            edition.Knowledge = original.Knowledge;
            edition.Language = ent.Comp.Language;
            edition.Original = false;
            edition.StudySeconds = original.StudySeconds;
            edition.Translator = Name(args.User);
            Dirty(ent, edition);
            RemComp<CurrencyComponent>(ent);
            RemComp<MedievalCurrencyComponent>(ent);
            var knowledge = _prototypes.Index<MedievalKnowledgePrototype>(edition.Knowledge);
            _metadata.SetEntityName(ent, Loc.GetString("knowledge-translated-title", ("title", Loc.GetString(knowledge.Name))));
            if (TryComp<ActivatableUIComponent>(ent, out var activatable))
                activatable.VerbText = "knowledge-read";
            OpenBook(args.User, (ent.Owner, edition));
            _popup.PopupEntity(Loc.GetString("knowledge-translation-complete"), ent, args.User);
            args.Handled = true;
        }
        RemCompDeferred<BookManuscriptComponent>(ent);
    }

    private void OnManuscriptShutdown(Entity<BookManuscriptComponent> ent, ref ComponentShutdown args)
    {
        if (TryComp<LearnableBookComponent>(ent.Comp.Source, out var original) && original.TranslationTarget == ent.Owner)
            original.TranslationTarget = null;
    }
}
