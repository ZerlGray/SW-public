using System.Linq;
using System.Numerics;
using Content.Server.Imperial.Medieval.Knowledge;
using Content.Server.Imperial.Medieval.Rituals;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.Damage;
using Content.Shared.Damage.Components;
using Content.Shared.Damage.Systems;
using Content.Shared.Imperial.Medieval.Rituals;
using Content.Shared.Imperial.Medieval.Additions;
using Content.Shared.Humanoid;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Mobs.Systems;
using Content.Shared.Speech;
using Content.Shared.Speech.Components;
using Robust.Shared.Audio;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.IntegrationTests.Tests.Imperial.Medieval.Rituals;

[TestFixture]
public sealed class RitualSystemTest
{
    [TestPrototypes]
    private const string Prototypes = """
- type: medievalRitual
  id: TestPreparedKreza
  name: medieval-ritual-kreza2-name
  knowledge: RitualKreza2
  effect: Kreza2
  duration: 0.2
  offerings:
    Candle: 1
- type: medievalRitual
  id: TestSomaWater
  name: medieval-ritual-soma3-name
  knowledge: RitualSoma3
  effect: Soma3
  tier: 3
  duration: 0.2
  offerings:
    Candle: 1
  reagents:
    Water: 10
""";

    [Test]
    public async Task OfferingsAreExclusiveAndCancellationDoesNotSpendThem()
    {
        await using var pair = await PoolManager.GetServerClient();
        var map = await pair.CreateTestMap();
        var em = pair.Server.ResolveDependency<IEntityManager>();
        EntityUid user = default, first = default, second = default, candle = default;
        await pair.Server.WaitAssertion(() =>
        {
            user = em.SpawnEntity("MobHuman", map.GridCoords);
            first = em.SpawnEntity("MedievalRitualCenter", map.GridCoords);
            second = em.SpawnEntity("MedievalRitualCenter", map.GridCoords.Offset(new Vector2(1, 0)));
            candle = em.SpawnEntity("Candle", map.GridCoords);
            foreach (var center in new[] {first, second})
            {
                var circle = em.GetComponent<MedievalRitualCenterComponent>(center);
                circle.Owner = user;
                circle.Target = user;
            }
            var rituals = em.System<MedievalRitualSystem>();
            Assert.That(rituals.TryStart(first, user, "TestPreparedKreza"), Is.False, "An unlearned prayer cannot be started.");
            em.System<MedievalKnowledgeSystem>().GrantKnowledge(user, "RitualKreza2");
            Assert.That(rituals.TryStart(first, user, "TestPreparedKreza"), Is.True);
            Assert.That(rituals.TryStart(second, user, "TestPreparedKreza"), Is.False, "The same offering cannot pay two simultaneous prayers.");
            em.System<SharedTransformSystem>().SetCoordinates(user, map.GridCoords.Offset(new Vector2(2, 0)));
        });
        await pair.RunTicksSync(60);
        await pair.Server.WaitAssertion(() =>
        {
            Assert.That(em.EntityExists(candle), Is.True);
            Assert.That(em.HasComponent<MedievalRitualOfferingReservedComponent>(candle), Is.False);
            Assert.That(em.HasComponent<MedievalPreparedBlessingsComponent>(user), Is.False);
            em.System<SharedTransformSystem>().SetCoordinates(user, map.GridCoords);
            Assert.That(em.System<MedievalRitualSystem>().TryStart(first, user, "TestPreparedKreza"), Is.True);
        });
        await pair.RunTicksSync(60);
        await pair.Server.WaitAssertion(() =>
        {
            Assert.That(em.EntityExists(candle), Is.False);
            Assert.That(em.GetComponent<MedievalPreparedBlessingsComponent>(user).Charges, Does.Contain("KrezaFree"));
            Assert.That(em.HasComponent<MedievalRitualAttemptComponent>(first), Is.False);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task WaterOfferingConsumesLiquidAndPreservesItsVessel()
    {
        await using var pair = await PoolManager.GetServerClient();
        var map = await pair.CreateTestMap();
        var em = pair.Server.ResolveDependency<IEntityManager>();
        EntityUid bottle = default, center = default;
        float before = 0;
        await pair.Server.WaitAssertion(() =>
        {
            var user = em.SpawnEntity("MobHuman", map.GridCoords);
            center = em.SpawnEntity("MedievalRitualCenter", map.GridCoords);
            em.GetComponent<MedievalRitualCenterComponent>(center).Owner = user;
            bottle = em.SpawnEntity("DrinkWaterBottleFull", map.GridCoords);
            em.SpawnEntity("Candle", map.GridCoords);
            before = em.System<SharedSolutionContainerSystem>().EnumerateSolutions((bottle, null)).Sum(s => s.Solution.Comp.Solution.GetTotalPrototypeQuantity("Water").Float());
            em.System<MedievalKnowledgeSystem>().GrantKnowledge(user, "RitualSoma3");
            Assert.That(em.System<MedievalRitualSystem>().TryStart(center, user, "TestSomaWater"), Is.True);
        });
        await pair.RunTicksSync(60);
        await pair.Server.WaitAssertion(() =>
        {
            Assert.That(em.EntityExists(bottle), Is.True);
            var after = em.System<SharedSolutionContainerSystem>().EnumerateSolutions((bottle, null)).Sum(s => s.Solution.Comp.Solution.GetTotalPrototypeQuantity("Water").Float());
            Assert.That(after, Is.EqualTo(before - 10));
            Assert.That(em.HasComponent<MedievalSomaHospitalComponent>(center), Is.True);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task ValtorRetainsDamageAndHealingCanSaveTheBearer()
    {
        await using var pair = await PoolManager.GetServerClient();
        var map = await pair.CreateTestMap();
        var em = pair.Server.ResolveDependency<IEntityManager>();
        await pair.Server.WaitAssertion(() =>
        {
            var user = em.SpawnEntity("MobHuman", map.GridCoords);
            // A freshly spawned human rejects both wound and stamina damage for 45 seconds.
            em.RemoveComponent<ShieldOnStartupComponent>(user);
            var timing = pair.Server.ResolveDependency<IGameTiming>();
            var blessing = em.EnsureComponent<MedievalValtorConsciousnessComponent>(user);
            blessing.DelayDeath = true;
            blessing.Until = timing.CurTime + TimeSpan.FromMinutes(1);
            var damage = em.System<DamageableSystem>();
            damage.TryChangeDamage(user, new DamageSpecifier {DamageDict = new() {{"Blunt", 1000}}}, ignoreResistances: true);
            Assert.That(em.GetComponent<MobStateComponent>(user).CurrentState, Is.EqualTo(MobState.Alive));
            Assert.That(em.GetComponent<DamageableComponent>(user).TotalDamage.Float(), Is.GreaterThanOrEqualTo(1000));
            damage.TryChangeDamage(user, new DamageSpecifier {DamageDict = new() {{"Blunt", -1000}}}, ignoreResistances: true);
            blessing.Until = timing.CurTime - TimeSpan.FromSeconds(1);
            em.System<MobThresholdSystem>().RefreshLivingThresholds(user);
            Assert.That(em.GetComponent<MobStateComponent>(user).CurrentState, Is.EqualTo(MobState.Alive));

            em.EnsureComponent<MedievalValtorEnduranceComponent>(user).Until = timing.CurTime + TimeSpan.FromMinutes(1);
            var stamina = em.GetComponent<StaminaComponent>(user);
            Assert.That(em.System<SharedStaminaSystem>().TryTakeStamina(user, stamina.CritThreshold + 10), Is.True);
            Assert.That(stamina.Critical, Is.False);
            Assert.That(stamina.StaminaDamage, Is.GreaterThan(stamina.CritThreshold));
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task IdentityCopiesSoundPresentationAndRestoresItWithoutGrantingEmotes()
    {
        await using var pair = await PoolManager.GetServerClient();
        var map = await pair.CreateTestMap();
        var em = pair.Server.ResolveDependency<IEntityManager>();
        EntityUid wearer = default;
        SpeechComponent originalSpeech = default!;
        VocalComponent originalVocal = default!;
        ZaygoSpeechPresentation originalSounds = default!;
        string? originalEmotes = null;
        string[] allowed = Array.Empty<string>();
        EntityUid? screamAction = null;
        await pair.Server.WaitAssertion(() =>
        {
            wearer = em.SpawnEntity("MobHuman", map.GridCoords);
            var center = em.SpawnEntity("MedievalRitualCenter", map.GridCoords);
            var mask = em.SpawnEntity("MedievalRitualWaxMask", map.GridCoords);
            originalSpeech = em.GetComponent<SpeechComponent>(wearer);
            originalVocal = em.GetComponent<VocalComponent>(wearer);
            originalSounds = new(originalSpeech.SpeechSounds, originalSpeech.AudioParams);
            originalEmotes = originalVocal.EmoteSounds?.Id;
            allowed = originalSpeech.AllowedEmotes.Select(e => e.Id).ToArray();
            screamAction = originalVocal.ScreamActionEntity;
            var likeness = em.EnsureComponent<ZaygoCapturedIdentityComponent>(mask);
            likeness.DisplayName = "Borrowed identity";
            likeness.Visual = ZaygoVisualSnapshot.Capture(em.GetComponent<HumanoidAppearanceComponent>(wearer));
            likeness.Speech = new(null, AudioParams.Default.WithVolume(-12));
            likeness.Vocal = new(null, "FemaleHuman", originalVocal.Wilhelm, 0);
            var context = new MedievalRitualContext
            {
                Caster = wearer, Target = wearer, Center = center,
                Participants = new[] {wearer}, Offerings = new[] {mask},
                Ritual = pair.Server.ResolveDependency<IPrototypeManager>().Index<MedievalRitualPrototype>("ZaygoIdentity3")
            };
            context.Data["mask"] = mask;
            context.Data["papers"] = Array.Empty<EntityUid>();
            em.EventBus.RaiseLocalEvent(center, new MedievalRitualExecuteEvent(context), broadcast: true);
            Assert.That(originalSpeech.SpeechSounds, Is.Null);
            Assert.That(originalSpeech.AudioParams, Is.EqualTo(likeness.Speech.Parameters));
            Assert.That(originalVocal.EmoteSounds?.Id, Is.EqualTo("FemaleHuman"));
            Assert.That(originalVocal.ScreamActionEntity, Is.EqualTo(screamAction));
            Assert.That(originalSpeech.AllowedEmotes.Select(e => e.Id), Is.EqualTo(allowed));
            em.GetComponent<ZaygoDisguiseLifetimeComponent>(wearer).Until = pair.Server.ResolveDependency<IGameTiming>().CurTime - TimeSpan.FromSeconds(1);
        });
        await pair.RunTicksSync(2);
        await pair.Server.WaitAssertion(() =>
        {
            Assert.That(em.HasComponent<ZaygoDisguiseComponent>(wearer), Is.False);
            Assert.That(originalSpeech.SpeechSounds, Is.EqualTo(originalSounds.Sounds));
            Assert.That(originalSpeech.AudioParams, Is.EqualTo(originalSounds.Parameters));
            Assert.That(originalVocal.EmoteSounds?.Id, Is.EqualTo(originalEmotes));
            Assert.That(originalVocal.ScreamActionEntity, Is.EqualTo(screamAction));
            Assert.That(originalSpeech.AllowedEmotes.Select(e => e.Id), Is.EqualTo(allowed));
        });
        await pair.CleanReturnAsync();
    }
}
