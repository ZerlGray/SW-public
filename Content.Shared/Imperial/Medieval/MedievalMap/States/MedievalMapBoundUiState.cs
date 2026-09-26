using System.Numerics;
using Robust.Shared.Serialization;

namespace Content.Shared.Imperial.Medieval.MedievalMap;


[Serializable, NetSerializable]
public sealed class MedievalMapBoundUiState : BoundUserInterfaceState
{
    public Vector2 Size = new Vector2(790, 790);

    public string MapTexturePath = "";

    public List<MedievalMapAnnotation> Annotations = new();
    public NetEntity? Surveyor;
    public string PendingDescription = "";
}

/// <summary>The drawing is schematic: its ink position is chosen by the cartographer,
/// while the observed world coordinates are always supplied by the server.</summary>
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
public sealed class MedievalMapAnnotateMessage(Vector2 position, string title) : BoundUserInterfaceMessage
{
    public Vector2 Position = position;
    public string Title = title;
}
