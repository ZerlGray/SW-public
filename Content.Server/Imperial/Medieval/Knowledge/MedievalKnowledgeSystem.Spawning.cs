using System.Linq;
using Content.Server.MagicBarrier;
using Content.Server.MagicBarrier.Components;
using Content.Server.Store.Components;
using Content.Shared.GameTicking;
using Content.Shared.Imperial.Medieval.Knowledge;
using Content.Shared.Imperial.Medieval.Trading;
using Content.Shared.Paper;
using Robust.Shared.Map;
using Robust.Shared.Random;
using Robust.Shared.Timing;

namespace Content.Server.Imperial.Medieval.Knowledge;

public sealed partial class MedievalKnowledgeSystem
{
    [Dependency] private readonly IRobustRandom _random = default!;
    private readonly HashSet<MapId> _seededMaps = new();

    private void InitializeSpawning()
    {
        SubscribeLocalEvent<MagicBarrierComponent, MapInitEvent>(OnBarrierInit);
        SubscribeLocalEvent<RoundRestartCleanupEvent>(_ => _seededMaps.Clear());
    }

    private void OnBarrierInit(Entity<MagicBarrierComponent> ent, ref MapInitEvent args)
    {
        // The map's loot markers may not have completed startup alongside the barrier yet.
        Timer.Spawn(1000, () =>
        {
            if (Deleted(ent))
                return;
            var map = Transform(ent).MapID;
            if (map == MapId.Nullspace || _seededMaps.Contains(map))
                return;
            var markers = EntityQuery<NecroBookSpawnComponent, TransformComponent>(includePaused: true)
                .Where(pair => pair.Item2.MapID == map).Select(pair => pair.Item2.Coordinates).ToList();
            if (markers.Count == 0)
                return;
            _seededMaps.Add(map);
            var candidates = _prototypes.EnumeratePrototypes<MedievalKnowledgePrototype>().ToList();
            // Finite world loot, deliberately separate from replenishing trader inventories.
            for (var i = 0; i < 6 && candidates.Count > 0; i++)
            {
                var weighted = candidates.SelectMany(p => Enumerable.Repeat(p, p.Tier switch { 1 => 8, 2 => 5, 3 => 2, _ => 1 })).ToList();
                var knowledge = _random.Pick(weighted);
                candidates.Remove(knowledge);
                var uid = Spawn("MedievalKnowledgeBook", _random.Pick(markers));
                var book = Comp<LearnableBookComponent>(uid);
                book.Knowledge = knowledge.ID;
                book.Language = knowledge.Language == "Common" ? "Elf" : knowledge.Language != null ? "Common" :
                    knowledge.Tier == 4 ? "Ancient" : _random.Pick(new[] { "Common", "Common", "Elf", "Orc" });
                book.Encrypted = knowledge.Tier == 4;
                book.StudySeconds = 60 + 60 * knowledge.Tier;
                book.TranslationSeconds = 45 * knowledge.Tier;
                Dirty(uid, book);
                var price = knowledge.Tier switch { 1 => 200, 2 => 400, 3 => 800, _ => 1400 };
                EnsureComp<CurrencyComponent>(uid).Price["Revent"] = price;
                EnsureComp<MedievalCurrencyComponent>(uid).Price["Revent"] = price;
                _metadata.SetEntityName(uid, Loc.GetString(knowledge.Name));
                _metadata.SetEntityDescription(uid, Loc.GetString("knowledge-original-description"));
                _paper.SetContent((uid, Comp<PaperComponent>(uid)), book.Encrypted
                    ? Loc.GetString("knowledge-encrypted-content")
                    : Loc.GetString(knowledge.Description));
            }
        });
    }
}
