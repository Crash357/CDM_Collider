namespace Cdm.GeoEngine.Core.DayZ;

/// <summary>DayZ / Arma Geometry LOD conventions.</summary>
public static class GeometryLodConstants
{
    /// <summary>Box overhang/skin beyond wrapped mesh faces (0.1 mm).</summary>
    public const double OverhangM = 0.0001;

    public const double LodResolution = 1.0e13;

    public static readonly IReadOnlyDictionary<string, object> DefaultObjectProperties =
        new Dictionary<string, object>
        {
            ["LOD"] = LodResolution,
            ["autocenter"] = 0,
            ["canbeoccluded"] = 1,
            ["canocclude"] = 0,
        };

    /// <summary>Quad face winding for OBB boxes (matches CDM BOX_FACES).</summary>
    public static readonly int[][] BoxFaces =
    {
        new[] { 0, 1, 3, 2 },
        new[] { 4, 6, 7, 5 },
        new[] { 0, 4, 5, 1 },
        new[] { 2, 3, 7, 6 },
        new[] { 0, 2, 6, 4 },
        new[] { 1, 5, 7, 3 },
    };

    /// <summary>Memory LOD door selections required per door index (DayZ building).</summary>
    public static IReadOnlyList<string> BuildingDoorMemorySelections(int doorIndex) =>
        new[]
        {
            $"door{doorIndex}",
            $"door{doorIndex}_action",
            $"door{doorIndex}_axis_1",
            $"door{doorIndex}_axis_2",
        };

    /// <summary>
    /// Resolution LOD must expose doorN with at least one vertex before Memory/Geometry export.
    /// </summary>
    public static string ResolutionDoorSelectionName(int doorIndex) => $"door{doorIndex}";
}
