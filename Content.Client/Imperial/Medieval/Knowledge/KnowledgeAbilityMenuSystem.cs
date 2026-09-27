using System.Linq;
using Content.Client.Imperial.Medieval.Abilities;
using Content.Shared.Imperial.Medieval.Knowledge;
using Robust.Shared.GameStates;
using Robust.Shared.Prototypes;

namespace Content.Client.Imperial.Medieval.Knowledge;

/// <summary>Contributes book descriptions and passive knowledge to the shared ability menu.</summary>
public sealed class KnowledgeAbilityMenuSystem : EntitySystem
{
    [Dependency] private readonly AbilityMenuSystem _menu = default!;
    [Dependency] private readonly IPrototypeManager _prototypes = default!;

    public override void Initialize()
    {
        SubscribeLocalEvent<LearnedKnowledgeComponent, GetAbilityMenuEntriesEvent>(OnEntries);
        SubscribeLocalEvent<LearnedKnowledgeComponent, AfterAutoHandleStateEvent>(OnKnowledgeState);
    }

    private void OnKnowledgeState(Entity<LearnedKnowledgeComponent> ent, ref AfterAutoHandleStateEvent args)
    {
        _menu.Refresh(ent.Owner);
    }

    private void OnEntries(EntityUid uid, LearnedKnowledgeComponent component, GetAbilityMenuEntriesEvent args)
    {
        var actions = args.Actions.Keys.ToArray();
        foreach (var id in component.Knowledge)
        {
            if (!_prototypes.TryIndex<MedievalKnowledgePrototype>(id, out var knowledge))
                continue;

            var entry = new AbilityMenuEntry(Loc.GetString(knowledge.Name), Loc.GetString(knowledge.Description),
                knowledge.Icon, knowledge.Tier);
            if (knowledge.Actions.Count == 0)
            {
                args.Passives.Add(entry);
                continue;
            }

            foreach (var action in actions)
            {
                if (knowledge.Actions.Any(prototype => prototype.Id == MetaData(action).EntityPrototype?.ID))
                    args.Actions[action] = entry;
            }
        }
    }
}
