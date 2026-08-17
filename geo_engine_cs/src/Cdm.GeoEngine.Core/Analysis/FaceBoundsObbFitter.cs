using Cdm.GeoEngine.Core.DayZ;
using Cdm.GeoEngine.Core.Models;
using Cdm.GeoEngine.Core.Pipeline;
using Cdm.GeoEngine.Core.Primitives;

namespace Cdm.GeoEngine.Core.Analysis;

/// <summary>
/// Build 8-corner collision boxes directly from face vertex bounds on building axes.
/// No PCA rotation, no resolution vertex-cloud sampling, no shell center shift.
/// </summary>
public static class FaceBoundsObbFitter
{
    private static double SkinM => GeometryLodConstants.OverhangM;

    public static OrientedBox? FitPatch(
        IReadOnlyList<Vec3> faceVerts,
        Vec3 hintNormal,
        BuildingMeshProfile profile,
        PatchSurfaceKind surfaceKind = PatchSurfaceKind.Wall)
    {
        if (faceVerts.Count == 0)
            return null;

        var n = surfaceKind == PatchSurfaceKind.Slope
            ? hintNormal.Normalized()
            : BuildingMeshAnalyzer.SnapNormalToBuildingAxes(hintNormal, profile);
        var kind = ToObbKind(surfaceKind, n);
        var (u, v) = ResolveInPlaneAxes(n, profile, kind, faceVerts);
        if (u == null)
            return ObbFitter.FitPatchTight(faceVerts, hintNormal, profile);

        var uAxis = u.Value;
        var vAxis = v ?? n.Cross(uAxis).Normalized();

        var nProj = faceVerts.Select(p => p.Dot(n)).ToArray();
        var uProj = faceVerts.Select(p => p.Dot(uAxis)).ToArray();
        var vProj = faceVerts.Select(p => p.Dot(vAxis)).ToArray();

        var uLo = uProj.Min() - SkinM;
        var uHi = uProj.Max() + SkinM;
        var vLo = vProj.Min() - SkinM;
        var vHi = vProj.Max() + SkinM;

        var wallThick = profile.WallThicknessM;
        var slabThick = profile.HorizontalSlabM;
        var nSpan = nProj.Max() - nProj.Min();

        double nLo;
        double nHi;
        if (nSpan >= wallThick * 0.75 && kind == PatchKind.Wall)
        {
            nLo = nProj.Min() - SkinM;
            nHi = nProj.Max() + SkinM;
        }
        else if (kind == PatchKind.Wall && nSpan > wallThick * 1.35 && nSpan < wallThick * 3.5)
        {
            var outer = nProj.Max();
            nHi = outer + SkinM;
            nLo = outer - wallThick - SkinM;
        }
        else if (kind == PatchKind.Wall)
        {
            nLo = nProj.Min() - SkinM;
            nHi = nProj.Max() + SkinM;
            if (nHi - nLo < wallThick)
            {
                var mid = (nLo + nHi) * 0.5;
                nLo = mid - wallThick * 0.5;
                nHi = mid + wallThick * 0.5;
            }
        }
        else if (kind == PatchKind.Horizontal)
        {
            var az = profile.AxisZ.Normalized();
            var zMin = faceVerts.Min(v => v.Dot(az));
            if (nSpan > profile.SizeM.Z * 0.32 && zMin > profile.SizeM.Z * 0.22)
            {
                var outer = nProj.Max();
                nHi = outer + SkinM;
                nLo = outer - slabThick - SkinM;
            }
            else
            {
                nLo = nProj.Min() - SkinM;
                nHi = nProj.Max() + SkinM;
                if (nHi - nLo < slabThick)
                {
                    var mid = (nLo + nHi) * 0.5;
                    nLo = mid - slabThick * 0.5;
                    nHi = mid + slabThick * 0.5;
                }
            }
        }
        else if (kind == PatchKind.Sloped)
        {
            nLo = nProj.Min() - SkinM;
            nHi = nProj.Max() + SkinM;
            if (nHi - nLo < wallThick)
            {
                var mid = (nLo + nHi) * 0.5;
                nLo = mid - wallThick * 0.5;
                nHi = mid + wallThick * 0.5;
            }
        }
        else if (nSpan >= slabThick * 0.5)
        {
            nLo = nProj.Min() - SkinM;
            nHi = nProj.Max() + SkinM;
        }
        else
        {
            var outer = nProj.Max() + SkinM;
            nHi = outer;
            nLo = outer - wallThick;
        }

        return BuildObb(n, uAxis, vAxis, nLo, nHi, uLo, uHi, vLo, vHi);
    }

    public static MeshData? BuildPatchMesh(
        IReadOnlyList<Vec3> faceVerts,
        Vec3 hintNormal,
        BuildingMeshProfile profile,
        PatchSurfaceKind surfaceKind = PatchSurfaceKind.Wall)
    {
        var obb = FitPatch(faceVerts, hintNormal, profile, surfaceKind);
        if (obb == null)
            return null;

        var mesh = new MeshData();
        foreach (var c in obb.Corners)
            mesh.Vertices.Add(c);
        foreach (var face in GeometryLodConstants.BoxFaces)
            mesh.Faces.Add(face.ToArray());
        return mesh;
    }

    public static List<Vec3> CollectPatchFaceVertices(MeshData mesh, PatchCluster patch)
    {
        if (patch.FaceIndices.Count == 0)
            return patch.WorldVertices.ToList();

        var indices = new HashSet<int>();
        foreach (var fi in patch.FaceIndices)
        {
            if (fi < 0 || fi >= mesh.Faces.Count)
                continue;
            foreach (var vi in mesh.Faces[fi])
                indices.Add(vi);
        }

        if (indices.Count == 0)
            return patch.WorldVertices.ToList();

        return indices.Select(i => mesh.Vertices[i]).ToList();
    }

    /// <summary>+Y gable: OBB from inner band only; +X gable unchanged (ref04).</summary>
    public static List<Vec3> CollectGableObbVertices(
        MeshData mesh,
        PatchCluster patch,
        BuildingMeshProfile profile)
    {
        var verts = CollectPatchFaceVertices(mesh, patch);
        if (patch.SurfaceKind != PatchSurfaceKind.Slope || patch.GableEnd != GableEndKind.PosY || verts.Count < 4)
            return verts;

        var ax = profile.AxisX.Normalized();
        var ay = profile.AxisY.Normalized();
        var maxAx = mesh.Vertices.Max(v => v.Dot(ax));
        var maxAy = mesh.Vertices.Max(v => v.Dot(ay));
        var posXDepth = System.Math.Clamp(profile.SizeM.X * 0.14, 0.35, 0.55);
        var posYDepth = System.Math.Clamp(profile.SizeM.Y * 0.22, 0.45, 0.65);
        var posXMin = maxAx - posXDepth;
        var posYObbMin = maxAy - posYDepth;
        var posXCutoff = posXMin + 0.08;
        var filtered = verts
            .Where(v => v.Dot(ay) >= posYObbMin && v.Dot(ax) < posXCutoff)
            .ToList();
        if (filtered.Count < 4)
        {
            filtered = verts.Where(v => v.Dot(ay) >= posYObbMin).ToList();
        }
        return filtered.Count >= 4 ? filtered : verts;
    }

    private static OrientedBox BuildObb(
        Vec3 n, Vec3 u, Vec3 v,
        double nLo, double nHi, double uLo, double uHi, double vLo, double vHi)
    {
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

    private static (Vec3? U, Vec3? V) ResolveInPlaneAxes(
        Vec3 n,
        BuildingMeshProfile profile,
        PatchKind kind,
        IReadOnlyList<Vec3>? faceVerts)
    {
        var nn = n.Normalized();

        if (kind == PatchKind.Sloped && faceVerts is { Count: > 0 })
            return PickGableSlopeFrame(faceVerts, nn, profile);

        var u = BuildingInPlaneAxis(nn, profile, kind, faceVerts);
        if (u == null)
            return (null, null);

        return (u, nn.Cross(u.Value).Normalized());
    }

    /// <summary>u = height along slope (world Z projected); v = ridge/run along slope.</summary>
    private static (Vec3 U, Vec3 V) PickGableSlopeFrame(
        IReadOnlyList<Vec3> faceVerts,
        Vec3 n,
        BuildingMeshProfile profile)
    {
        var az = profile.AxisZ.Normalized();
        var heightOnSlope = az.Sub(n.Scale(az.Dot(n)));
        Vec3 u;
        if (heightOnSlope.Length() > 1e-6)
            u = heightOnSlope.Normalized();
        else
            u = PickSlopedAxes(faceVerts, n, profile) ?? profile.AxisX.Normalized();

        var v = n.Cross(u);
        if (v.Length() < 1e-6)
            v = profile.AxisX.Sub(n.Scale(profile.AxisX.Dot(n)));
        v = v.Normalized();

        var uSpan = ProjectSpan(faceVerts, u);
        var vSpan = ProjectSpan(faceVerts, v);
        if (vSpan > uSpan * 1.08)
        {
            (u, v) = (v, u);
            uSpan = ProjectSpan(faceVerts, u);
            vSpan = ProjectSpan(faceVerts, v);
        }

        if (ProjectSpan(faceVerts, az) > uSpan * 1.05 && heightOnSlope.Length() > 1e-6)
        {
            u = heightOnSlope.Normalized();
            v = n.Cross(u).Normalized();
        }

        return (u, v);
    }

    private static double ProjectSpan(IReadOnlyList<Vec3> verts, Vec3 axis)
    {
        var a = axis.Normalized();
        return verts.Max(v => v.Dot(a)) - verts.Min(v => v.Dot(a));
    }

    private static Vec3? BuildingInPlaneAxis(
        Vec3 n,
        BuildingMeshProfile profile,
        PatchKind kind,
        IReadOnlyList<Vec3>? faceVerts = null)
    {
        var nn = n.Normalized();

        if (kind == PatchKind.Wall)
        {
            var height = profile.AxisZ.Sub(nn.Scale(profile.AxisZ.Dot(nn)));
            if (height.Length() > 1e-6)
                return height.Normalized();
        }

        if (kind == PatchKind.Horizontal)
            return profile.AxisX.Length() > 1e-6 ? profile.AxisX.Normalized() : null;

        if (kind == PatchKind.Sloped)
        {
            if (faceVerts is { Count: > 0 })
            {
                var ridge = PickSlopedAxes(faceVerts, nn, profile);
                if (ridge != null)
                    return ridge;
            }

            var height = profile.AxisZ.Sub(nn.Scale(profile.AxisZ.Dot(nn)));
            if (height.Length() > 1e-6)
                return height.Normalized();
        }

        if (kind != PatchKind.Wall)
            return profile.AxisX.Length() > 1e-6 ? profile.AxisX.Normalized() : null;

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

    private enum PatchKind { Wall, Horizontal, Sloped }

    private static PatchKind ToObbKind(PatchSurfaceKind surfaceKind, Vec3 snappedNormal) =>
        surfaceKind switch
        {
            PatchSurfaceKind.Horizontal => PatchKind.Horizontal,
            PatchSurfaceKind.Plinth => PatchKind.Horizontal,
            PatchSurfaceKind.Soffit => PatchKind.Horizontal,
            PatchSurfaceKind.EndCap => PatchKind.Wall,
            PatchSurfaceKind.Slope => PatchKind.Sloped,
            _ => ClassifyPatch(snappedNormal),
        };

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

    private static Vec3? PickSlopedAxes(
        IReadOnlyList<Vec3> faceVerts,
        Vec3 n,
        BuildingMeshProfile profile)
    {
        Vec3? best = null;
        var bestSpan = 0.0;
        foreach (var axis in new[] { profile.AxisX, profile.AxisY, profile.AxisZ })
        {
            var proj = axis.Sub(n.Scale(axis.Dot(n)));
            if (proj.Length() < 1e-6)
                continue;
            proj = proj.Normalized();
            var span = faceVerts.Max(v => v.Dot(proj)) - faceVerts.Min(v => v.Dot(proj));
            if (span > bestSpan)
            {
                bestSpan = span;
                best = proj;
            }
        }

        return best;
    }
}
