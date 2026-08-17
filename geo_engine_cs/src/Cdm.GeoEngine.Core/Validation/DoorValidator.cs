using Cdm.GeoEngine.Core.DayZ;
using Cdm.GeoEngine.Core.Models;

namespace Cdm.GeoEngine.Core.Validation;

public sealed class DoorValidationResult
{
    public bool IsValid { get; init; }
    public List<string> Errors { get; init; } = new();
    public List<string> Warnings { get; init; } = new();
    public List<DoorMarker> FoundDoors { get; init; } = new();
}

/// <summary>
/// DayZ building doors must be defined as vertex selections on the Resolution LOD
/// (door1, door2, …) before Memory/Geometry export — see dayz_geometry_maker.
/// </summary>
public static class DoorValidator
{
    public static DoorValidationResult Validate(MeshData resolutionLod, int expectedDoorCount = 0)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        var found = ExtractDoors(resolutionLod);

        if (expectedDoorCount > 0 && found.Count < expectedDoorCount)
        {
            errors.Add(
                $"Expected at least {expectedDoorCount} door selection(s), found {found.Count}.");
        }

        foreach (var door in found)
        {
            if (!door.HasVertices)
            {
                errors.Add(
                    $"Selection '{door.SelectionName}' has no vertices assigned — " +
                    "DayZ requires door markers as vertices on Resolution LOD.");
            }
            else if (door.Vertices.Count < 3)
            {
                warnings.Add(
                    $"Selection '{door.SelectionName}' has only {door.Vertices.Count} vertex(es) — " +
                    "use at least 3 vertices to define the door opening footprint.");
            }
        }

        if (found.Count == 0)
        {
            warnings.Add(
                "No doorN vertex groups found on Resolution LOD. " +
                "Add door1, door2, … with assigned vertices for building animations.");
        }

        for (var i = 1; i <= System.Math.Max(expectedDoorCount, found.Count); i++)
        {
            var name = GeometryLodConstants.ResolutionDoorSelectionName(i);
            var door = found.FirstOrDefault(d =>
                string.Equals(d.SelectionName, name, StringComparison.OrdinalIgnoreCase));
            if (door == null && expectedDoorCount > 0)
                errors.Add($"Missing required selection '{name}' with vertices.");
            else if (door != null && !door.HasVertices)
                errors.Add($"'{name}' exists but has no vertices.");
        }

        return new DoorValidationResult
        {
            IsValid = errors.Count == 0,
            Errors = errors,
            Warnings = warnings,
            FoundDoors = found,
        };
    }

    public static List<DoorMarker> ExtractDoors(MeshData mesh)
    {
        var doors = new List<DoorMarker>();

        foreach (var (name, indices) in mesh.VertexGroups)
        {
            if (!IsDoorSelection(name))
                continue;

            doors.Add(new DoorMarker
            {
                Index = ParseDoorIndex(name),
                SelectionName = name,
                Vertices = indices.Select(i => mesh.Vertices[i]).ToList(),
            });
        }

        return doors.OrderBy(d => d.Index).ToList();
    }

    private static bool IsDoorSelection(string name)
    {
        if (!name.StartsWith("door", StringComparison.OrdinalIgnoreCase))
            return false;
        var suffix = name[4..];
        return suffix.Length == 0 || int.TryParse(suffix, out _);
    }

    private static int ParseDoorIndex(string name)
    {
        var digits = new string(name.Where(char.IsDigit).ToArray());
        return int.TryParse(digits, out var n) ? n : 0;
    }
}
