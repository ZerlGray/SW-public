using Content.Shared.FixedPoint;
using Robust.Shared.Serialization;

namespace Content.Shared.Chemistry.Reagent;

[ImplicitDataDefinitionForInheritors, Serializable, NetSerializable]
public sealed partial class DnaData : ReagentData
{
    [DataField]
    public string DNA = String.Empty;

    public override ReagentData Clone() => this;

    public override bool Equals(ReagentData? other)
    {
        if (other is not DnaData dna)
        {
            return false;
        }

        return dna.DNA == DNA;
    }

    public override int GetHashCode()
    {
        return DNA.GetHashCode();
    }
}
