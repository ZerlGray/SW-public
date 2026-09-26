using System.Numerics;
using Content.Shared.Imperial.Medieval.CartographerTable;
using Robust.Shared.Serialization;

namespace Content.Shared.Imperial.Medieval.MedievalMap;


[Serializable, NetSerializable]
public sealed class MedievalMapBoundUiState : BoundUserInterfaceState
{
    public Vector2 Size = new Vector2(790, 790);

    public string MapTexturePath = "";
    public bool IsSurveyMap;
    public MedievalCartographerBoundUserInterfaceState? Geography;
    public int? DisplayedWorldMap;

    public List<MedievalMapAnnotation> Annotations = new();
    public NetEntity? Surveyor;
    public int PendingAnnotationIndex = -1;
    public string PendingDescription = "";
}

/// <summary>A surveyed location displayed at its actual coordinates on the world map.</summary>
[Serializable, NetSerializable, DataDefinition]
public sealed partial class MedievalMapAnnotation
{
    [DataField]
    public Vector2 Position;

    [DataField]
    public Vector2 WorldPosition;

    [DataField]
    public int WorldMap;

    [DataField]
    public string Title = "";

    [DataField]
    public string Description = "";
}

[Serializable, NetSerializable]
public sealed class MedievalMapAnnotateMessage(string title) : BoundUserInterfaceMessage
{
    public string Title = title;
}
