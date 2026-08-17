using System.Globalization;
using System.Text.RegularExpressions;
using Cdm.GeoEngine.Core.Primitives;
using Cdm.GeoEngine.Core.Models;

namespace Cdm.GeoEngine.Core.IO;

/// <summary>Reads CDM Collider text dumps (geo_dump/*.txt).</summary>
public static class CdmDumpParser
{
    private static readonly Regex VertexLine = new(
        @"^\s*(\d+)\s+(-?\d+(?:\.\d+)?(?:[eE][+-]?\d+)?)\s+(-?\d+(?:\.\d+)?(?:[eE][+-]?\d+)?)\s+(-?\d+(?:\.\d+)?(?:[eE][+-]?\d+)?)\s*$",
        RegexOptions.Compiled);

    private static readonly Regex FaceLine = new(
        @"^\s*(\d+)\s+\[([^\]]+)\]",
        RegexOptions.Compiled);

    private static readonly Regex ComponentHeader = new(
        @"^VERTEX GROUP / COMPONENT\s+(Component\d+)\s+\(#\d+\)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex MetaInt = new(
        @"^(Vertices|Edges|Faces|Vertex Groups|Component-Groups|Geschlossene Islands|Offene Islands):\s*(\d+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ObjectLine = new(
        @"^Objekt:\s*(.+)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static MeshData ParseMeshDump(string text, string defaultName = "Mesh")
    {
        var mesh = new MeshData { Name = defaultName };
        var lines = text.Replace("\r\n", "\n").Split('\n');
        ParseObjectName(lines, mesh);
        ParseGlobalMesh(lines, mesh, stopAtComponent: true);
        return mesh;
    }

    public static (MeshData Mesh, List<MeshComponent> Components) ParseGeometryLodDump(string text)
    {
        var mesh = new MeshData { Name = "Geometry" };
        var components = new List<MeshComponent>();
        var lines = text.Replace("\r\n", "\n").Split('\n');

        ParseObjectName(lines, mesh);
        ParseDayZProperties(lines, mesh);

        // Full merged Geometry LOD lives in GESAMT-MESH block.
        var gesamtIdx = Array.FindIndex(lines, l =>
            l.Equals("GESAMT-MESH", StringComparison.OrdinalIgnoreCase));
        if (gesamtIdx >= 0)
            ParseGlobalMesh(lines, mesh, stopAtComponent: true, startAt: gesamtIdx);

        var i = 0;
        while (i < lines.Length)
        {
            var compMatch = ComponentHeader.Match(lines[i]);
            if (!compMatch.Success)
            {
                i++;
                continue;
            }

            var compName = compMatch.Groups[1].Value;
            var vertIndices = ParseComponentVertexIndices(lines, i + 1);
            if (vertIndices.Count > 0)
            {
                mesh.VertexGroups[compName] = vertIndices;
                components.Add(new MeshComponent
                {
                    Name = compName,
                    Mesh = ExtractSubmesh(mesh, compName, vertIndices),
                });
            }

            i++;
        }

        if (mesh.Vertices.Count == 0 && components.Count > 0)
            MergeComponentsIntoGeometry(mesh, components);

        return (mesh, components);
    }

    private static List<int> ParseComponentVertexIndices(string[] lines, int start)
    {
        var indices = new List<int>();
        var i = start;
        for (; i < lines.Length; i++)
        {
            if (ComponentHeader.IsMatch(lines[i]))
                break;
            if (lines[i].StartsWith("FACES in Component", StringComparison.OrdinalIgnoreCase))
                break;
            var m = VertexLine.Match(lines[i]);
            if (m.Success)
                indices.Add(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture));
        }
        return indices;
    }

    private static MeshData ExtractSubmesh(MeshData source, string name, IReadOnlyList<int> vertIndices)
    {
        var sub = new MeshData { Name = name };
        var map = new Dictionary<int, int>();
        foreach (var gi in vertIndices.OrderBy(x => x))
        {
            if (gi < 0 || gi >= source.Vertices.Count)
                continue;
            map[gi] = sub.Vertices.Count;
            sub.Vertices.Add(source.Vertices[gi]);
        }

        var vertSet = vertIndices.ToHashSet();
        foreach (var face in source.Faces)
        {
            if (face.All(vi => vertSet.Contains(vi)))
                sub.Faces.Add(face.Select(vi => map[vi]).ToArray());
        }

        return sub;
    }

    public static BuildingDataset ParseBuildingPair(string buildingDumpPath, string? geometryDumpPath = null)
    {
        var buildingText = File.ReadAllText(buildingDumpPath);
        var building = ParseMeshDump(buildingText, Path.GetFileNameWithoutExtension(buildingDumpPath));

        var dataset = new BuildingDataset
        {
            ModelName = building.Name,
            SourcePath = buildingDumpPath,
            ResolutionLod = building,
        };

        dataset = ApplyBuildingStats(buildingText, dataset);

        if (geometryDumpPath != null && File.Exists(geometryDumpPath))
        {
            var geoText = File.ReadAllText(geometryDumpPath);
            var (geoMesh, components) = ParseGeometryLodDump(geoText);
            dataset = dataset with
            {
                GeometryLod = geoMesh,
                ReferenceComponents = components,
            };
        }

        dataset = dataset with { Doors = ExtractDoorMarkers(building) };
        return dataset;
    }

    private static void ParseObjectName(string[] lines, MeshData mesh)
    {
        foreach (var line in lines)
        {
            var m = ObjectLine.Match(line);
            if (m.Success)
                mesh.Name = m.Groups[1].Value.Trim();
        }
    }

    private static void ParseDayZProperties(string[] lines, MeshData mesh)
    {
        foreach (var line in lines)
        {
            if (line.StartsWith("  LOD:", StringComparison.Ordinal))
                mesh.Properties["LOD"] = double.Parse(line.Split(':')[1].Trim(), CultureInfo.InvariantCulture);
            else if (line.StartsWith("  autocenter:", StringComparison.Ordinal))
                mesh.Properties["autocenter"] = int.Parse(line.Split(':')[1].Trim(), CultureInfo.InvariantCulture);
            else if (line.StartsWith("  canbeoccluded:", StringComparison.Ordinal))
                mesh.Properties["canbeoccluded"] = int.Parse(line.Split(':')[1].Trim(), CultureInfo.InvariantCulture);
            else if (line.StartsWith("  canocclude:", StringComparison.Ordinal))
                mesh.Properties["canocclude"] = int.Parse(line.Split(':')[1].Trim(), CultureInfo.InvariantCulture);
        }
    }

    private static BuildingDataset ApplyBuildingStats(string text, BuildingDataset dataset)
    {
        int closed = 0, open = 0;
        foreach (var line in text.Split('\n'))
        {
            var m = MetaInt.Match(line);
            if (!m.Success)
                continue;
            if (m.Groups[1].Value.Contains("Geschlossene", StringComparison.OrdinalIgnoreCase))
                closed = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            if (m.Groups[1].Value.Contains("Offene", StringComparison.OrdinalIgnoreCase))
                open = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        }

        if (closed == 0 && open == 0)
            return dataset;

        return dataset with
        {
            Stats = new BuildingPipelineStats { ClosedIslands = closed, OpenIslands = open },
        };
    }

    private static List<DoorMarker> ExtractDoorMarkers(MeshData mesh)
    {
        var doors = new List<DoorMarker>();
        foreach (var (name, indices) in mesh.VertexGroups)
        {
            if (!name.StartsWith("door", StringComparison.OrdinalIgnoreCase))
                continue;
            var idx = ParseDoorIndex(name);
            doors.Add(new DoorMarker
            {
                Index = idx,
                SelectionName = name,
                Vertices = indices.Select(i => mesh.Vertices[i]).ToList(),
            });
        }
        return doors.OrderBy(d => d.Index).ToList();
    }

    private static int ParseDoorIndex(string name)
    {
        var digits = new string(name.Where(char.IsDigit).ToArray());
        return int.TryParse(digits, out var n) ? n : 0;
    }

    private static void ParseGlobalMesh(string[] lines, MeshData mesh, bool stopAtComponent, int startAt = 0)
    {
        var i = startAt;
        while (i < lines.Length)
        {
            if (stopAtComponent && ComponentHeader.IsMatch(lines[i]))
                break;
            if (lines[i].StartsWith("VERTICES (Welt", StringComparison.OrdinalIgnoreCase))
            {
                i = ParseVertices(lines, i + 2, mesh);
                continue;
            }
            if (lines[i].StartsWith("FACES", StringComparison.OrdinalIgnoreCase) &&
                !lines[i].Contains("Offene", StringComparison.OrdinalIgnoreCase) &&
                !lines[i].Contains("in Component", StringComparison.OrdinalIgnoreCase))
            {
                i = ParseFaces(lines, i + 2, mesh);
                continue;
            }
            i++;
        }
    }

    private static int ParseMeshBlock(string[] lines, int start, MeshData mesh)
    {
        var i = start;
        while (i < lines.Length)
        {
            if (ComponentHeader.IsMatch(lines[i]))
                break;
            if (lines[i].StartsWith("---", StringComparison.Ordinal))
                break;
            if (lines[i].StartsWith("VERTICES (Welt", StringComparison.OrdinalIgnoreCase))
            {
                i = ParseVertices(lines, i + 2, mesh);
                continue;
            }
            if (lines[i].StartsWith("FACES", StringComparison.OrdinalIgnoreCase))
            {
                i = ParseFaces(lines, i + 2, mesh);
                continue;
            }
            i++;
        }
        return i;
    }

    private static int ParseVertices(string[] lines, int start, MeshData mesh)
    {
        mesh.Vertices.Clear();
        var i = start;
        for (; i < lines.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i]))
                break;
            if (lines[i].StartsWith("EDGES", StringComparison.OrdinalIgnoreCase))
                break;
            var m = VertexLine.Match(lines[i]);
            if (!m.Success)
                continue;
            mesh.Vertices.Add(new Vec3(
                double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture),
                double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture),
                double.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture)));
        }
        return i;
    }

    private static int ParseFaces(string[] lines, int start, MeshData mesh)
    {
        mesh.Faces.Clear();
        var i = start;
        for (; i < lines.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i]))
                break;
            if (lines[i].StartsWith("AABB", StringComparison.OrdinalIgnoreCase))
                break;
            if (lines[i].StartsWith("VERTICES", StringComparison.OrdinalIgnoreCase))
                break;
            var m = FaceLine.Match(lines[i]);
            if (!m.Success)
                continue;
            var verts = m.Groups[2].Value
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(v => int.Parse(v, CultureInfo.InvariantCulture))
                .ToArray();
            mesh.Faces.Add(verts);
        }
        return i;
    }

    private static void MergeComponentsIntoGeometry(MeshData target, List<MeshComponent> components)
    {
        target.Vertices.Clear();
        target.Faces.Clear();
        target.VertexGroups.Clear();

        var offset = 0;
        foreach (var comp in components)
        {
            var localToGlobal = new int[comp.Mesh.Vertices.Count];
            for (var vi = 0; vi < comp.Mesh.Vertices.Count; vi++)
            {
                localToGlobal[vi] = target.Vertices.Count;
                target.Vertices.Add(comp.Mesh.Vertices[vi]);
            }

            foreach (var face in comp.Mesh.Faces)
                target.Faces.Add(face.Select(vi => localToGlobal[vi]).ToArray());

            target.VertexGroups[comp.Name] = localToGlobal.ToList();
            offset += comp.Mesh.Vertices.Count;
        }
    }
}
