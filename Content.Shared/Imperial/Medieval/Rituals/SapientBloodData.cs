using Content.Shared.Chemistry.Reagent;
using Robust.Shared.Serialization;

namespace Content.Shared.Imperial.Medieval.Rituals;

/// <summary>Records the real blood donor, independently of later disguises or the donor's survival.</summary>
[Serializable, NetSerializable]
public sealed partial class SapientBloodData : ReagentData
{
    public override ReagentData Clone() => new SapientBloodData();
    public override bool Equals(ReagentData? other) => other is SapientBloodData;
    public override int GetHashCode() => typeof(SapientBloodData).GetHashCode();
}
