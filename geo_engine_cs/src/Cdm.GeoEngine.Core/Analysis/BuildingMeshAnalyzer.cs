using Cdm.GeoEngine.Core.Models;
using Cdm.GeoEngine.Core.Primitives;
using Cdm.GeoEngine.Core.Topology;

namespace Cdm.GeoEngine.Core.Analysis;

/// <summary>Phase 0 — read building dimensions and axes from Resolution mesh.</summary>
public static class BuildingMeshAnalyzer
{
    public static BuildingMeshProfile Analyze(MeshData mesh)
    {
        if (mesh.Vertices.Count == 0)
        {
            return new BuildingMeshProfile
            {
                Centroid = Vec3.From(System.Numerics.Vector3.Zero),
                BoundsMin = Vec3.From(System.Numerics.Vector3.Zero),
                BoundsMax = Vec3.From(System.Numerics.Vector3.Zero),
                SizeM = Vec3.From(System.Numerics.Vector3.Zero),
                AxisX = new Vec3(1, 0, 0),
                AxisY = new Vec3(0, 1, 0),
            };
        }

        var min = mesh.Vertices[0];
        var max = mesh.Vertices[0];
        var sum = new Vec3(0, 0, 0);
        foreach (var v in mesh.Vertices)
        {
            min = Min(min, v);
            max = Max(max, v);
            sum = sum.Add(v);
        }

        var centroid = sum.Scale(1.0 / mesh.Vertices.Count);
        var size = max.Sub(min);
        var axisX = PrimaryHorizontalAxis(mesh.Vertices, centroid);
        var axisZ = new Vec3(0, 0, 1);
        var axisY = axisZ.Cross(axisX).Normalized();

        var wallThickness = EstimateWallThickness(mesh);
        var horizSlab = EstimateHorizontalSlab(mesh);
        var footprint = System.Math.Max(0.01, size.X * size.Y);

        return new BuildingMeshProfile
        {
            Centroid = centroid,
            BoundsMin = min,
            BoundsMax = max,
            SizeM = size,
            FootprintAreaM2 = footprint,
            HeightM = size.Z,
            AxisX = axisX,
            AxisY = axisY,
            AxisZ = axisZ,
            VertexCount = mesh.VertexCount,
            FaceCount = mesh.FaceCount,
            WallThicknessM = wallThickness,
            HorizontalSlabM = horizSlab,
        };
    }

    private static double EstimateHorizontalSlab(MeshData mesh)
    {
        if (mesh.Faces.Count == 0)
            return 0.12;

        var samples = new List<double>();
        foreach (var face in mesh.Faces)
        {
            var n = MeshTopology.FaceNormal(mesh, face).Normalized();
            if (System.Math.Abs(n.Z) < 0.85)
                continue;

            var verts = face.Select(vi => mesh.Vertices[vi]).ToList();
            var ext = ObbFitter.ProjectExtents(verts, n, Vec3.Centroid(verts));
            var depth = ext.nHi - ext.nLo;
            if (depth > 0.04 && depth < 0.45)
                samples.Add(depth);
        }

        if (samples.Count == 0)
            return 0.12;

        samples.Sort();
        return samples[samples.Count / 2];
    }

    private static double EstimateWallThickness(MeshData mesh)
    {
        if (mesh.Faces.Count == 0)
            return 0.15;

        var fromPairs = EstimateWallThicknessFromPairs(mesh);
        if (fromPairs > 0)
            return fromPairs;

        var samples = new List<double>();
        foreach (var face in mesh.Faces)
        {
            var n = MeshTopology.FaceNormal(mesh, face).Normalized();
            if (System.Math.Abs(n.Z) > 0.85)
                continue;

            var verts = face.Select(vi => mesh.Vertices[vi]).ToList();
            var axis = SnapNormalToBuildingAxes(n, null);
            var ext = ObbFitter.ProjectExtents(verts, axis, Vec3.Centroid(verts));
            var depth = ext.nHi - ext.nLo;
            if (depth > 0.02 && depth < 0.6)
                samples.Add(depth);
        }

        if (samples.Count == 0)
            return 0.15;

        samples.Sort();
        return samples[samples.Count / 2];
    }

    /// <summary>Measure cavity width between antiparallel wall faces (blind extent calibration).</summary>
    internal static double EstimateWallThicknessFromPairs(MeshData mesh)
    {
        var normals = new List<Vec3>();
        var centroids = new List<Vec3>();
        var spans = new List<double>();

        foreach (var face in mesh.Faces)
        {
            if (face.Length < 3)
                continue;

            var n = MeshTopology.FaceNormal(mesh, face).Normalized();
            if (System.Math.Abs(n.Z) > 0.85)
                continue;

            var verts = face.Select(vi => mesh.Vertices[vi]).ToList();
            var c = Vec3.Centroid(verts);
            var span = verts.Max(v => Vec3.Distance(v, c));
            normals.Add(SnapNormalToBuildingAxes(n, null));
            centroids.Add(c);
            spans.Add(span);
        }

        if (normals.Count < 2)
            return 0;

        var samples = new List<double>();
        for (var i = 0; i < normals.Count; i++)
        {
            for (var j = i + 1; j < normals.Count; j++)
            {
                if (normals[i].Dot(normals[j]) > -0.85)
                    continue;

                var axis = normals[i].Normalized();
                var u = System.Math.Abs(axis.Dot(new Vec3(0, 0, 1))) > 0.99
                    ? new Vec3(1, 0, 0)
                    : new Vec3(0, 0, 1);
                u = u.Sub(axis.Scale(u.Dot(axis))).Normalized();
                var v = axis.Cross(u).Normalized();

                var ci = centroids[i];
                var cj = centroids[j];
                var du = System.Math.Abs(ci.Dot(u) - cj.Dot(u));
                var dv = System.Math.Abs(ci.Dot(v) - cj.Dot(v));
                var overlap = System.Math.Max(spans[i], spans[j]) + 0.25;
                if (du > overlap || dv > overlap)
                    continue;

                var depth = System.Math.Abs(ci.Dot(axis) - cj.Dot(axis));
                if (depth >= 0.08 && depth <= 0.45)
                    samples.Add(depth);
            }
        }

        if (samples.Count == 0)
            return 0;

        samples.Sort();
        return samples[samples.Count / 2];
    }

    private static Vec3 PrimaryHorizontalAxis(IReadOnlyList<Vec3> verts, Vec3 centroid)
    {
        double sxx = 0, syy = 0, sxy = 0;
        foreach (var p in verts)
        {
            var dx = p.X - centroid.X;
            var dy = p.Y - centroid.Y;
            sxx += dx * dx;
            syy += dy * dy;
            sxy += dx * dy;
        }

        var theta = 0.5 * System.Math.Atan2(2 * sxy, sxx - syy);
        return new Vec3(System.Math.Cos(theta), System.Math.Sin(theta), 0).Normalized();
    }

    internal static Vec3 SnapNormalToBuildingAxes(Vec3 normal, BuildingMeshProfile? profile)
    {
        var n = normal.Normalized();
        if (profile == null)
            return n;

        var candidates = new[]
        {
            profile.AxisX, profile.AxisX.Scale(-1),
            profile.AxisY, profile.AxisY.Scale(-1),
            profile.AxisZ, profile.AxisZ.Scale(-1),
        };

        Vec3 best = n;
        var bestDot = -2.0;
        foreach (var c in candidates)
        {
            var d = System.Math.Abs(n.Dot(c));
            if (d > bestDot)
            {
                bestDot = d;
                best = n.Dot(c) >= 0 ? c.Normalized() : c.Scale(-1).Normalized();
            }
        }

        return bestDot >= 0.65 ? best : n;
    }

    private static Vec3 Min(Vec3 a, Vec3 b) =>
        new(System.Math.Min(a.X, b.X), System.Math.Min(a.Y, b.Y), System.Math.Min(a.Z, b.Z));

    private static Vec3 Max(Vec3 a, Vec3 b) =>
        new(System.Math.Max(a.X, b.X), System.Math.Max(a.Y, b.Y), System.Math.Max(a.Z, b.Z));
}
