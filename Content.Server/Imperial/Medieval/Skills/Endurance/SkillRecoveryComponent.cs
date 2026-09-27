using Robust.Shared.Serialization.TypeSerializers.Implementations.Custom;
using Robust.Shared.GameStates;

namespace Content.Server.Imperial.Medieval.Skills.Progression;

[RegisterComponent, AutoGenerateComponentPause]
public sealed partial class SkillRecoveryComponent : Component
{
    [DataField] public EntityUid? RecoveryAction;
    [DataField(customTypeSerializer: typeof(TimeOffsetSerializer)), AutoPausedField] public TimeSpan RecoveryReadyAt;
}
