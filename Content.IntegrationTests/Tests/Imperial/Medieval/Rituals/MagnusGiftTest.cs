using System.Numerics;
using Content.Server.Imperial.Medieval.Rituals;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Imperial.Medieval.Rituals;
using Robust.Shared.GameObjects;
using Robust.Shared.Physics.Components;
using Robust.Shared.Timing;

namespace Content.IntegrationTests.Tests.Imperial.Medieval.Rituals;

[TestFixture]
public sealed class MagnusGiftTest
{
    [Test]
    public async Task OfferingsChoosePhenomenonAndOnlyGreatSourceCombinesThem()
    {
        await using var pair = await PoolManager.GetServerClient();
        var map = await pair.CreateTestMap();
        var em = pair.Server.ResolveDependency<IEntityManager>();
        await pair.Server.WaitAssertion(() =>
        {
            var caster = em.SpawnEntity("MobHuman", map.GridCoords);
            var center = em.SpawnEntity("MedievalRitualCenter", map.GridCoords);
            var weapon = em.SpawnEntity("Crowbar", map.GridCoords);
            var thread = em.SpawnEntity("Paper", map.GridCoords);
            var hammer = em.SpawnEntity("Paper", map.GridCoords);
            em.EnsureComponent<RitualOfferingComponent>(thread).Kind = "recall";
            em.EnsureComponent<RitualOfferingComponent>(hammer).Kind = "thunder";
            MedievalRitualContext Make(int tier, params EntityUid[] offerings) => new()
            {
                Caster = caster, Center = center, Target = weapon,
                Ritual = new MedievalRitualPrototype { Effect = "MagnusCreation" + tier, Tier = tier },
                Participants = new[] { caster }, Offerings = offerings,
            };
            var small = Make(2, thread);
            var validation = new MedievalRitualValidateEvent(small);
            em.EventBus.RaiseEvent(EventSource.Local, validation);
            Assert.That(validation.Error, Is.Null);
            em.EventBus.RaiseEvent(EventSource.Local, new MedievalRitualExecuteEvent(small));
            Assert.That(em.HasComponent<MagnusRecallComponent>(weapon), Is.True);
            Assert.That(em.HasComponent<MagnusThunderComponent>(weapon), Is.False);
            foreach (var tier in new[] { 2, 3, 4 })
            {
                var combination = new MedievalRitualValidateEvent(Make(tier, thread, hammer));
                em.EventBus.RaiseEvent(EventSource.Local, combination);
                Assert.That(combination.Error == null, Is.EqualTo(tier == 4));
            }
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task RecallMovesOriginalAndSuppressionOrAnotherHolderDoesNotSpendCharges()
    {
        await using var pair = await PoolManager.GetServerClient();
        var map = await pair.CreateTestMap();
        var em = pair.Server.ResolveDependency<IEntityManager>();
        await pair.Server.WaitAssertion(() =>
        {
            var user = em.SpawnEntity("MobHuman", map.GridCoords);
            var other = em.SpawnEntity("MobHuman", map.GridCoords.Offset(new Vector2(6, 0)));
            var item = em.SpawnEntity("Crowbar", map.GridCoords.Offset(new Vector2(6, 0)));
            var comp = em.EnsureComponent<MagnusRecallComponent>(item);
            comp.Master = user;
            comp.Charges = 2;
            comp.Until = pair.Server.ResolveDependency<IGameTiming>().CurTime + TimeSpan.FromMinutes(1);
            var field = em.EnsureComponent<MedievalAntimagicFieldComponent>(user);
            field.Until = comp.Until;
            var gifts = em.System<MedievalMagnusSystem>();
            var hands = em.System<SharedHandsSystem>();
            Assert.That(gifts.TryRecall(user, item), Is.False);
            Assert.That(comp.Charges, Is.EqualTo(2));
            em.RemoveComponent<MedievalAntimagicFieldComponent>(user);
            Assert.That(hands.TryPickupAnyHand(other, item), Is.True);
            Assert.That(gifts.TryRecall(user, item), Is.False);
            Assert.That(comp.Charges, Is.EqualTo(2));
            Assert.That(hands.TryDrop(other, item), Is.True);
            Assert.That(gifts.TryRecall(user, item), Is.True);
            Assert.That(hands.IsHolding(user, item, out _), Is.True);
            Assert.That(comp.Charges, Is.EqualTo(1));
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task ExpiringPassageWaitsForOccupantThenRestoresSameWall()
    {
        await using var pair = await PoolManager.GetServerClient();
        var map = await pair.CreateTestMap();
        var em = pair.Server.ResolveDependency<IEntityManager>();
        EntityUid wall = default, occupant = default;
        await pair.Server.WaitAssertion(() =>
        {
            occupant = em.SpawnEntity("MobHuman", map.GridCoords);
            wall = em.SpawnEntity("WallSolid", map.GridCoords);
            var gift = em.EnsureComponent<MagnusPhasedComponent>(wall);
            gift.OriginalCollision = true;
            gift.Active = true;
            gift.Until = TimeSpan.Zero;
            em.System<Robust.Shared.Physics.Systems.SharedPhysicsSystem>().SetCanCollide(wall, false);
        });
        await pair.RunTicksSync(30);
        await pair.Server.WaitAssertion(() =>
        {
            Assert.That(em.GetComponent<PhysicsComponent>(wall).CanCollide, Is.False);
            em.System<SharedTransformSystem>().SetCoordinates(occupant, map.GridCoords.Offset(new Vector2(4, 0)));
        });
        await pair.RunTicksSync(30);
        await pair.Server.WaitAssertion(() =>
        {
            Assert.That(em.EntityExists(wall), Is.True);
            Assert.That(em.GetComponent<PhysicsComponent>(wall).CanCollide, Is.True);
            Assert.That(em.HasComponent<MagnusPhasedComponent>(wall), Is.False);
        });
        await pair.CleanReturnAsync();
    }
}
