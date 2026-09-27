using Content.Shared.Imperial.Medieval.Skills;

namespace Content.Server.Imperial.Medieval.Skills.Progression;

[RegisterComponent]
public sealed partial class SkillStrengthEquipmentComponent : Component
{
    [DataField] public int AppliedStrength = SkillScaling.Baseline;
}
