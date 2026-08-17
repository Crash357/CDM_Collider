using System.Text.Json;
using Cdm.GeoEngine.Core.Models;
using Cdm.GeoEngine.Core.Primitives;

namespace Cdm.GeoEngine.Core.IO;

/// <summary>Loads mesh JSON from Blender export or corpus mesh bake.</summary>
public static class JsonMeshLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public static MeshData LoadFromFile(string jsonPath)
    {
        var text = File.ReadAllText(jsonPath);
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0)
            root = root[0];

        if (root.TryGetProperty("resolutionLod", out var resNode)
            || root.TryGetProperty("resolution_lod", out resNode))
            return FromDto(resNode);

        if (root.TryGetProperty("geometryLod", out var geoNode)
            || root.TryGetProperty("geometry_lod", out geoNode))
            return FromDto(geoNode);

        throw new InvalidDataException($"JSON has no resolutionLod/geometryLod block: {jsonPath}");
    }

    public static MeshData LoadResolutionFromFile(string jsonPath)
    {
        var text = File.ReadAllText(jsonPath);
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0)
            root = root[0];
        if (!root.TryGetProperty("resolutionLod", out var resNode)
            && !root.TryGetProperty("resolution_lod", out resNode))
            throw new InvalidDataException($"JSON has no resolutionLod block: {jsonPath}");
        return FromDto(resNode);
    }

    public static MeshData LoadGeometryFromFile(string jsonPath)
    {
        var text = File.ReadAllText(jsonPath);
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        if (!root.TryGetProperty("geometryLod", out var geoNode)
            && !root.TryGetProperty("geometry_lod", out geoNode))
            throw new InvalidDataException($"JSON has no geometryLod block: {jsonPath}");
        return FromDto(geoNode);
    }

    public static MeshData FromDto(JsonElement node)
    {
        var mesh = new MeshData
        {
            Name = node.TryGetProperty("name", out var nameNode)
                ? nameNode.GetString() ?? "Mesh"
                : "Mesh",
        };

        foreach (var v in node.GetProperty("vertices").EnumerateArray())
            mesh.Vertices.Add(new Vec3(v[0].GetDouble(), v[1].GetDouble(), v[2].GetDouble()));

        foreach (var f in node.GetProperty("faces").EnumerateArray())
            mesh.Faces.Add(f.EnumerateArray().Select(x => x.GetInt32()).ToArray());

        if (node.TryGetProperty("vertexGroups", out var groups))
        {
            foreach (var prop in groups.EnumerateObject())
                mesh.VertexGroups[prop.Name] = prop.Value.EnumerateArray().Select(x => x.GetInt32()).ToList();
        }

        if (node.TryGetProperty("geoRegionSeeds", out var seedsNode)
            || node.TryGetProperty("geo_region_seeds", out seedsNode))
        {
            mesh.Properties["geo_region_seeds"] = GeoRegionSeedLoader.FromJson(seedsNode);
        }

        if (node.TryGetProperty("transform", out var transformNode))
        {
            if (transformNode.TryGetProperty("scale", out var scaleNode)
                && scaleNode.ValueKind == JsonValueKind.Array)
            {
                mesh.Properties["transform_scale"] = scaleNode.EnumerateArray()
                    .Select(x => x.GetDouble()).ToArray();
            }
            if (transformNode.TryGetProperty("applied", out var appliedNode))
                mesh.Properties["transform_applied"] = appliedNode.GetBoolean();
        }

        return mesh;
    }
}
