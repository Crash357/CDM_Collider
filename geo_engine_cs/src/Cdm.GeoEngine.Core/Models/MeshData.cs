using Cdm.GeoEngine.Core.Primitives;

namespace Cdm.GeoEngine.Core.Models;

public sealed class MeshData
{
    public string Name { get; set; } = "Mesh";
    public List<Vec3> Vertices { get; init; } = new();
    public List<int[]> Faces { get; init; } = new();
    public Dictionary<string, List<int>> VertexGroups { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, object> Properties { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public int VertexCount => Vertices.Count;
    public int FaceCount => Faces.Count;

    public MeshData Clone() => new()
    {
        Name = Name,
        Vertices = Vertices.Select(v => v).ToList(),
        Faces = Faces.Select(f => f.ToArray()).ToList(),
        VertexGroups = VertexGroups.ToDictionary(
            kv => kv.Key,
            kv => kv.Value.ToList(),
            StringComparer.OrdinalIgnoreCase),
        Properties = new Dictionary<string, object>(Properties, StringComparer.OrdinalIgnoreCase),
    };
}

public sealed class MeshComponent
{
    public string Name { get; init; } = "Component01";
    public MeshData Mesh { get; init; } = new();
}

public sealed record BuildingDataset
{
    public string ModelName { get; init; } = "";
    public string SourcePath { get; init; } = "";
    public DateTime CollectedAtUtc { get; init; } = DateTime.UtcNow;

    /// <summary>Resolution LOD 1 — high-detail visual mesh.</summary>
    public MeshData? ResolutionLod { get; init; }

    /// <summary>Reference Geometry LOD (ground truth from CDM dump or P3D).</summary>
    public MeshData? GeometryLod { get; init; }

    public List<MeshComponent> ReferenceComponents { get; init; } = new();
    public List<DoorMarker> Doors { get; init; } = new();
    public BuildingPipelineStats? Stats { get; init; }
}

public sealed record BuildingPipelineStats
{
    public int ClosedIslands { get; init; }
    public int OpenIslands { get; init; }
    public int ComponentCount { get; init; }
    public int PatchCount { get; init; }
}

public sealed class DoorMarker
{
    public int Index { get; init; }
    public string SelectionName { get; init; } = "";
    public List<Vec3> Vertices { get; init; } = new();
    public bool HasVertices => Vertices.Count > 0;
}
