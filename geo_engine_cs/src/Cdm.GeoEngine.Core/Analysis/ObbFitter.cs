using Cdm.GeoEngine.Core.DayZ;
using Cdm.GeoEngine.Core.Models;
using Cdm.GeoEngine.Core.Primitives;

namespace Cdm.GeoEngine.Core.Analysis;

/// <summary>Oriented box fit: in-plane PCA for rotation + building-aware normal snap.</summary>
public static class ObbFitter
{
    /// <summary>Per-axis skin applied when projecting patch vertices onto OBB axes.</summary>
    private static double SkinM => GeometryLodConstants.OverhangM;

    public static OrientedBox? FitPatch(
        IReadOnlyList<Vec3> worldVerts,
        Vec3 hintNormal,
        BuildingMeshProfile? profile = null)
    {
        return FitPatchInternal(worldVerts, hintNormal, profile, tightWrap: false);
    }

    /// <summary>Tight AABB along building axes — for resolution-guided blind collision boxes.</summary>
    public static OrientedBox? FitPatchTight(
        IReadOnlyList<Vec3> worldVerts,
        Vec3 hintNormal,
        BuildingMeshProfile? profile = null)
    {
        return FitPatchInternal(worldVerts, hintNormal, profile, tightWrap: true);
    }

    private static OrientedBox? FitPatchInternal(
        IReadOnlyList<Vec3> worldVerts,
        Vec3 hintNormal,
        BuildingMeshProfile? profile,
        bool tightWrap)
    {
        if (worldVerts.Count == 0)
            return null;

        var n = BuildingMeshAnalyzer.SnapNormalToBuildingAxes(hintNormal, profile);
        var centroid = Vec3.Centroid(worldVerts);
        var u = BuildingAlignedInPlaneAxis(worldVerts, n, centroid, profile);
        var v = n.Cross(u).Normalized();

        var kind = ClassifyPatch(n);
        var (nLo, nHi, uLo, uHi, vLo, vHi) = tightWrap
            ? ProjectExtentsTight(worldVerts, n, u, v)
            : ProjectExtents(worldVerts, n, u, v, kind, profile);
        if (!tightWrap && profile != null)
            (uLo, uHi, vLo, vHi) = ClampInPlaneExtents(
                worldVerts, n, u, v, uLo, uHi, vLo, vHi, profile, kind);

        var corners = BuildCorners(n, u, v, nLo, nHi, uLo, uHi, vLo, vHi);
        return new OrientedBox
        {
            Center = n.Scale((nLo + nHi) * 0.5)
                .Add(u.Scale((uLo + uHi) * 0.5))
                .Add(v.Scale((vLo + vHi) * 0.5)),
            AxisN = n,
            AxisU = u,
            AxisV = v,
            ExtentN = (nHi - nLo) * 0.5,
            ExtentU = (uHi - uLo) * 0.5,
            ExtentV = (vHi - vLo) * 0.5,
            Corners = corners,
        };
    }

    public static MeshData? BuildPatchMesh(
        IReadOnlyList<Vec3> worldVerts,
        Vec3 hintNormal,
        BuildingMeshProfile? profile = null)
    {
        var obb = FitPatch(worldVerts, hintNormal, profile);
        if (obb == null)
            return null;

        return BuildMeshFromObb(obb);
    }

    public static MeshData? BuildPatchMeshTight(
        IReadOnlyList<Vec3> worldVerts,
        Vec3 hintNormal,
        BuildingMeshProfile? profile = null)
    {
        var obb = FitPatchTight(worldVerts, hintNormal, profile);
        if (obb == null)
            return null;

        return BuildMeshFromObb(obb);
    }

    private static MeshData BuildMeshFromObb(OrientedBox obb)
    {
        var mesh = new MeshData();
        foreach (var c in obb.Corners)
            mesh.Vertices.Add(c);
        foreach (var face in GeometryLodConstants.BoxFaces)
            mesh.Faces.Add(face.ToArray());
        return mesh;
    }

    public static (Vec3 n, Vec3 u, Vec3 v, double nLo, double nHi, double uLo, double uHi, double vLo, double vHi)
        ProjectExtents(IReadOnlyList<Vec3> worldVerts, Vec3 n, Vec3 centroid)
    {
        var u = InPlanePrimaryAxis(worldVerts, n, centroid, null);
        var v = n.Cross(u).Normalized();
        var kind = ClassifyPatch(n);
        var ext = ProjectExtents(worldVerts, n, u, v, kind, null);
        return (n, u, v, ext.nLo, ext.nHi, ext.uLo, ext.uHi, ext.vLo, ext.vHi);
    }

    private static (double nLo, double nHi, double uLo, double uHi, double vLo, double vHi) ProjectExtentsTight(
        IReadOnlyList<Vec3> worldVerts,
        Vec3 n,
        Vec3 u,
        Vec3 v)
    {
        var nProj = worldVerts.Select(p => p.Dot(n)).ToList();
        var uProj = worldVerts.Select(p => p.Dot(u)).ToList();
        var vProj = worldVerts.Select(p => p.Dot(v)).ToList();
        return (
            nProj.Min() - SkinM, nProj.Max() + SkinM,
            uProj.Min() - SkinM, uProj.Max() + SkinM,
            vProj.Min() - SkinM, vProj.Max() + SkinM);
    }

    private static (double nLo, double nHi, double uLo, double uHi, double vLo, double vHi) ProjectExtents(
        IReadOnlyList<Vec3> worldVerts,
        Vec3 n, Vec3 u, Vec3 v,
        PatchKind kind,
        BuildingMeshProfile? profile)
    {
        var nProj = worldVerts.Select(p => p.Dot(n)).ToList();
        var uProj = worldVerts.Select(p => p.Dot(u)).ToList();
        var vProj = worldVerts.Select(p => p.Dot(v)).ToList();

        var uLo = uProj.Min() - SkinM;
        var uHi = uProj.Max() + SkinM;
        var vLo = vProj.Min() - SkinM;
        var vHi = vProj.Max() + SkinM;

        var wallSlab = profile?.WallThicknessM ?? 0.15;
        var horizSlab = profile?.HorizontalSlabM ?? 0.12;

        double nLo, nHi;
        var ns = nProj.OrderBy(x => x).ToList();
        if (kind == PatchKind.Wall)
        {
            nLo = nProj.Min() - SkinM;
            nHi = nProj.Max() + SkinM;
            if (nHi - nLo < wallSlab * 0.5)
            {
                var mid = (nLo + nHi) * 0.5;
                nLo = mid - wallSlab * 0.5;
                nHi = mid + wallSlab * 0.5;
            }
        }
        else if (kind == PatchKind.Horizontal)
        {
            nLo = nProj.Min() - SkinM;
            nHi = nProj.Max() + SkinM;
            if (nHi - nLo < horizSlab)
            {
                var mid = (nLo + nHi) * 0.5;
                nLo = mid - horizSlab * 0.5;
                nHi = mid + horizSlab * 0.5;
            }
        }
        else
        {
            nLo = nProj.Min() - SkinM;
            nHi = nProj.Max() + SkinM;
            if (nHi - nLo < SkinM * 2)
            {
                var mid = (nLo + nHi) * 0.5;
                nLo = mid - SkinM;
                nHi = mid + SkinM;
            }
        }

        return (nLo, nHi, uLo, uHi, vLo, vHi);
    }

    private static (double uLo, double uHi, double vLo, double vHi) ClampInPlaneExtents(
        IReadOnlyList<Vec3> worldVerts,
        Vec3 n, Vec3 u, Vec3 v,
        double uLo, double uHi, double vLo, double vHi,
        BuildingMeshProfile profile,
        PatchKind kind)
    {
        var maxU = System.Math.Max(profile.SizeM.X, profile.SizeM.Y) * 1.05;
        var maxV = kind == PatchKind.Horizontal
            ? maxU
            : System.Math.Max(profile.HeightM, 0.5) * 1.05;

        var uSpan = uHi - uLo;
        var vSpan = vHi - vLo;
        if (uSpan <= maxU && vSpan <= maxV)
            return (uLo, uHi, vLo, vHi);

        // Trim outliers: use percentile range when patch AABB exceeds building bounds
        var uProj = worldVerts.Select(p => p.Dot(u)).OrderBy(x => x).ToList();
        var vProj = worldVerts.Select(p => p.Dot(v)).OrderBy(x => x).ToList();
        if (uProj.Count < 4)
            return (uLo, uHi, vLo, vHi);

        var uP5 = Percentile(uProj, 0.05);
        var uP95 = Percentile(uProj, 0.95);
        var vP5 = Percentile(vProj, 0.05);
        var vP95 = Percentile(vProj, 0.95);

        return (
            uP5 - SkinM, uP95 + SkinM,
            vP5 - SkinM, vP95 + SkinM);
    }

    private static double Percentile(IReadOnlyList<double> sorted, double p)
    {
        if (sorted.Count == 0)
            return 0;
        var idx = (int)System.Math.Round(p * (sorted.Count - 1));
        idx = System.Math.Clamp(idx, 0, sorted.Count - 1);
        return sorted[idx];
    }

    private static Vec3 BuildingAlignedInPlaneAxis(
        IReadOnlyList<Vec3> verts,
        Vec3 n,
        Vec3 centroid,
        BuildingMeshProfile? profile)
    {
        if (profile != null)
        {
            var aligned = TryBuildingAxisBasis(n, profile);
            if (aligned.HasValue)
                return aligned.Value;
        }

        return InPlanePrimaryAxis(verts, n, centroid, profile);
    }

    private static Vec3? TryBuildingAxisBasis(Vec3 n, BuildingMeshProfile profile)
    {
        var nn = n.Normalized();
        var kind = ClassifyPatch(nn);

        if (kind == PatchKind.Horizontal)
        {
            return profile.AxisX.Length() > 1e-6 ? profile.AxisX.Normalized() : null;
        }

        if (kind != PatchKind.Wall)
            return null;

        var ax = System.Math.Abs(nn.Dot(profile.AxisX));
        var ay = System.Math.Abs(nn.Dot(profile.AxisY));

        if (ax >= 0.70 && ax >= ay)
        {
            var u = profile.AxisY.Sub(nn.Scale(profile.AxisY.Dot(nn)));
            return u.Length() > 1e-6 ? u.Normalized() : null;
        }

        if (ay >= 0.70)
        {
            var u = profile.AxisX.Sub(nn.Scale(profile.AxisX.Dot(nn)));
            return u.Length() > 1e-6 ? u.Normalized() : null;
        }

        return null;
    }

    private static Vec3 InPlanePrimaryAxis(
        IReadOnlyList<Vec3> verts,
        Vec3 n,
        Vec3 centroid,
        BuildingMeshProfile? profile)
    {
        var refAxis = System.Math.Abs(n.Dot(new Vec3(0, 0, 1))) > 0.999
            ? new Vec3(1, 0, 0)
            : new Vec3(0, 0, 1);
        var u0 = refAxis.Sub(n.Scale(refAxis.Dot(n))).Normalized();
        var v0 = n.Cross(u0).Normalized();

        double cxx = 0, cyy = 0, cxy = 0;
        foreach (var p in verts)
        {
            var d = p.Sub(centroid);
            var u = d.Dot(u0);
            var v = d.Dot(v0);
            cxx += u * u;
            cyy += v * v;
            cxy += u * v;
        }

        var theta = 0.5 * System.Math.Atan2(2 * cxy, cxx - cyy);
        var uPca = u0.Scale(System.Math.Cos(theta)).Add(v0.Scale(System.Math.Sin(theta))).Normalized();

        if (profile == null)
            return uPca;

        var candidates = new[] { uPca, profile.AxisX, profile.AxisY, profile.AxisX.Scale(-1), profile.AxisY.Scale(-1) };
        Vec3 best = uPca;
        var bestScore = -1.0;
        foreach (var c in candidates)
        {
            var cu = c.Sub(n.Scale(c.Dot(n))).Normalized();
            if (cu.Length() < 1e-6)
                continue;
            var score = System.Math.Abs(uPca.Dot(cu));
            if (score > bestScore)
            {
                bestScore = score;
                best = cu;
            }
        }

        return best;
    }

    private static List<Vec3> BuildCorners(
        Vec3 n, Vec3 u, Vec3 v,
        double nLo, double nHi, double uLo, double uHi, double vLo, double vHi)
    {
        var specs = new (double sn, double su, double sv)[]
        {
            (nLo, uLo, vLo), (nLo, uLo, vHi), (nLo, uHi, vLo), (nLo, uHi, vHi),
            (nHi, uLo, vLo), (nHi, uLo, vHi), (nHi, uHi, vLo), (nHi, uHi, vHi),
        };
        return specs.Select(t => n.Scale(t.sn).Add(u.Scale(t.su)).Add(v.Scale(t.sv))).ToList();
    }

    private enum PatchKind { Wall, Horizontal, Sloped }

    private static PatchKind ClassifyPatch(Vec3 normal)
    {
        var n = normal.Normalized();
        var ax = System.Math.Abs(n.X);
        var ay = System.Math.Abs(n.Y);
        var az = System.Math.Abs(n.Z);
        if (az >= 0.85 && az >= ax && az >= ay)
            return PatchKind.Horizontal;
        if (ax >= 0.70 && ax >= ay && ax >= az)
            return PatchKind.Wall;
        if (ay >= 0.70 && ay >= ax && ay >= az)
            return PatchKind.Wall;
        return PatchKind.Sloped;
    }
}
