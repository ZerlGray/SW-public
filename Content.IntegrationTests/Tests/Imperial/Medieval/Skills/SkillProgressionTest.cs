using System.Collections.Generic;
using System.Linq;
using Content.Server.Imperial.ImperialStore;
using Content.Server.Imperial.Medieval.Magic.BindStoreOnEquip;
using Content.Server.Imperial.Medieval.Skills;
using Content.Server.Imperial.Medieval.Skills.Progression;
using Content.Shared.Chemistry.Reagent;
using Content.Shared.Damage;
using Content.Shared.Damage.Components;
using Content.Shared.Damage.Systems;
using Content.Shared.EntityEffects.EffectConditions;
using Content.Shared.FixedPoint;
using Content.Shared.Imperial.Dash;
using Content.Shared.Imperial.ImperialStore;
using Content.Shared.Imperial.Medieval.Additions;
using Content.Shared.Imperial.Medieval.Magic.Mana;
using Content.Shared.Imperial.Medieval.Skills;
using Content.Shared.Mobs.Components;
using Content.Shared.Projectiles;
using Content.Shared.Preferences;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.UnitTesting;

namespace Content.IntegrationTests.Tests.Imperial.Medieval.Skills;

/// <summary>Exercise skill changes on real server entities without starting a graphical client.</summary>
[NonParallelizable]
public sealed class SkillProgressionTest : RobustIntegrationTest
{
#pragma warning disable NUnit1032 // RobustIntegrationTest.OneTimeTearDown stops every instance created by StartServer.
    private ServerIntegrationInstance _server = default!;
#pragma warning restore NUnit1032

    [OneTimeSetUp]
    public async Task StartSkillServer()
    {
        var options = new ServerIntegrationOptions
        {
            Pool = false,
            ContentStart = true,
            LoadTestAssembly = false,
            ContentAssemblies = new[]
            {
                typeof(Content.Shared.Entry.EntryPoint).Assembly,
                typeof(Content.Server.Entry.EntryPoint).Assembly,
            },
            Options = new()
            {
                LoadConfigAndUserData = false,
                LoadContentResources = true,
            },
        };
        foreach (var (cvar, value) in PoolManager.TestCvars)
            options.CVarOverrides[cvar] = value;

        _server = StartServer(options);
        await _server.WaitIdleAsync();
    }

    private EntityUid SpawnCharacter()
    {
        var uid = _server.EntMan.SpawnEntity("MobHuman", MapCoordinates.Nullspace);
        // Model an established character: the normal 45-second spawn shield cancels stamina damage.
        _server.EntMan.RemoveComponent<ShieldOnStartupComponent>(uid);
        _server.System<SkillsSystem>().SetSkills(uid, SharedSkillsSystem.GetDefaultSkillLevels(_server.ProtoMan));
        return uid;
    }

    [Test]
    public async Task LowSkillRefundsFundOtherSkillsSymmetrically()
    {
        await _server.WaitAssertion(() =>
        {
            Assert.That(SkillScaling.PointCost(1) - SkillScaling.PointCost(2), Is.EqualTo(2));
            Assert.That(SkillScaling.PointCost(4) - SkillScaling.PointCost(5), Is.EqualTo(2));
            Assert.That(SkillScaling.PointCost(1), Is.EqualTo(11));
            var profile = HumanoidCharacterProfile.DefaultWithSpecies("Human");
            profile = profile.WithSkill(SharedSkillsSystem.StrengthId, 1, out var success);
            Assert.That(success, Is.True);
            Assert.That(SharedSkillsSystem.GetRemainingPoints(_server.ProtoMan, profile.Skills), Is.EqualTo(14));
            profile = profile.WithSkill(SharedSkillsSystem.IntelligenceId, 17, out success);
            Assert.That(success, Is.True);
            Assert.That(SharedSkillsSystem.GetRemainingPoints(_server.ProtoMan, profile.Skills), Is.EqualTo(1));
            var rejected = profile.WithSkill(SharedSkillsSystem.StrengthId, 2, out success);
            Assert.That(success, Is.False, "A two-point step must reject a one-point budget.");
            Assert.That(rejected.Skills[SharedSkillsSystem.StrengthId], Is.EqualTo(1));
        });
    }

    [TestCase(12, 1)]
    [TestCase(16, 2)]
    [TestCase(20, 3)]
    public async Task JumpSeriesUsesOneNormalStaminaCost(int level, int charges)
    {
        await _server.WaitAssertion(() =>
        {
            var uid = SpawnCharacter();
            try
            {
                _server.System<SkillsSystem>().SetSkillLevel(uid, SharedSkillsSystem.AgilityId, level);
                var state = _server.EntMan.GetComponent<SkillProgressionComponent>(uid);
                var stamina = _server.EntMan.GetComponent<StaminaComponent>(uid);
                var jump = _server.System<SkillJumpSystem>();
                state.JumpRecharge = _server.Timing.CurTime + TimeSpan.FromSeconds(10);
                for (var i = 0; i < charges; i++)
                {
                    state.JumpsUsed = i;
                    Assert.That(jump.CanJump(uid), Is.True);
                    var cost = new CheckDashStaminaCostModifiersEvent(1f);
                    _server.EntMan.EventBus.RaiseLocalEvent(uid, ref cost);
                    Assert.That(cost.Modifier, Is.EqualTo(1f / charges).Within(0.0001f));
                    Assert.That(_server.System<SharedStaminaSystem>().TryTakeStamina(uid, 15f * cost.Modifier, ignoreResist: true), Is.True);
                }
                state.JumpsUsed = charges;
                Assert.That(jump.CanJump(uid), Is.False);
                Assert.That(stamina.StaminaDamage, Is.EqualTo(15f).Within(0.001f));
                state.JumpRecharge = _server.Timing.CurTime;
                Assert.That(jump.CanJump(uid), Is.True);
            }
            finally
            {
                _server.EntMan.DeleteEntity(uid);
            }
        });
    }

    [TestCase("medievalhealburn", 11)]
    [TestCase("medievalhealbloodandoxygen", 11)]
    [TestCase("medievalhealslash", 30)]
    [TestCase("medievalhealpiercing", 30)]
    [TestCase("medievalhealblunt", 30)]
    public async Task MedicinalOverdoseReturnsAtExactlyDoubleThreshold(string reagent, int threshold)
    {
        await _server.WaitAssertion(() =>
        {
            var uid = SpawnCharacter();
            try
            {
                var poison = _server.ProtoMan.Index<ReagentPrototype>(reagent).Metabolisms["Poison"];
                var effects = poison.Effects.Where(x => x.Conditions?.OfType<ReagentThreshold>().Any() == true).ToArray();
                Assert.That(effects, Is.Not.Empty);
                foreach (var effect in effects)
                {
                    _server.System<SkillsSystem>().SetSkillLevel(uid, SharedSkillsSystem.VitalityId, 15);
                    var ordinary = new MetabolismEffectAttemptEvent(reagent, "Poison", effect, FixedPoint2.New(threshold));
                    _server.EntMan.EventBus.RaiseLocalEvent(uid, ref ordinary);
                    Assert.That(ordinary.Cancelled, Is.False);

                    _server.System<SkillsSystem>().SetSkillLevel(uid, SharedSkillsSystem.VitalityId, 16);
                    var below = new MetabolismEffectAttemptEvent(reagent, "Poison", effect, FixedPoint2.New(threshold * 2) - FixedPoint2.New(0.01));
                    _server.EntMan.EventBus.RaiseLocalEvent(uid, ref below);
                    Assert.That(below.Cancelled, Is.True);
                    var at = new MetabolismEffectAttemptEvent(reagent, "Poison", effect, FixedPoint2.New(threshold * 2));
                    _server.EntMan.EventBus.RaiseLocalEvent(uid, ref at);
                    Assert.That(at.Cancelled, Is.False, "Reaching the doubled limit must restore every overdose effect.");
                }
            }
            finally
            {
                _server.EntMan.DeleteEntity(uid);
            }
        });
    }

    private ImperialStoreComponent PrepareBook(EntityUid uid)
    {
        var store = _server.EntMan.GetComponent<ImperialStoreComponent>(uid);
        // Nullspace does not run MapInit; a real map initializes these automatically.
        _server.System<ImperialStoreSystem>().RefreshAllListings(store);
        return store;
    }

    private void Buy(EntityUid uid, EntityUid book, string listingId)
    {
        var store = _server.EntMan.GetComponent<ImperialStoreComponent>(book);
        var listing = store.Listings.Single(x => x.ID == listingId);
        _server.EntMan.EventBus.RaiseLocalEvent(book, new ImperialStoreBuyListingMessage(listing) { Actor = uid });
    }

    [Test]
    public async Task LearningGrimoireUpgradesOnceAndPreservesPurchasesAndWallet()
    {
        await _server.WaitAssertion(() =>
        {
            var uid = SpawnCharacter();
            var normal = _server.EntMan.SpawnEntity("MedievalSpellBookBase", MapCoordinates.Nullspace);
            var extra = _server.EntMan.SpawnEntity("MedievalSpellBookBase", MapCoordinates.Nullspace);
            var manual = _server.EntMan.SpawnEntity("SkillLearningGrimoire", MapCoordinates.Nullspace);
            var magic = _server.System<SkillMagicSystem>();
            var books = _server.System<BindStoreOnEquipSystem>();
            EntityUid? learner = null;
            EntityUid? restored = null;
            try
            {
                magic.RegisterProfession(uid, null);
                _server.System<SkillsSystem>().SetSkillLevel(uid, SharedSkillsSystem.IntelligenceId, 20);
                Assert.That(_server.EntMan.HasComponent<GrimoireOwnerComponent>(uid), Is.False, "Ordinary refresh cannot grant a starting book.");
                Assert.That(books.TryBindGrimoire(manual, uid), Is.False, "Learning books cannot be manually bound.");
                magic.GrantStartingGrimoire(uid);
                var owner = _server.EntMan.GetComponent<GrimoireOwnerComponent>(uid);
                learner = owner.GrimoireUid;
                var oldStore = PrepareBook(learner.Value);
                Assert.That(oldStore.Balance.Values.All(x => x == FixedPoint2.Zero), Is.True);
                Assert.That(books.TryAddCurrency(uid, new Dictionary<EntProtoId, FixedPoint2>
                {
                    ["MagicMedievalFire"] = 500,
                    ["ArchmagePoints"] = 7,
                }), Is.True);

                Buy(uid, learner.Value, "MedievalSpellDivineTouchBeginner");
                Assert.That(_server.EntMan.GetComponent<SkillMagicComponent>(uid).FreeSpellClaimed, Is.True);
                var actions = oldStore.BoughtEntities.ToArray();
                Assert.That(actions, Is.Not.Empty);
                Buy(uid, learner.Value, "SkillArchmagicFire");
                Assert.That(oldStore.Balance["MagicMedievalFire"], Is.EqualTo(FixedPoint2.New(300)));
                Assert.That(oldStore.Balance["ArchmagePoints"], Is.EqualTo(FixedPoint2.New(8)));

                var newStore = PrepareBook(normal);
                var initialFire = newStore.Balance["MagicMedievalFire"];
                var initialArchmagic = newStore.Balance["ArchmagePoints"];
                Assert.That(books.TryBindGrimoire(normal, uid), Is.True);
                Assert.Multiple(() =>
                {
                    Assert.That(owner.GrimoireUid, Is.EqualTo(normal));
                    Assert.That(newStore.Balance["MagicMedievalFire"], Is.EqualTo(initialFire + 300));
                    Assert.That(newStore.Balance["ArchmagePoints"], Is.EqualTo(initialArchmagic + 8));
                    Assert.That(_server.EntMan.IsQueuedForDeletion(learner.Value), Is.True);
                    Assert.That(oldStore.AccountOwner, Is.Null);
                    Assert.That(newStore.BoughtEntities, Is.EquivalentTo(actions));
                    Assert.That(newStore.Listings.Single(x => x.ID == "MedievalSpellDivineTouchBeginner").PurchaseAmount, Is.EqualTo(1));
                    Assert.That(newStore.Listings.Single(x => x.ID == "MedievalSpellDivineTouchBeginner").Cost,
                        Is.EquivalentTo(_server.ProtoMan.Index<ImperialListingPrototype>("MedievalSpellDivineTouchBeginner").Cost));
                    Assert.That(newStore.Listings.Single(x => x.ID == "MedievalSpellDivineTouchMiddle").ProductActionEntity,
                        Is.EqualTo(actions.Single()), "The next tier must keep its link to the learned action.");
                    Assert.That(books.TryBindGrimoire(extra, uid), Is.False);
                    Assert.That(books.TryBindGrimoire(manual, uid, startingGrimoire: true), Is.False);
                });
                _server.EntMan.DeleteEntity(learner.Value);
                foreach (var action in actions)
                    Assert.That(_server.EntMan.GetComponent<ImperialStoreRefundComponent>(action).StoreEntity, Is.EqualTo(normal));

                Assert.That(books.TryAddCurrency(uid, new Dictionary<EntProtoId, FixedPoint2> { ["MagicMedievalFire"] = 20 }), Is.True);
                Buy(uid, normal, "SkillArchmagicFire");
                Assert.That(newStore.Balance["MagicMedievalFire"], Is.EqualTo(initialFire + 120));
                Assert.That(newStore.Balance["ArchmagePoints"], Is.EqualTo(initialArchmagic + 9));
                magic.GrantStartingGrimoire(uid);
                Assert.That(owner.GrimoireUid, Is.EqualTo(normal));

                var balance = new Dictionary<string, FixedPoint2>(newStore.Balance);
                _server.EntMan.DeleteEntity(normal);
                restored = _server.EntMan.SpawnEntity("MedievalSpellBookBase", MapCoordinates.Nullspace);
                PrepareBook(restored.Value);
                Assert.That(books.TryRestoreGrimoire(uid, restored.Value, owner), Is.True);
                var restoredStore = _server.EntMan.GetComponent<ImperialStoreComponent>(restored.Value);
                Assert.That(restoredStore.Balance, Is.EquivalentTo(balance));
                Assert.That(restoredStore.BoughtEntities, Is.EquivalentTo(actions));
                foreach (var action in actions)
                    Assert.That(_server.EntMan.GetComponent<ImperialStoreRefundComponent>(action).StoreEntity, Is.EqualTo(restored));
            }
            finally
            {
                foreach (var entity in new EntityUid?[] { restored, learner, normal, extra, manual, uid })
                    if (entity is { } existing && _server.EntMan.EntityExists(existing))
                        _server.EntMan.DeleteEntity(existing);
            }
        });
    }

    [Test]
    public async Task MageGetsSixtyRandomEssenceOnceAndNeverALearningBook()
    {
        await _server.WaitAssertion(() =>
        {
            var uid = SpawnCharacter();
            var book = _server.EntMan.SpawnEntity("MedievalSpellBookBase", MapCoordinates.Nullspace);
            try
            {
                var state = _server.EntMan.GetComponent<SkillMagicComponent>(uid);
                state.ProfessionKnown = true;
                state.ProfessionMage = true;
                _server.System<SkillsSystem>().SetSkillLevel(uid, SharedSkillsSystem.IntelligenceId, 20);
                _server.System<SkillMagicSystem>().GrantStartingGrimoire(uid);
                Assert.That(_server.EntMan.HasComponent<GrimoireOwnerComponent>(uid), Is.False,
                    "A mage must not receive a learning book while their ordinary one is still unbound.");
                var store = PrepareBook(book);
                var initial = new Dictionary<string, FixedPoint2>(store.Balance);
                Assert.That(_server.System<BindStoreOnEquipSystem>().TryBindGrimoire(book, uid), Is.True);
                var differences = store.Balance.Select(x => (x.Value - initial[x.Key]).Float()).Where(x => x != 0).ToArray();
                Assert.That(differences, Is.EqualTo(new[] { 60f }));
                var rewarded = new Dictionary<string, FixedPoint2>(store.Balance);
                _server.System<SkillMagicSystem>().GrantStartingGrimoire(uid);
                _server.System<SkillsSystem>().SetSkillLevel(uid, SharedSkillsSystem.IntelligenceId, 19);
                _server.System<SkillsSystem>().SetSkillLevel(uid, SharedSkillsSystem.IntelligenceId, 20);
                Assert.That(store.Balance, Is.EquivalentTo(rewarded));
                Assert.That(_server.EntMan.GetComponent<GrimoireOwnerComponent>(uid).GrimoireUid, Is.EqualTo(book));
                Assert.That(_server.EntMan.HasComponent<SkillLearningStoreComponent>(book), Is.False);
            }
            finally
            {
                _server.EntMan.DeleteEntity(book);
                _server.EntMan.DeleteEntity(uid);
            }
        });
    }

    [Test]
    public async Task ContactDodgeCancelsAllEffectsOfOneProjectile()
    {
        await _server.WaitAssertion(() =>
        {
            var uid = SpawnCharacter();
            var hazard = _server.EntMan.SpawnEntity(null, MapCoordinates.Nullspace);
            var firstProjectile = _server.EntMan.SpawnEntity(null, MapCoordinates.Nullspace);
            var secondProjectile = _server.EntMan.SpawnEntity(null, MapCoordinates.Nullspace);
            try
            {
                _server.System<SkillsSystem>().SetSkillLevel(uid, SharedSkillsSystem.AgilityId, 16);
                _server.EntMan.EnsureComponent<ProjectileComponent>(firstProjectile);
                _server.EntMan.EnsureComponent<ProjectileComponent>(secondProjectile);

                var stationaryContact = new BeforeAttackEffectsEvent(hazard, null, AttackDelivery.Contact);
                _server.EntMan.EventBus.RaiseLocalEvent(uid, ref stationaryContact);
                Assert.That(stationaryContact.Cancelled, Is.False, "Stationary hazards must not consume dodge.");

                var firstContact = new BeforeAttackEffectsEvent(firstProjectile, null, AttackDelivery.Contact);
                _server.EntMan.EventBus.RaiseLocalEvent(uid, ref firstContact);
                Assert.That(firstContact.Cancelled, Is.True, "A contact effect must be cancelled before it runs.");

                var sameProjectileDamage = new BeforeAttackEffectsEvent(firstProjectile, null, AttackDelivery.Projectile);
                _server.EntMan.EventBus.RaiseLocalEvent(uid, ref sameProjectileDamage);
                Assert.That(sameProjectileDamage.Cancelled, Is.True, "Damage and contact effects share one dodge.");

                var secondContact = new BeforeAttackEffectsEvent(secondProjectile, null, AttackDelivery.Contact);
                _server.EntMan.EventBus.RaiseLocalEvent(uid, ref secondContact);
                Assert.That(secondContact.Cancelled, Is.False, "A different projectile must respect the cooldown.");
            }
            finally
            {
                _server.EntMan.DeleteEntity(firstProjectile);
                _server.EntMan.DeleteEntity(secondProjectile);
                _server.EntMan.DeleteEntity(hazard);
                _server.EntMan.DeleteEntity(uid);
            }
        });
    }

    [Test]
    public async Task RecoveryWorksDuringStaminaCritical()
    {
        await _server.WaitAssertion(() =>
        {
            var uid = SpawnCharacter();
            try
            {
                var stamina = _server.EntMan.GetComponent<StaminaComponent>(uid);
                var system = _server.System<SharedStaminaSystem>();
                system.TakeStaminaDamage(uid, stamina.CritThreshold, stamina, ignoreResist: true, visual: false);
                Assert.That(stamina.Critical, Is.True, "The character must be exhausted before recovery.");

                system.RestoreStamina(uid, stamina);
                Assert.Multiple(() =>
                {
                    Assert.That(stamina.StaminaDamage, Is.Zero);
                    Assert.That(stamina.Critical, Is.False);
                    Assert.That(stamina.AfterCritical, Is.False);
                    Assert.That(_server.EntMan.HasComponent<ActiveStaminaComponent>(uid), Is.False);
                });
            }
            finally
            {
                _server.EntMan.DeleteEntity(uid);
            }
        });
    }

    [Test]
    public async Task EnduranceConversionIncludesExactlyTwentyPercentRemaining()
    {
        await _server.WaitAssertion(() =>
        {
            var uid = SpawnCharacter();
            try
            {
                _server.System<SkillsSystem>().SetSkillLevel(uid, SharedSkillsSystem.EnduranceId, 20);
                var stamina = _server.EntMan.GetComponent<StaminaComponent>(uid);
                var system = _server.System<SharedStaminaSystem>();
                var endurance = _server.System<SkillEnduranceSystem>();
                system.TakeStaminaDamage(uid, stamina.CritThreshold * 0.8f, stamina, ignoreResist: true, visual: false);
                Assert.That(stamina.StaminaDamage, Is.EqualTo(stamina.CritThreshold * 0.8f).Within(0.0001f));
                Assert.That(endurance.Converts(uid), Is.True, "Exactly 20% must still allow conversion.");

                system.TakeStaminaDamage(uid, 0.01f, stamina, ignoreResist: true, visual: false);
                Assert.That(endurance.Converts(uid), Is.False, "Below 20% ordinary control must resume.");

                system.RestoreStamina(uid, stamina);
                Assert.That(endurance.Converts(uid), Is.True);
            }
            finally
            {
                _server.EntMan.DeleteEntity(uid);
            }
        });
    }

    [Test]
    public async Task VitalityThresholdsDoNotDependOnLevelHistory()
    {
        await _server.WaitAssertion(() =>
        {
            var uid = SpawnCharacter();
            try
            {
                var skills = _server.System<SkillsSystem>();
                var thresholds = _server.EntMan.GetComponent<MobThresholdsComponent>(uid);
                var baseline = thresholds.Thresholds.ToArray();
                skills.SetSkillLevel(uid, SharedSkillsSystem.VitalityId, 20);
                var legendary = thresholds.Thresholds.ToArray();
                Assert.That(legendary, Is.Not.EquivalentTo(baseline));

                skills.SetSkillLevel(uid, SharedSkillsSystem.VitalityId, 16);
                skills.SetSkillLevel(uid, SharedSkillsSystem.VitalityId, 20);
                Assert.That(thresholds.Thresholds, Is.EquivalentTo(legendary));

                skills.SetSkills(uid, new Dictionary<string, int>(_server.EntMan.GetComponent<SkillsComponent>(uid).Levels));
                Assert.That(thresholds.Thresholds, Is.EquivalentTo(legendary), "Reapplying a profile must not stack health.");

                skills.SetSkillLevel(uid, SharedSkillsSystem.VitalityId, 10);
                Assert.That(thresholds.Thresholds, Is.EquivalentTo(baseline));
            }
            finally
            {
                _server.EntMan.DeleteEntity(uid);
            }
        });
    }

    [Test]
    public async Task ManaCapacityDoesNotCompoundOrRefill()
    {
        await _server.WaitAssertion(() =>
        {
            var uid = SpawnCharacter();
            try
            {
                var skills = _server.System<SkillsSystem>();
                var mana = _server.EntMan.EnsureComponent<ManaComponent>(uid);
                var baselineMaximum = mana.MaxMana;
                mana.Mana = 25f;

                skills.SetSkillLevel(uid, SharedSkillsSystem.IntelligenceId, 20);
                var legendaryMaximum = mana.MaxMana;
                Assert.That(legendaryMaximum, Is.GreaterThan(baselineMaximum));
                skills.SetSkillLevel(uid, SharedSkillsSystem.IntelligenceId, 19);
                Assert.That(mana.MaxMana, Is.LessThan(legendaryMaximum));

                skills.SetSkillLevel(uid, SharedSkillsSystem.IntelligenceId, 20);
                Assert.That(mana.MaxMana, Is.EqualTo(legendaryMaximum).Within(0.001f));
                skills.SetSkillLevel(uid, SharedSkillsSystem.IntelligenceId, 10);
                Assert.Multiple(() =>
                {
                    Assert.That(mana.MaxMana, Is.EqualTo(baselineMaximum).Within(0.001f));
                    Assert.That(mana.Mana, Is.EqualTo(25f), "Changing intelligence must not refill spent mana.");
                });
            }
            finally
            {
                _server.EntMan.DeleteEntity(uid);
            }
        });
    }
}
