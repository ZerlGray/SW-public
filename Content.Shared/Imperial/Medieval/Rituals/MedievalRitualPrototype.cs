using Robust.Shared.Prototypes;

namespace Content.Shared.Imperial.Medieval.Rituals;

/// <summary>A learned prayer. Offerings are local, real items, never a global rune count.</summary>
[Prototype("medievalRitual")]
public sealed partial class MedievalRitualPrototype : IPrototype
{
    [IdDataField] public string ID { get; private set; } = default!;
    [DataField(required: true)] public string Name = string.Empty;
    [DataField(required: true)] public string Knowledge = string.Empty;
    [DataField(required: true)] public string Effect = string.Empty;
    [DataField] public string Description = string.Empty;
    [DataField] public int Tier = 2;
    [DataField] public float Duration = 30;
    [DataField] public int Participants = 1;
    [DataField] public float Radius = 4;
    [DataField] public Dictionary<string, int> Offerings = new();
    [DataField] public Dictionary<string, int> OptionalOfferings = new();
    [DataField] public List<string> OptionalKinds = new();
    [DataField] public Dictionary<string, float> Reagents = new();
}
