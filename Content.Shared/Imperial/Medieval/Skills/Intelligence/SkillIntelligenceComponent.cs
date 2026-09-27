using Content.Shared.FixedPoint;
using Content.Shared.Mobs;
using Robust.Shared.GameStates;
using Robust.Shared.Serialization.TypeSerializers.Implementations.Custom;

namespace Content.Shared.Imperial.Medieval.Skills;

[RegisterComponent, NetworkedComponent]
public sealed partial class SkillIntelligenceComponent : Component
{
    [DataField(serverOnly: true)] public List<string> RandomLanguages = new();
    [DataField(serverOnly: true)] public bool LanguagesSelected;
    [DataField(serverOnly: true)] public float ManaMaximumMultiplier = 1f;
    [DataField(serverOnly: true)] public float ManaRegenerationMultiplier = 1f;
}
