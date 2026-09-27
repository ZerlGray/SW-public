using Content.Shared.Alert;
using Robust.Shared.GameStates;
using Robust.Shared.Prototypes;

namespace Content.Shared.Imperial.Medieval.Magic.Mana;


/// <summary>Raised after racial and profession mana modifiers have been applied.</summary>
[ByRefEvent]
public readonly record struct ManaInitializedEvent;

[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class ManaComponent : Component
{
    public const float DefaultRegenMultiplier = 10f;

    [DataField]
    public float RegenRaceModifier = 1f;

    [DataField]
    public float MaxManaRaceModifier = 1f;

    [DataField, AutoNetworkedField]
    public float Mana = 0f;

    [DataField, AutoNetworkedField]
    public float MaxMana = 100f;

    [DataField, AutoNetworkedField]
    public float Regen = 0.25f;

    [DataField, AutoNetworkedField]
    public float RegenMultiplier = DefaultRegenMultiplier;


    [DataField]
    public ProtoId<AlertPrototype> ManaAlert = "Mana";


    [DataField("reloadTime")]
    public TimeSpan ReloadTime = TimeSpan.FromSeconds(10f);


    [ViewVariables]
    public Dictionary<EntityUid, float> CastedSpells = new();

    [ViewVariables]
    public TimeSpan EndTime = TimeSpan.FromSeconds(0f);

    [ViewVariables]
    public bool ModifiersApplied = false;
}
