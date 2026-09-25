using Content.Shared.Imperial.Medieval.Knowledge;
using Content.Shared.Imperial.Medieval.Language;
using Content.Shared.Paper;
using Content.Shared.Interaction;
using Content.Shared.Tag;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Prototypes;

namespace Content.Client.Paper.UI;

/// <summary>Learning and transcription live in the existing book reader/writing editor.</summary>
public sealed class KnowledgePaperControls : BoxContainer
{
    public KnowledgePaperControls(IEntityManager entities, EntityUid book, EntityUid? user,
        PaperComponent.PaperAction mode, Action<BoundUserInterfaceMessage> send)
    {
        Orientation = LayoutOrientation.Vertical;
        Margin = new Thickness(6);
        if (!user.HasValue)
            return;
        var prototypes = IoCManager.Resolve<IPrototypeManager>();
        if (entities.TryGetComponent<LearnableBookComponent>(book, out var lesson))
        {
            if (prototypes.TryIndex<MedievalKnowledgePrototype>(lesson.Knowledge, out var knowledge))
                AddChild(new Label { Text = Loc.GetString(knowledge.Name) });
            var learn = new Button
            {
                Text = Loc.GetString(lesson.Spent ? "knowledge-book-spent" : lesson.Encrypted ? "knowledge-book-encrypted" : "knowledge-study"),
                Disabled = lesson.Spent || lesson.Encrypted,
            };
            learn.OnPressed += _ => send(new StudyBookMessage());
            AddChild(learn);
            return;
        }

        if (mode != PaperComponent.PaperAction.Write || !entities.System<TagSystem>().HasTag(book, "Book") ||
            !entities.TryGetComponent<LearnedKnowledgeComponent>(user, out var learned) || !learned.Knowledge.Contains("BookDecipherer") ||
            !entities.TryGetComponent<LanguageSpeakerComponent>(user, out var speaker))
            return;

        AddChild(new Label { Text = Loc.GetString("knowledge-translation-heading") });
        var sources = new OptionButton();
        var sourceIds = new List<NetEntity>();
        var transform = entities.System<SharedTransformSystem>();
        var userTransform = entities.GetComponent<TransformComponent>(user.Value);
        var query = entities.EntityQueryEnumerator<LearnableBookComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var candidate, out var xform))
        {
            if (!candidate.Original || candidate.Spent || xform.MapID != userTransform.MapID ||
                !speaker.Languages.ContainsKey(candidate.Language) ||
                !entities.System<SharedInteractionSystem>().CanAccess(user.Value, uid) ||
                (transform.GetWorldPosition(xform) - transform.GetWorldPosition(userTransform)).LengthSquared() > 4)
                continue;
            sources.AddItem(entities.GetComponent<MetaDataComponent>(uid).EntityName, sourceIds.Count);
            sourceIds.Add(entities.GetNetEntity(uid));
        }
        sources.OnItemSelected += args => sources.SelectId(args.Id);
        var languages = new OptionButton();
        var languageIds = new List<string>();
        foreach (var language in speaker.Languages.Keys)
        {
            if (!prototypes.TryIndex<LanguagePrototype>(language, out var prototype))
                continue;
            languages.AddItem(prototype.LocalizedName, languageIds.Count);
            languageIds.Add(language);
        }
        languages.OnItemSelected += args => languages.SelectId(args.Id);
        var translate = new Button { Text = Loc.GetString("knowledge-translate"), Disabled = sourceIds.Count == 0 || languageIds.Count == 0 };
        translate.OnPressed += _ => send(new TranslateBookMessage(sourceIds[sources.SelectedId], languageIds[languages.SelectedId]));
        AddChild(sources);
        AddChild(languages);
        AddChild(translate);
    }
}
