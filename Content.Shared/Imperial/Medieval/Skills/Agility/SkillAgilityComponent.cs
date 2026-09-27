using Content.Shared.FixedPoint;
using Content.Shared.Mobs;
using Robust.Shared.GameStates;
using Robust.Shared.Serialization.TypeSerializers.Implementations.Custom;

namespace Content.Shared.Imperial.Medieval.Skills;

[RegisterComponent, NetworkedComponent, AutoGenerateComponentState, AutoGenerateComponentPause]
public sealed partial class SkillAgilityComponent : Component
{
    [DataField(serverOnly: true)] public Dictionary<EntityUid, TimeSpan> HiddenTheftUntil = new();
    [DataField(customTypeSerializer: typeof(TimeOffsetSerializer)), AutoNetworkedField, AutoPausedField] public TimeSpan NextDodge;
    [DataField(customTypeSerializer: typeof(TimeOffsetSerializer)), AutoNetworkedField, AutoPausedField] public TimeSpan JumpRecharge;
    [DataField, AutoNetworkedField] public int JumpsUsed;
    [DataField(customTypeSerializer: typeof(TimeOffsetSerializer)), AutoNetworkedField, AutoPausedField] public TimeSpan JumpUntil;
}
