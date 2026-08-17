using Cdm.GeoEngine.Core.Models;
using Cdm.GeoEngine.Core.Primitives;

namespace Cdm.GeoEngine.Core.Validation;

/// <summary>Detect un-applied object transforms and inconsistent mesh state before geo generation.</summary>
public static class MeshTransformValidator
{
    public sealed class Result
    {
        public bool Ok { get; set; } = true;
        public List<string> Warnings { get; init; } = new();
        public List<string> Errors { get; init; } = new();
        public Vec3 BboxCenter { get; set; }
        public Vec3 BboxSize { get; set; }
        public bool HasVertexGroups { get; set; }
        public int VertexGroupCount { get; set; }
    }

    public static Result Validate(MeshData mesh)
    {
        var result = new Result();
        if (mesh.Vertices.Count == 0)
        {
            result.Errors.Add("Resolution-Mesh hat keine Vertices.");
            result.Ok = false;
            return result;
        }

        if (mesh.Faces.Count == 0)
            result.Warnings.Add("Resolution-Mesh hat keine Faces — nur Vertex-Gruppen werden genutzt.");

        var (center, size) = Bbox(mesh);
        result.BboxCenter = center;
        result.BboxSize = size;

        if (mesh.Properties.TryGetValue("transform_scale", out var scaleObj)
            && scaleObj is double[] scaleArr && scaleArr.Length == 3)
        {
            var maxDev = scaleArr.Max(v => System.Math.Abs(v - 1.0));
            if (maxDev > 0.02)
                result.Warnings.Add(
                    $"Objekt-Scale ({scaleArr[0]:F3}, {scaleArr[1]:F3}, {scaleArr[2]:F3}) — "
                    + "Apply Transforms empfohlen (Ctrl+A).");
        }

        if (mesh.Properties.TryGetValue("transform_applied", out var appliedObj)
            && appliedObj is bool applied && !applied)
        {
            result.Warnings.Add(
                "Transform nicht angewendet — Engine nutzt matrix_world, Apply Transforms empfohlen.");
        }

        var maxDim = System.Math.Max(size.X, System.Math.Max(size.Y, size.Z));
        if (maxDim > 500)
            result.Warnings.Add($"Sehr großes Mesh ({maxDim:F0} m) — Einheiten/Transform prüfen.");

        result.HasVertexGroups = mesh.VertexGroups.Count > 0;
        result.VertexGroupCount = mesh.VertexGroups.Count;

        if (result.VertexGroupCount > 0
            && !mesh.VertexGroups.Keys.Any(n => n.Contains("door", StringComparison.OrdinalIgnoreCase)))
        {
            result.Warnings.Add("Keine Door-Vertex-Gruppe gefunden — Türen nur über Geometrie-Heuristik.");
        }

        result.Ok = result.Errors.Count == 0;
        return result;
    }

    private static (Vec3 center, Vec3 size) Bbox(MeshData mesh)
    {
        var xs = mesh.Vertices.Select(v => v.X).ToList();
        var ys = mesh.Vertices.Select(v => v.Y).ToList();
        var zs = mesh.Vertices.Select(v => v.Z).ToList();
        var mn = new Vec3(xs.Min(), ys.Min(), zs.Min());
        var mx = new Vec3(xs.Max(), ys.Max(), zs.Max());
        return (mn.Add(mx).Scale(0.5), mx.Sub(mn));
    }
}
