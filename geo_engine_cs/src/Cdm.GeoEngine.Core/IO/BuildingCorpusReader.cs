using System.Text.Json;
using System.Text.Json.Serialization;
using Cdm.GeoEngine.Core.Models;

namespace Cdm.GeoEngine.Core.IO;

/// <summary>Reads building_corpus_index.json from the CDM geo pipeline.</summary>
public static class BuildingCorpusReader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    public static BuildingCorpusIndex Load(string jsonPath)
    {
        var json = File.ReadAllText(jsonPath);
        var dto = JsonSerializer.Deserialize<BuildingCorpusIndexDto>(json, JsonOptions)
            ?? throw new InvalidDataException("Empty building corpus JSON.");
        return FromDto(dto, jsonPath);
    }

    public static IReadOnlyList<BuildingDataset> ToBuildingDatasets(
        BuildingCorpusIndex corpus,
        CorpusMeshStore? meshStore = null)
    {
        var root = Path.GetDirectoryName(corpus.SourcePath) ?? ".";
        var list = new List<BuildingDataset>();

        foreach (var m in corpus.Models)
        {
            MeshData? resolutionLod = null;
            MeshData? geometryLod = null;
            var sourcePath = "";

            if (meshStore != null && meshStore.TryLoadPair(m.Id, out var pair) && pair != null)
            {
                resolutionLod = pair.ResolutionLod;
                geometryLod = pair.GeometryLod;
                sourcePath = pair.Entry.ResolutionMeshPath ?? pair.Entry.GeometryMeshPath ?? "";
            }
            else
            {
                var detailPath = Path.Combine(root, m.Record.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(detailPath))
                    continue;
                sourcePath = detailPath;

                var record = JsonSerializer.Deserialize<BuildingRecordDto>(
                    File.ReadAllText(detailPath), JsonOptions);
                if (record == null)
                    continue;

                resolutionLod = record.ResolutionLods?.Count > 0
                    ? LodSummaryToPlaceholder(m.ResolutionLod1, m.Id + " Res1")
                    : null;
                geometryLod = record.GeometryLods?.Count > 0
                    ? LodSummaryToPlaceholder(m.GeometryLod, m.Id + " Geometry")
                    : null;
            }

            list.Add(new BuildingDataset
            {
                ModelName = m.Id,
                SourcePath = sourcePath,
                ResolutionLod = resolutionLod,
                GeometryLod = geometryLod,
                Doors = (m.DoorsInResolution ?? new List<string>())
                    .Select(name => new DoorMarker
                    {
                        Index = ParseDoorIndex(name),
                        SelectionName = name,
                    })
                    .ToList(),
            });
        }

        return list;
    }

    private static MeshData LodSummaryToPlaceholder(LodSummaryDto? lod, string name)
    {
        var mesh = new MeshData { Name = name };
        if (lod == null)
            return mesh;
        mesh.Properties["vertices"] = lod.Vertices;
        mesh.Properties["faces"] = lod.Faces;
        mesh.Properties["lod_name"] = lod.LodName ?? "";
        return mesh;
    }

    private static int ParseDoorIndex(string name)
    {
        var digits = new string(name.Where(char.IsDigit).ToArray());
        return int.TryParse(digits, out var n) ? n : 0;
    }

    private static BuildingCorpusIndex FromDto(BuildingCorpusIndexDto dto, string path) =>
        new()
        {
            SourcePath = path,
            BuiltAtUtc = dto.BuiltAt,
            ModelCount = dto.ModelCount,
            WithDoors = dto.WithDoors,
            WithScenes = dto.WithScenes,
            WithSceneImport = dto.WithSceneImport,
            Models = dto.Models ?? new List<BuildingCorpusModelDto>(),
        };
}

public sealed class BuildingCorpusIndex
{
    public string SourcePath { get; init; } = "";
    public DateTime BuiltAtUtc { get; init; }
    public int ModelCount { get; init; }
    public int WithDoors { get; init; }
    public int WithScenes { get; init; }
    public int WithSceneImport { get; init; }
    public List<BuildingCorpusModelDto> Models { get; init; } = new();
}

public sealed class BuildingCorpusModelDto
{
    public string Id { get; set; } = "";
    public string? Category { get; set; }

    [JsonPropertyName("mesh_kind")]
    public string? MeshKind { get; set; }

    public List<string>? DoorsInResolution { get; set; }
    public List<string>? DoorsInGeometry { get; set; }
    public bool DoorsMatch { get; set; }

    [JsonPropertyName("resolution_lod_1")]
    public LodSummaryDto? ResolutionLod1 { get; set; }

    public LodSummaryDto? GeometryLod { get; set; }
    public string Record { get; set; } = "";
    public string? Scene { get; set; }
    public SceneImportDto? SceneImport { get; set; }
}

public sealed class LodSummaryDto
{
    public double Resolution { get; set; }
    public string? LodName { get; set; }
    public int Vertices { get; set; }
    public int Faces { get; set; }
    public int ComponentCount { get; set; }
    public List<DoorSummaryDto>? Doors { get; set; }
}

public sealed class DoorSummaryDto
{
    public string Name { get; set; } = "";
    public int Index { get; set; }
    public int VertexCount { get; set; }
    public int FaceCount { get; set; }
}

public sealed class SceneImportDto
{
    public bool Ok { get; set; }
    public int MeshCount { get; set; }
}

internal sealed class BuildingCorpusIndexDto
{
    public DateTime BuiltAt { get; set; }
    public int ModelCount { get; set; }
    public int WithDoors { get; set; }
    public int WithScenes { get; set; }
    public int WithSceneImport { get; set; }
    public List<BuildingCorpusModelDto>? Models { get; set; }
}

internal sealed class BuildingRecordDto
{
    public List<LodDetailDto>? ResolutionLods { get; set; }
    public List<LodDetailDto>? GeometryLods { get; set; }
}

internal sealed class LodDetailDto
{
    public double Resolution { get; set; }
    public string? LodName { get; set; }
    public int Vertices { get; set; }
    public int Faces { get; set; }
}
