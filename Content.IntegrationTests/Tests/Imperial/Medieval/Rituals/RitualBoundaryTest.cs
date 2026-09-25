using System.Numerics;
using Content.Server.Imperial.Medieval.Rituals;
using Content.Shared.Actions.Components;
using Content.Shared.Imperial.Medieval.Magic;
using Content.Shared.Imperial.Medieval.Magic.Mana;
using Content.Shared.Imperial.Medieval.Rituals;
using Content.Shared.Projectiles;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Timing;

namespace Content.IntegrationTests.Tests.Imperial.Medieval.Rituals;

[TestFixture]
public sealed class RitualBoundaryTest
{
    [TestPrototypes]
    private const string Prototypes = """
- type: entity
  id: BookTestDelayedSpell
  components:
  - type: Action
  - type: TargetAction
  - type: WorldTargetAction
    event: !type:MedievalProjectileSpellEvent
      projectilePrototype:
        0: MedievalProjectileMagicArrowBeginner
      spellCastDoAfter:
        delay: 0.2
  - type: ManaDrainSpell
    manaDrain: 5
""";

    [Test]
    public void BoundaryGeometryIncludesCrossingSegmentsButExcludesOtherMaps()
    {
        var map = new MapId(1);
        var center = new MapCoordinates(Vector2.Zero, map);
        var left = new MapCoordinates(new Vector2(-5, 0), map);
        var right = new MapCoordinates(new Vector2(5, 0), map);
        Assert.Multiple(() =>
        {
            Assert.That(SharedRitualMagicSystem.Intersects(left, right, center, 2), Is.True);
            Assert.That(SharedRitualMagicSystem.CrossesBoundary(center, center, center, 2), Is.False);
            Assert.That(SharedRitualMagicSystem.Intersects(left, right, new MapCoordinates(Vector2.Zero, new MapId(2)), 2), Is.False);
            Assert.That(SharedRitualMagicSystem.Intersects(new MapCoordinates(new Vector2(-5, 4), map), new MapCoordinates(new Vector2(5, 4), map), center, 2), Is.False);
        });
    }

    [Test]
    public async Task SmallAntimagicCircleAllowsIncomingProjectileAndFullCircleStopsIt()
    {
        await using var pair = await PoolManager.GetServerClient();
        var map = await pair.CreateTestMap();
        var entities = pair.Server.ResolveDependency<IEntityManager>();
        await pair.Server.WaitAssertion(() =>
        {
            var fieldEntity = entities.SpawnEntity(null, map.GridCoords);
            var field = entities.EnsureComponent<MedievalAntimagicFieldComponent>(fieldEntity);
            field.Radius = 3;
            field.Until = pair.Server.ResolveDependency<IGameTiming>().CurTime + TimeSpan.FromMinutes(1);
            var projectile = entities.SpawnEntity("MedievalProjectileMagicArrowBeginner", map.GridCoords.Offset(new Vector2(-5, 0)));
            entities.EnsureComponent<RitualMagicProjectileComponent>(projectile);
            var transform = entities.System<SharedTransformSystem>();
            transform.SetCoordinates(projectile, map.GridCoords.Offset(new Vector2(-2, 0)));
            transform.SetCoordinates(projectile, map.GridCoords);
            Assert.That(entities.GetComponent<ProjectileComponent>(projectile).ProjectileSpent, Is.False);
            field.BlockPassage = true;
            transform.SetCoordinates(projectile, map.GridCoords.Offset(new Vector2(1, 0)));
            Assert.That(entities.GetComponent<ProjectileComponent>(projectile).ProjectileSpent, Is.True);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task FieldAppearingDuringCastCancelsCompletionAndReleasesManaReservation()
    {
        await using var pair = await PoolManager.GetServerClient();
        var map = await pair.CreateTestMap();
        var entities = pair.Server.ResolveDependency<IEntityManager>();
        EntityUid caster = default, action = default;
        float before = 0;
        await pair.Server.WaitAssertion(() =>
        {
            caster = entities.SpawnEntity("MobHuman", map.GridCoords);
            var mana = entities.EnsureComponent<ManaComponent>(caster);
            mana.Mana = 100;
            before = mana.Mana;
            action = entities.SpawnEntity("BookTestDelayedSpell", map.GridCoords);
            var spell = (MedievalProjectileSpellEvent) entities.GetComponent<WorldTargetActionComponent>(action).Event!;
            spell.Performer = caster;
            spell.Action = action;
            spell.Target = map.GridCoords.Offset(new Vector2(5, 0));
            entities.EventBus.RaiseLocalEvent(action, spell, true);
            Assert.That(mana.CastedSpells.ContainsKey(action), Is.True);
            var center = entities.SpawnEntity(null, map.GridCoords);
            var field = entities.EnsureComponent<MedievalAntimagicFieldComponent>(center);
            field.Until = pair.Server.ResolveDependency<IGameTiming>().CurTime + TimeSpan.FromMinutes(1);
        });
        await pair.RunTicksSync(60);
        await pair.Server.WaitAssertion(() =>
        {
            var mana = entities.GetComponent<ManaComponent>(caster);
            Assert.That(mana.CastedSpells.ContainsKey(action), Is.False);
            Assert.That(mana.Mana, Is.EqualTo(before));
        });
        await pair.CleanReturnAsync();
    }
}
