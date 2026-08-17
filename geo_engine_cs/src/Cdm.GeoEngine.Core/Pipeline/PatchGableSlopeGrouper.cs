using Cdm.GeoEngine.Core.Analysis;
using Cdm.GeoEngine.Core.Models;
using Cdm.GeoEngine.Core.Primitives;
using Cdm.GeoEngine.Core.Topology;

namespace Cdm.GeoEngine.Core.Pipeline;

/// <summary>Full gable roof slopes (ref04 +X, ref19 +Y) — building-axis end pockets.</summary>
public static class PatchGableSlopeGrouper
{
    public static (HashSet<int> GableFaces, IReadOnlyList<PatchCluster> GablePatches) Extract(
        MeshData mesh,
        BuildingMeshProfile profile,
        HashSet<int> reservedFaces,
        double minAreaM2 = 0.05)
    {
        if (mesh.Faces.Count == 0)
            return (new HashSet<int>(), Array.Empty<PatchCluster>());

        var ax = profile.AxisX.Normalized();
        var ay = profile.AxisY.Normalized();
        var az = profile.AxisZ.Normalized();
        var alongX = mesh.Vertices.Select(v => v.Dot(ax)).ToList();
        var alongY = mesh.Vertices.Select(v => v.Dot(ay)).ToList();
        var minAlongX = alongX.Min();
        var maxAlongX = alongX.Max();
        var maxAlongY = alongY.Max();
        var centerAlongX = (minAlongX + maxAlongX) * 0.5;
        var soffitPocketMaxAlongX = centerAlongX + 0.35;
        var rearMinAlongY = maxAlongY - System.Math.Clamp(profile.SizeM.Y * 0.26, 0.42, 0.62);
        var posXObbMin = maxAlongX - System.Math.Clamp(profile.SizeM.X * 0.14, 0.35, 0.55);
        var posXBandMin = posXObbMin - System.Math.Clamp(profile.SizeM.X * 0.08, 0.15, 0.25);
        var posYObbMin = maxAlongY - System.Math.Clamp(profile.SizeM.Y * 0.22, 0.45, 0.65);
        var posYBandMin = posYObbMin - System.Math.Clamp(profile.SizeM.Y * 0.08, 0.15, 0.25);

        var faceNormals = mesh.Faces
            .Select(f => MeshTopology.FaceNormal(mesh, f).Normalized())
            .ToArray();

        var posXFaces = new List<int>();
        var posXBandReserved = new List<int>();
        var posYFaces = new List<int>();
        for (var fi = 0; fi < mesh.Faces.Count; fi++)
        {
            if (reservedFaces.Contains(fi))
                continue;

            var absZ = System.Math.Abs(faceNormals[fi].Dot(az));
            if (absZ > 0.92)
                continue;

            var c = FaceCentroid(mesh, mesh.Faces[fi]);
            var cAlongX = c.Dot(ax);
            var cAlongY = c.Dot(ay);
            if (cAlongX <= soffitPocketMaxAlongX && cAlongY >= rearMinAlongY)
                continue;

            if (cAlongX >= posXObbMin)
                posXFaces.Add(fi);
            else if (cAlongX >= posXBandMin)
                posXBandReserved.Add(fi);
            else if (cAlongY >= posYBandMin)
                posYFaces.Add(fi);
        }

        var gableFaces = new HashSet<int>();
        foreach (var fi in posXBandReserved)
            gableFaces.Add(fi);
        var patches = new List<PatchCluster>();
        foreach (var (group, endAxis, gableEnd) in new (List<int>, Vec3, GableEndKind)[]
                 { (posXFaces, ax, GableEndKind.PosX), (posYFaces, ay, GableEndKind.PosY) })
        {
            if (group.Count == 0)
                continue;

            foreach (var fi in group)
                gableFaces.Add(fi);

            var hintOutward = endAxis.Scale(-1).Add(az.Scale(0.65)).Normalized();
            var patch = BuildPatch(mesh, group, faceNormals, profile, minAreaM2, hintOutward, gableEnd);
            if (patch != null)
                patches.Add(patch);
        }

        return (gableFaces, patches);
    }

    private static PatchCluster? BuildPatch(
        MeshData mesh,
        List<int> faceIndices,
        Vec3[] faceNormals,
        BuildingMeshProfile profile,
        double minAreaM2,
        Vec3 hintOutward,
        GableEndKind gableEnd)
    {
        var az = profile.AxisZ.Normalized();
        var vertSet = new HashSet<int>();
        var slopedSum = new Vec3(0, 0, 0);
        var slopedArea = 0.0;
        var area = 0.0;
        foreach (var fi in faceIndices)
        {
            var a = MeshTopology.FaceArea(mesh, mesh.Faces[fi]);
            area += a;
            if (System.Math.Abs(faceNormals[fi].Dot(az)) >= 0.22)
            {
                slopedSum = slopedSum.Add(faceNormals[fi].Scale(a));
                slopedArea += a;
            }

            foreach (var vi in mesh.Faces[fi])
                vertSet.Add(vi);
        }

        if (vertSet.Count < 3 || area < minAreaM2)
            return null;

        var avgN = slopedArea > minAreaM2 * 0.12 && slopedSum.Length() > 1e-6
            ? slopedSum.Normalized()
            : hintOutward;

        return new PatchCluster(
            faceIndices,
            vertSet.Select(vi => mesh.Vertices[vi]).ToList(),
            area,
            avgN,
            SurfaceKind: PatchSurfaceKind.Slope,
            GableEnd: gableEnd);
    }

    private static Vec3 FaceCentroid(MeshData mesh, int[] face)
    {
        var sum = new Vec3(0, 0, 0);
        foreach (var vi in face)
            sum = sum.Add(mesh.Vertices[vi]);
        return sum.Scale(1.0 / face.Length);
    }
}
