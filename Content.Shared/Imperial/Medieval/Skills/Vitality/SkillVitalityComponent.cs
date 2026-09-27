using Content.Shared.FixedPoint;
using Content.Shared.Mobs;
using Robust.Shared.GameStates;
using Robust.Shared.Serialization.TypeSerializers.Implementations.Custom;

namespace Content.Shared.Imperial.Medieval.Skills;

[RegisterComponent, NetworkedComponent]
public sealed partial class SkillVitalityComponent : Component
{
    [DataField(serverOnly: true)] public Dictionary<MobState, FixedPoint2> BaseHealthThresholds = new();
    [DataField(serverOnly: true)] public float? BaseSoftCritThreshold;
}
