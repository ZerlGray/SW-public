using Content.Server.Administration.Logs;
using Content.Shared.Database;

namespace Content.Server.Imperial.Medieval.Rituals;

public sealed class MedievalRitualLogSystem : EntitySystem
{
    [Dependency] private readonly IAdminLogManager _log = default!;

    public override void Initialize() => SubscribeLocalEvent<MedievalRitualExecuteEvent>(OnExecute);

    private void OnExecute(MedievalRitualExecuteEvent args)
    {
        var c = args.Context;
        _log.Add(LogType.Action, LogImpact.High,
            $"{ToPrettyString(c.Caster):actor} completed ritual {c.Ritual.ID} ({c.Ritual.Effect}) at {ToPrettyString(c.Center):center}, targeting {ToPrettyString(c.Target):target}, with {c.Participants.Count} participants and {c.Offerings.Count} offering entities.");
    }
}
