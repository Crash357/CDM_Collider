using System.Text.Json;
using System.Text.Json.Serialization;
using Cdm.GeoEngine.Core.Primitives;
using Cdm.GeoEngine.Core.Models;

namespace Cdm.GeoEngine.Core.IO;

public static class DatasetCollector
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// Scan a folder for CDM compare dumps (*_00_gebaeude.txt + *_03_geometry_lod.txt).
    /// </summary>
    public static IReadOnlyList<BuildingDataset> CollectFromDirectory(string directory)
    {
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException(directory);

        var results = new List<BuildingDataset>();
        var buildingFiles = Directory.GetFiles(directory, "*_00_gebaeude.txt", SearchOption.AllDirectories);

        foreach (var buildingPath in buildingFiles.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var prefix = buildingPath[..^"_00_gebaeude.txt".Length];
            var geoPath = prefix + "_03_geometry_lod.txt";
            results.Add(CdmDumpParser.ParseBuildingPair(buildingPath, File.Exists(geoPath) ? geoPath : null));
        }

        // Standalone geometry dumps (no building pair)
        foreach (var geoOnly in Directory.GetFiles(directory, "*geometry_lod.txt", SearchOption.AllDirectories))
        {
            if (geoOnly.Contains("_03_geometry_lod", StringComparison.OrdinalIgnoreCase))
                continue;
            if (results.Any(r => string.Equals(r.GeometryLod?.Name, Path.GetFileNameWithoutExtension(geoOnly), StringComparison.OrdinalIgnoreCase)))
                continue;

            var (mesh, components) = CdmDumpParser.ParseGeometryLodDump(File.ReadAllText(geoOnly));
            results.Add(new BuildingDataset
            {
                ModelName = mesh.Name,
                SourcePath = geoOnly,
                GeometryLod = mesh,
                ReferenceComponents = components,
            });
        }

        return results;
    }

    public static void ExportDatasetJson(IEnumerable<BuildingDataset> datasets, string outputPath)
    {
        var dto = datasets.Select(ToDto).ToList();
        var json = JsonSerializer.Serialize(dto, JsonOptions);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath)) ?? ".");
        File.WriteAllText(outputPath, json);
    }

    public static BuildingDatasetDto ToDto(BuildingDataset ds) => new()
    {
        ModelName = ds.ModelName,
        SourcePath = ds.SourcePath,
        CollectedAtUtc = ds.CollectedAtUtc,
        ResolutionLod = ds.ResolutionLod == null ? null : MeshToDto(ds.ResolutionLod),
        GeometryLod = ds.GeometryLod == null ? null : MeshToDto(ds.GeometryLod),
        ReferenceComponents = ds.ReferenceComponents.Select(c => new ComponentDto
        {
            Name = c.Name,
            Mesh = MeshToDto(c.Mesh),
        }).ToList(),
        Doors = ds.Doors.Select(d => new DoorDto
        {
            Index = d.Index,
            SelectionName = d.SelectionName,
            Vertices = d.Vertices.Select(v => new[] { v.X, v.Y, v.Z }).ToList(),
            HasVertices = d.HasVertices,
        }).ToList(),
        Stats = ds.Stats == null ? null : new StatsDto
        {
            ClosedIslands = ds.Stats.ClosedIslands,
            OpenIslands = ds.Stats.OpenIslands,
            ComponentCount = ds.Stats.ComponentCount,
            PatchCount = ds.Stats.PatchCount,
        },
    };

    private static MeshDto MeshToDto(MeshData mesh) => new()
    {
        Name = mesh.Name,
        Vertices = mesh.Vertices.Select(v => new[] { v.X, v.Y, v.Z }).ToList(),
        Faces = mesh.Faces.Select(f => f.ToArray()).ToList(),
        VertexGroups = mesh.VertexGroups.ToDictionary(
            kv => kv.Key,
            kv => kv.Value.ToArray(),
            StringComparer.OrdinalIgnoreCase),
        Properties = mesh.Properties.ToDictionary(kv => kv.Key, kv => kv.Value),
    };
}

public sealed class BuildingDatasetDto
{
    public string ModelName { get; set; } = "";
    public string SourcePath { get; set; } = "";
    public DateTime CollectedAtUtc { get; set; }
    public MeshDto? ResolutionLod { get; set; }
    public MeshDto? GeometryLod { get; set; }
    public List<ComponentDto> ReferenceComponents { get; set; } = new();
    public List<DoorDto> Doors { get; set; } = new();
    public StatsDto? Stats { get; set; }
}

public sealed class MeshDto
{
    public string Name { get; set; } = "";
    public List<double[]> Vertices { get; set; } = new();
    public List<int[]> Faces { get; set; } = new();
    public Dictionary<string, int[]> VertexGroups { get; set; } = new();
    public Dictionary<string, object> Properties { get; set; } = new();
}

public sealed class ComponentDto
{
    public string Name { get; set; } = "";
    public MeshDto Mesh { get; set; } = new();
}

public sealed class DoorDto
{
    public int Index { get; set; }
    public string SelectionName { get; set; } = "";
    public List<double[]> Vertices { get; set; } = new();
    public bool HasVertices { get; set; }
}

public sealed class StatsDto
{
    public int ClosedIslands { get; set; }
    public int OpenIslands { get; set; }
    public int ComponentCount { get; set; }
    public int PatchCount { get; set; }
}
