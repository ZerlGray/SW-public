using Robust.Shared.Timing;
using Robust.Shared.GameStates;

namespace Content.Shared.Imperial.Medieval.Rituals;

public sealed class MedievalRitualProtectionSystem : EntitySystem
{
    [Dependency] private readonly IGameTiming _timing = default!;
    public override void Initialize()
    {
        SubscribeLocalEvent<MedievalValtorEnduranceComponent, MedievalExhaustionCheckEvent>(OnExhaustion);
    }
    private void OnExhaustion(EntityUid uid, MedievalValtorEnduranceComponent comp, ref MedievalExhaustionCheckEvent args)
    {
        args.IgnoreExhaustion |= comp.Until > _timing.CurTime;
    }
}

[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class MedievalSomaSanctuaryComponent : Component
{
    [DataField, AutoNetworkedField] public float Radius = 5;
    [DataField, AutoNetworkedField] public TimeSpan Until;
}
