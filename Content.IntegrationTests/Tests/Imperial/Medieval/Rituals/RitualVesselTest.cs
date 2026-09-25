using Content.Server.Imperial.Medieval.Rituals;
using Content.Shared.Chemistry.Components;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.Damage;
using Content.Shared.FixedPoint;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Imperial.Medieval.Rituals;
using Robust.Shared.GameObjects;
using Robust.Shared.Timing;

namespace Content.IntegrationTests.Tests.Imperial.Medieval.Rituals;

[TestFixture]
public sealed class RitualVesselTest
{
    [Test]
    public async Task ExpiringArmourPreservesOriginalGearAndItemsAddedLater()
    {
        await using var pair = await PoolManager.GetServerClient();
        var map = await pair.CreateTestMap();
        var em = pair.Server.ResolveDependency<IEntityManager>();
        EntityUid owner = default, armour = default, weapon = default, added = default, shell = default;
        await pair.Server.WaitAssertion(() =>
        {
            owner = em.SpawnEntity("MobHuman", map.GridCoords);
            armour = em.SpawnEntity("ClothingOuterArmorBasic", map.GridCoords);
            weapon = em.SpawnEntity("Crowbar", map.GridCoords);
        });
        await pair.RunTicksSync(2);
        await pair.Server.WaitAssertion(() =>
        {
            var animation = em.System<MedievalRitualAnimatedSystem>();
            Assert.That(animation.CanApplyGift(armour, "animation"), Is.True);
            animation.ApplyGift(armour, "animation", owner, 2,
                pair.Server.ResolveDependency<IGameTiming>().CurTime + TimeSpan.FromMinutes(1));
            shell = em.GetComponent<TransformComponent>(armour).ParentUid;
            var effect = em.GetComponent<RitualAnimatedComponent>(shell);
            Assert.That(effect.Shell, Is.True);
            em.System<DamageableSystem>().TryChangeDamage(shell,
                new DamageSpecifier { DamageDict = new() { { "Blunt", 5 } } });
            Assert.That(em.GetComponent<DamageableComponent>(shell).TotalDamage.Float(), Is.GreaterThan(0),
                "An animated item must not inherit the player's temporary spawn invulnerability.");
            added = em.SpawnEntity("d6Dice", map.GridCoords);
            Assert.That(em.System<SharedHandsSystem>().TryPickupAnyHand(shell, added), Is.True);
            effect.Until = TimeSpan.Zero;
        });
        await pair.RunTicksSync(5);
        await pair.Server.WaitAssertion(() =>
        {
            foreach (var item in new[] { armour, weapon, added })
            {
                Assert.That(em.EntityExists(item), Is.True);
                Assert.That(em.GetComponent<TransformComponent>(item).ParentUid, Is.Not.EqualTo(shell));
            }
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task LinkedVesselsTransferActualContentsAndRespectCapacityAndSuppression()
    {
        await using var pair = await PoolManager.GetServerClient();
        var map = await pair.CreateTestMap();
        var em = pair.Server.ResolveDependency<IEntityManager>();
        await pair.Server.WaitAssertion(() =>
        {
            var first = em.SpawnEntity("Beaker", map.GridCoords);
            var second = em.SpawnEntity("Beaker", map.GridCoords);
            var owner = em.SpawnEntity("MobHuman", map.GridCoords);
            var solutions = em.System<SharedSolutionContainerSystem>();
            Assert.That(solutions.TryGetDrainableSolution(first, out var firstEntity, out var a), Is.True);
            Assert.That(solutions.TryGetDrainableSolution(second, out var secondEntity, out var b), Is.True);
            b.MaxVolume = FixedPoint2.New(6);
            var until = pair.Server.ResolveDependency<IGameTiming>().CurTime + TimeSpan.FromMinutes(1);
            var links = em.System<MedievalRitualAnimatedSystem>();
            links.ApplyGift(first, "vessels", owner, 2, until);
            Assert.That(links.Link(first, second), Is.True);
            Assert.That(links.Link(first, second), Is.True, "Re-selecting the same pair must preserve it.");
            Assert.That(solutions.TryAddSolution(firstEntity!.Value, new Solution("Water", 10)), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(a.Volume, Is.EqualTo(FixedPoint2.New(4)));
                Assert.That(b.Volume, Is.EqualTo(FixedPoint2.New(6)));
                Assert.That(a.Volume + b.Volume, Is.EqualTo(FixedPoint2.New(10)));
            });
            solutions.SplitSolution(secondEntity!.Value, FixedPoint2.New(6));
            var field = em.EnsureComponent<MedievalAntimagicFieldComponent>(first);
            field.Until = until;
            Assert.That(solutions.TryAddSolution(firstEntity.Value, new Solution("Water", 3)), Is.True);
            Assert.That(b.Volume, Is.EqualTo(FixedPoint2.Zero));
            em.RemoveComponent<MedievalAntimagicFieldComponent>(first);
            Assert.That(solutions.TryAddSolution(firstEntity.Value, new Solution("Water", 2)), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(a.Volume, Is.EqualTo(FixedPoint2.New(7)));
                Assert.That(b.Volume, Is.EqualTo(FixedPoint2.New(2)));
                Assert.That(a.Volume + b.Volume, Is.EqualTo(FixedPoint2.New(9)));
            });
        });
        await pair.CleanReturnAsync();
    }
}
