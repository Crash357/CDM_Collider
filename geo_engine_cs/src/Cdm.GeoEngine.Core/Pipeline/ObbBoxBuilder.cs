using Cdm.GeoEngine.Core.DayZ;
using Cdm.GeoEngine.Core.Primitives;
using Cdm.GeoEngine.Core.Models;

namespace Cdm.GeoEngine.Core.Pipeline;

public static class ObbBoxBuilder
{
    public const double SkinM = 0.001;
    public const double WallSlabM = 0.15;
    public const double HorizSlabM = 0.12;

    public static MeshData? BuildPatchBox(IReadOnlyList<Vec3> worldVerts, Vec3 normal)
    {
        if (worldVerts.Count == 0)
            return null;

        var kind = ClassifyPatch(normal);
        var (n, u, v) = BuildBasis(normal);

        var nProj = worldVerts.Select(p => p.Dot(n)).ToList();
        var uProj = worldVerts.Select(p => p.Dot(u)).ToList();
        var vProj = worldVerts.Select(p => p.Dot(v)).ToList();

        var uLo = uProj.Min() - SkinM;
        var uHi = uProj.Max() + SkinM;
        var vLo = vProj.Min() - SkinM;
        var vHi = vProj.Max() + SkinM;

        double nLo, nHi;
        var ns = nProj.OrderBy(x => x).ToList();
        if (kind == PatchKind.Wall)
        {
            var nQ1 = ns[System.Math.Max(0, ns.Count / 4)];
            var nQ3 = ns[System.Math.Min(ns.Count - 1, 3 * ns.Count / 4)];
            var nSurface = (nQ1 + nQ3) * 0.5;
            nHi = nSurface + SkinM;
            nLo = nSurface - System.Math.Max(nQ3 - nQ1, WallSlabM);
        }
        else if (kind == PatchKind.Horizontal)
        {
            nLo = nProj.Min() - SkinM;
            nHi = nProj.Max() + SkinM;
            if (nHi - nLo < HorizSlabM)
            {
                var mid = (nLo + nHi) * 0.5;
                nLo = mid - HorizSlabM * 0.5;
                nHi = mid + HorizSlabM * 0.5;
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

        return BuildBoxMesh(n, u, v, nLo, nHi, uLo, uHi, vLo, vHi);
    }

    public static MeshData BuildBoxMesh(
        Vec3 n, Vec3 u, Vec3 v,
        double nLo, double nHi, double uLo, double uHi, double vLo, double vHi)
    {
        var specs = new (double sn, double su, double sv)[]
        {
            (nLo, uLo, vLo), (nLo, uLo, vHi), (nLo, uHi, vLo), (nLo, uHi, vHi),
            (nHi, uLo, vLo), (nHi, uLo, vHi), (nHi, uHi, vLo), (nHi, uHi, vHi),
        };

        var mesh = new MeshData();
        foreach (var (sn, su, sv) in specs)
            mesh.Vertices.Add(n.Scale(sn).Add(u.Scale(su)).Add(v.Scale(sv)));

        foreach (var face in GeometryLodConstants.BoxFaces)
            mesh.Faces.Add(face.ToArray());

        return mesh;
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

    private static (Vec3 n, Vec3 u, Vec3 v) BuildBasis(Vec3 normal)
    {
        var n = normal.Normalized();
        var reference = System.Math.Abs(n.Dot(new Vec3(0, 0, 1))) > 0.999
            ? new Vec3(1, 0, 0)
            : new Vec3(0, 0, 1);
        var u = reference.Sub(n.Scale(reference.Dot(n))).Normalized();
        var v = n.Cross(u).Normalized();
        return (n, u, v);
    }
}
