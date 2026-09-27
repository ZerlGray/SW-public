using Content.Shared.FixedPoint;
using Content.Shared.Mobs;
using Robust.Shared.GameStates;
using Robust.Shared.Serialization.TypeSerializers.Implementations.Custom;

namespace Content.Shared.Imperial.Medieval.Skills;

[RegisterComponent, NetworkedComponent, AutoGenerateComponentState, AutoGenerateComponentPause]
public sealed partial class SkillEnduranceComponent : Component
{
    [DataField(serverOnly: true)] public float? BaseStamina;
    [DataField(customTypeSerializer: typeof(TimeOffsetSerializer)), AutoNetworkedField, AutoPausedField] public TimeSpan ControlProtectionUntil;
    [DataField(customTypeSerializer: typeof(TimeOffsetSerializer)), AutoNetworkedField, AutoPausedField] public TimeSpan ConvertedKnockdownUntil;
    public float ConvertedSlowdown;
}
