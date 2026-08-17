using System.Text.Json;
using Cdm.GeoEngine.Core.Models;
using Cdm.GeoEngine.Core.Primitives;

namespace Cdm.GeoEngine.Core.IO;

/// <summary>Load sparse geo region seeds from JSON (Blender picker export).</summary>
public static class GeoRegionSeedLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public static IReadOnlyList<GeoRegionSeed> LoadFromFile(string jsonPath)
    {
        var text = File.ReadAllText(jsonPath);
        using var doc = JsonDocument.Parse(text);
        return FromJson(doc.RootElement);
    }

    public static IReadOnlyList<GeoRegionSeed> FromMeshJson(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0)
            root = root[0];

        if (root.TryGetProperty("geoRegionSeeds", out var seedsNode)
            || root.TryGetProperty("geo_region_seeds", out seedsNode))
            return FromJson(seedsNode);

        if (root.TryGetProperty("resolutionLod", out var resNode)
            || root.TryGetProperty("resolution_lod", out resNode))
        {
            if (resNode.TryGetProperty("geoRegionSeeds", out seedsNode)
                || resNode.TryGetProperty("geo_region_seeds", out seedsNode))
                return FromJson(seedsNode);
        }

        return Array.Empty<GeoRegionSeed>();
    }

    public static IReadOnlyList<GeoRegionSeed> FromJson(JsonElement node)
    {
        if (node.ValueKind != JsonValueKind.Array)
            return Array.Empty<GeoRegionSeed>();

        var seeds = new List<GeoRegionSeed>();
        foreach (var item in node.EnumerateArray())
        {
            var kindText = item.TryGetProperty("kind", out var kindNode)
                ? kindNode.GetString() ?? ""
                : "";
            if (!Enum.TryParse<GeoRegionKind>(kindText, true, out var kind))
                continue;

            var faceIndex = item.TryGetProperty("faceIndex", out var fiNode)
                ? fiNode.GetInt32()
                : item.TryGetProperty("face_index", out fiNode) ? fiNode.GetInt32() : -1;

            var pos = ReadVec3(item, "position") ?? new Vec3(0, 0, 0);
            var normal = ReadVec3(item, "normal") ?? new Vec3(0, 0, 1);
            seeds.Add(new GeoRegionSeed(kind, faceIndex, pos, normal));
        }

        return seeds;
    }

    private static Vec3? ReadVec3(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var node) || node.ValueKind != JsonValueKind.Array)
            return null;
        var arr = node.EnumerateArray().Select(x => x.GetDouble()).ToArray();
        if (arr.Length < 3)
            return null;
        return new Vec3(arr[0], arr[1], arr[2]);
    }
}
