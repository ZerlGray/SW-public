using Robust.Shared.Prototypes;

namespace Content.Shared.Imperial.Medieval.Knowledge;

/// <summary>A learned rule, independent of the physical edition which teaches it.</summary>
[Prototype("medievalKnowledge")]
public sealed partial class MedievalKnowledgePrototype : IPrototype
{
    [IdDataField] public string ID { get; private set; } = default!;
    [DataField(required: true)] public LocId Name = string.Empty;
    [DataField(required: true)] public LocId Description = string.Empty;
    [DataField] public int Tier = 1;
    [DataField] public string? Language;
    [DataField] public List<EntProtoId> Actions = new();
}
