using Content.Server.Imperial.Medieval.Knowledge;
using Content.Server.Store.Components;
using Content.Shared.Actions;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Imperial.Medieval.Knowledge;
using Content.Shared.Imperial.Medieval.Language;
using Content.Shared.Imperial.Medieval.Trading;
using Content.Shared.Mind;
using Content.Shared.Mind.Components;
using Robust.Shared.GameObjects;

namespace Content.IntegrationTests.Tests.Imperial.Medieval.Knowledge;

[TestFixture]
public sealed class BookLearningTest
{
    [Test]
    public async Task KnowledgeFollowsMindAndDoesNotTeachTheVacatedBody()
    {
        await using var pair = await PoolManager.GetServerClient();
        var map = await pair.CreateTestMap();
        var entities = pair.Server.ResolveDependency<IEntityManager>();
        await pair.Server.WaitAssertion(() =>
        {
            var first = entities.SpawnEntity(null, map.GridCoords);
            var second = entities.SpawnEntity(null, map.GridCoords);
            entities.EnsureComponent<MindContainerComponent>(first);
            entities.EnsureComponent<MindContainerComponent>(second);
            entities.EnsureComponent<LanguageSpeakerComponent>(first).Languages["Common"] = LanguageKnowledge.Speak;
            entities.EnsureComponent<LanguageSpeakerComponent>(second).Languages["Common"] = LanguageKnowledge.Speak;
            var minds = entities.System<SharedMindSystem>();
            var mind = minds.CreateMind(null);
            minds.TransferTo(mind, first);
            var knowledge = entities.System<MedievalKnowledgeSystem>();
            Assert.That(knowledge.GrantKnowledge(first, "BookDecipherer"), Is.True);
            Assert.That(knowledge.GrantKnowledge(first, "BookLanguageElf"), Is.True);
            Assert.That(knowledge.GrantKnowledge(first, "BookLanguageElf"), Is.False);
            Assert.That(knowledge.GrantKnowledge(first, "BookEscapeBonds"), Is.True);
            Assert.That(knowledge.PersonallyUnderstands(first, "Ancient"), Is.False);
            var actions = entities.System<SharedActionsSystem>();
            Assert.That(actions.GetActions(first), Is.Not.Empty);
            minds.TransferTo(mind, second);
            Assert.Multiple(() =>
            {
                Assert.That(knowledge.HasKnowledge(second, "BookDecipherer"), Is.True);
                Assert.That(knowledge.HasKnowledge(second, "BookLanguageElf"), Is.True);
                Assert.That(knowledge.HasKnowledge(first, "BookDecipherer"), Is.False);
                Assert.That(knowledge.PersonallyUnderstands(first, "Elf"), Is.False);
                Assert.That(actions.GetActions(first), Is.Empty);
                Assert.That(actions.GetActions(second), Is.Not.Empty);
                Assert.That(entities.GetComponent<LanguageSpeakerComponent>(second).Languages["Elf"], Is.EqualTo(LanguageKnowledge.Speak));
            });
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task TranslationTransfersOneLessonAndPreservesOriginalValue()
    {
        await using var pair = await PoolManager.GetServerClient();
        var map = await pair.CreateTestMap();
        var entities = pair.Server.ResolveDependency<IEntityManager>();
        EntityUid reader = default;
        EntityUid original = default;
        EntityUid translation = default;
        EntityUid spare = default;
        await pair.Server.WaitAssertion(() =>
        {
            reader = entities.SpawnEntity("MobHuman", map.GridCoords);
            entities.EnsureComponent<LanguageSpeakerComponent>(reader).Languages["Common"] = LanguageKnowledge.Speak;
            var pen = entities.SpawnEntity("MedievalPen", map.GridCoords);
            Assert.That(entities.System<SharedHandsSystem>().TryPickupAnyHand(reader, pen), Is.True);
            original = entities.SpawnEntity("MedievalKnowledgeBook", map.GridCoords);
            translation = entities.SpawnEntity("BookBase", map.GridCoords);
            spare = entities.SpawnEntity("BookBase", map.GridCoords);
            entities.EnsureComponent<CurrencyComponent>(translation).Price["Revent"] = 10;
            entities.EnsureComponent<MedievalCurrencyComponent>(translation).Price["Revent"] = 10;
            var lesson = entities.GetComponent<LearnableBookComponent>(original);
            lesson.Knowledge = "BookLanguageElf";
            lesson.Encrypted = true;
            lesson.TranslationSeconds = 0.1f;
            lesson.StudySeconds = 0.1f;
            var system = entities.System<MedievalKnowledgeSystem>();
            Assert.That(system.TryTranslate(reader, translation, original, "Common"), Is.False);
            system.GrantKnowledge(reader, "BookDecipherer");
            Assert.That(system.TryTranslate(reader, translation, original, "Ancient"), Is.False);
            Assert.That(system.TryStudy(reader, (original, lesson)), Is.False);
            Assert.That(system.TryTranslate(reader, translation, original, "Common"), Is.True);
            Assert.That(system.TryTranslate(reader, spare, original, "Common"), Is.False);
        });
        await pair.RunTicksSync(90);
        await pair.Server.WaitAssertion(() =>
        {
            var originalBook = entities.GetComponent<LearnableBookComponent>(original);
            var edition = entities.GetComponent<LearnableBookComponent>(translation);
            Assert.Multiple(() =>
            {
                Assert.That(originalBook.Spent, Is.True);
                Assert.That(edition.Original, Is.False);
                Assert.That(edition.Encrypted, Is.False);
                Assert.That(edition.Spent, Is.False);
                Assert.That(edition.Language, Is.EqualTo("Common"));
                Assert.That(entities.HasComponent<CurrencyComponent>(translation), Is.False);
                Assert.That(entities.HasComponent<MedievalCurrencyComponent>(translation), Is.False);
                Assert.That(entities.GetComponent<CurrencyComponent>(original).Price["Revent"].Int(), Is.EqualTo(400));
                Assert.That(entities.GetComponent<MedievalCurrencyComponent>(original).Price["Revent"].Int(), Is.EqualTo(400));
            });
            var system = entities.System<MedievalKnowledgeSystem>();
            Assert.That(system.TryTranslate(reader, spare, original, "Common"), Is.False);
            Assert.That(system.TryStudy(reader, (translation, edition)), Is.True);
        });
        await pair.RunTicksSync(90);
        await pair.Server.WaitAssertion(() =>
        {
            var edition = entities.GetComponent<LearnableBookComponent>(translation);
            var system = entities.System<MedievalKnowledgeSystem>();
            Assert.That(edition.Spent, Is.True);
            Assert.That(system.HasKnowledge(reader, "BookLanguageElf"), Is.True);
            Assert.That(system.TryStudy(reader, (translation, edition)), Is.False);
        });
        await pair.CleanReturnAsync();
    }
}
