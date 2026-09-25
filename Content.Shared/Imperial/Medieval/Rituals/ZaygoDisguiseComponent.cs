using Content.Shared.Humanoid;
using Content.Shared.Humanoid.Markings;
using Content.Shared.Humanoid.Prototypes;
using Content.Shared.DoAfter;
using Content.Shared.Inventory;
using Robust.Shared.Enums;
using Robust.Shared.GameStates;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;

namespace Content.Shared.Imperial.Medieval.Rituals;

/// <summary>Presentation only. Never grants a species, faction, access, or knowledge.</summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState(true)]
public sealed partial class ZaygoDisguiseComponent : Component
{
    [DataField, AutoNetworkedField] public string DisplayName = string.Empty;
    [DataField, AutoNetworkedField] public int Identifier;
    [DataField, AutoNetworkedField] public bool HideUnknown = true;
    [DataField, AutoNetworkedField] public ZaygoVisualSnapshot Visual = new();
    [DataField, AutoNetworkedField] public string? Faction;
    [DataField, AutoNetworkedField] public bool FactionLeader;
    [DataField, AutoNetworkedField] public List<string> AttackedFactions = new();
}

[Serializable, NetSerializable, DataDefinition]
public sealed partial class ZaygoVisualSnapshot
{
    [DataField] public ProtoId<SpeciesPrototype> Species = "Human";
    [DataField] public Sex Sex;
    [DataField] public Gender Gender;
    [DataField] public int Age;
    [DataField] public Color SkinColor;
    [DataField] public Color EyeColor;
    [DataField] public MarkingSet Markings = new();
    [DataField] public Dictionary<HumanoidVisualLayers, CustomBaseLayerInfo> CustomLayers = new();
    [DataField] public Dictionary<HumanoidVisualLayers, SlotFlags> HiddenLayers = new();
    [DataField] public HashSet<HumanoidVisualLayers> PermanentlyHidden = new();

    public static ZaygoVisualSnapshot Capture(HumanoidAppearanceComponent appearance) => new()
    {
        Species = appearance.Species, Sex = appearance.Sex, Gender = appearance.Gender,
        Age = appearance.Age, SkinColor = appearance.SkinColor, EyeColor = appearance.EyeColor,
        Markings = new MarkingSet(appearance.MarkingSet),
        CustomLayers = new(appearance.CustomBaseLayers),
        HiddenLayers = new(appearance.HiddenLayers), PermanentlyHidden = new(appearance.PermanentlyHidden),
    };

    public HumanoidAppearanceComponent ForRendering(HumanoidAppearanceComponent real) => new()
    {
        Species = Species, Sex = Sex, Gender = Gender, Age = Age,
        SkinColor = SkinColor, EyeColor = EyeColor,
        MarkingSet = new MarkingSet(Markings), CustomBaseLayers = new(CustomLayers),
        HiddenLayers = new(HiddenLayers), PermanentlyHidden = new(PermanentlyHidden),
        BaseLayers = new(real.BaseLayers),
    };
}

/// <summary>Explicit protection for ritual transport, including reverse displacement.</summary>
[RegisterComponent]
public sealed partial class RitualTransportProtectedComponent : Component;

/// <summary>Marker position is resolved through its current container, never stored as a location.</summary>
[RegisterComponent]
public sealed partial class ZaygoTheftMarkerComponent : Component
{
    [DataField] public EntityUid? Owner;
}

/// <summary>Captured likeness, filled by the server using a timed interaction.</summary>
[RegisterComponent]
public sealed partial class ZaygoWaxMaskComponent : Component;

[RegisterComponent, NetworkedComponent, AutoGenerateComponentState(true)]
public sealed partial class ZaygoItemAppearanceComponent : Component
{
    [DataField, AutoNetworkedField] public EntProtoId Prototype;
    [DataField, AutoNetworkedField] public bool Invisible;
}

[Serializable, NetSerializable]
public sealed partial class ZaygoCaptureDoAfterEvent : SimpleDoAfterEvent;
