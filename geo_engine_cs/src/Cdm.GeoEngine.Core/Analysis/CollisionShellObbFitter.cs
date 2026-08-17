using Cdm.GeoEngine.Core.DayZ;
using Cdm.GeoEngine.Core.Models;
using Cdm.GeoEngine.Core.Primitives;

namespace Cdm.GeoEngine.Core.Analysis;

/// <summary>
/// DayZ Geometry LOD = thin collision shells, not full-resolution hulls.
/// Tight fit from patch vertices includes <see cref="GeometryLodConstants.OverhangM"/> skin;
/// depth is then clamped to wall/slab thickness while preserving the outer overhang face.
/// </summary>
public static class CollisionShellObbFitter
{
    public static OrientedBox? FitPatchMesh(
        IReadOnlyList<Vec3> samples,
        Vec3 hintNormal,
        BuildingMeshProfile profile)
    {
        var obb = ObbFitter.FitPatchTight(samples, hintNormal, profile)
            ?? ObbFitter.FitPatch(samples, hintNormal, profile);
        if (obb == null)
            return null;
        return ClampToCollisionShell(obb, profile, hintNormal);
    }

    public static MeshData? BuildPatchMesh(
        IReadOnlyList<Vec3> samples,
        Vec3 hintNormal,
        BuildingMeshProfile profile)
    {
        var obb = FitPatchMesh(samples, hintNormal, profile);
        if (obb == null)
            return null;
        var mesh = new MeshData();
        foreach (var c in obb.Corners)
            mesh.Vertices.Add(c);
        foreach (var face in DayZ.GeometryLodConstants.BoxFaces)
            mesh.Faces.Add(face.ToArray());
        return mesh;
    }

    public static OrientedBox ClampToCollisionShell(
        OrientedBox obb,
        BuildingMeshProfile profile,
        Vec3 hintNormal)
    {
        var n = BuildingMeshAnalyzer.SnapNormalToBuildingAxes(hintNormal, profile);
        var kind = ClassifyNormal(n);
        var wallHalf = profile.WallThicknessM * 0.5;
        var slabHalf = profile.HorizontalSlabM * 0.5;

        var extents = new[] { obb.ExtentN, obb.ExtentU, obb.ExtentV };
        var axes = new[] { obb.AxisN, obb.AxisU, obb.AxisV };

        var thinIdx = 0;
        for (var i = 1; i < 3; i++)
        {
            if (extents[i] < extents[thinIdx])
                thinIdx = i;
        }

        var targetHalf = kind switch
        {
            ShellKind.Horizontal => System.Math.Min(extents[thinIdx], slabHalf),
            ShellKind.Wall => wallHalf,
            _ => System.Math.Min(extents[thinIdx], wallHalf * 1.5),
        };

        if (extents[thinIdx] <= targetHalf * 1.05)
            return obb;

        var thinAxis = axes[thinIdx];
        var surfaceDot = obb.Center.Dot(thinAxis);
        var newCenter = obb.Center;

        // Keep outer face near original box surface (collision shell, not volumetric fill).
        var outer = surfaceDot + extents[thinIdx];
        newCenter = thinAxis.Scale(outer - targetHalf)
            .Add(obb.Center.Sub(thinAxis.Scale(surfaceDot)));

        var newExtents = extents.ToArray();
        newExtents[thinIdx] = targetHalf;

        return new OrientedBox
        {
            Center = newCenter,
            AxisN = obb.AxisN,
            AxisU = obb.AxisU,
            AxisV = obb.AxisV,
            ExtentN = thinIdx == 0 ? newExtents[0] : obb.ExtentN,
            ExtentU = thinIdx == 1 ? newExtents[1] : obb.ExtentU,
            ExtentV = thinIdx == 2 ? newExtents[2] : obb.ExtentV,
            Corners = RebuildCorners(obb, newCenter, thinIdx, targetHalf),
        };
    }

    private static List<Vec3> RebuildCorners(
        OrientedBox obb,
        Vec3 newCenter,
        int thinIdx,
        double thinHalf)
    {
        var eN = thinIdx == 0 ? thinHalf : obb.ExtentN;
        var eU = thinIdx == 1 ? thinHalf : obb.ExtentU;
        var eV = thinIdx == 2 ? thinHalf : obb.ExtentV;
        var specs = new (double sn, double su, double sv)[]
        {
            (-1, -1, -1), (-1, -1, 1), (-1, 1, -1), (-1, 1, 1),
            (1, -1, -1), (1, -1, 1), (1, 1, -1), (1, 1, 1),
        };
        return specs.Select(t =>
            newCenter
                .Add(obb.AxisN.Scale(t.sn * eN))
                .Add(obb.AxisU.Scale(t.su * eU))
                .Add(obb.AxisV.Scale(t.sv * eV))).ToList();
    }

    private enum ShellKind { Wall, Horizontal, Sloped }

    private static ShellKind ClassifyNormal(Vec3 n)
    {
        var nn = n.Normalized();
        var az = System.Math.Abs(nn.Z);
        if (az >= 0.85)
            return ShellKind.Horizontal;
        if (System.Math.Abs(nn.X) >= 0.65 || System.Math.Abs(nn.Y) >= 0.65)
            return ShellKind.Wall;
        return ShellKind.Sloped;
    }
}
