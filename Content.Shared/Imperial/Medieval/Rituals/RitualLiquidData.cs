using Content.Shared.Chemistry.Reagent;
using Robust.Shared.Serialization;

namespace Content.Shared.Imperial.Medieval.Rituals;

/// <summary>Magic belongs to the actual reagent volume, so pouring and splitting conserve it.</summary>
[Serializable, NetSerializable]
public sealed partial class RitualLiquidData : ReagentData
{
    [DataField] public string Kind = string.Empty;
    [DataField] public int Tier;
    [DataField] public TimeSpan Until;

    public override ReagentData Clone() => new RitualLiquidData { Kind = Kind, Tier = Tier, Until = Until };
    public override bool Equals(ReagentData? other) => other is RitualLiquidData data && data.Kind == Kind && data.Tier == Tier && data.Until == Until;
    public override int GetHashCode() => HashCode.Combine(nameof(RitualLiquidData), Kind, Tier, Until);
}
