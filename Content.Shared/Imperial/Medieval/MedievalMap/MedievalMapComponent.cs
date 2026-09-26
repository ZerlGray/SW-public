using System.Numerics;

namespace Content.Shared.Imperial.Medieval.MedievalMap;


[RegisterComponent]
public sealed partial class MedievalMapComponent : Component
{
    [DataField]
    public string MapTexturePath = "";

    /// <summary>Player-drawn regional maps are separate from the existing illustrated maps.</summary>
    [DataField]
    public bool IsSurveyMap;

    [DataField]
    public EntityUid? SurveyedMap;

    [DataField]
    public Vector2 SurveyCenter;

    [DataField]
    public float SurveyRange = 256f;

    [DataField]
    public LocId OpenMapText = "medieval-open-map";

    [DataField]
    public Vector2 Size = new Vector2(790, 790);

    /// <summary>Field notes belong to this physical map and travel with it.</summary>
    [DataField]
    public List<MedievalMapAnnotation> Annotations = new();
}
