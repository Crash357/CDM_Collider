using Cdm.GeoEngine.Core.Analysis;
using Cdm.GeoEngine.Core.Models;
using Cdm.GeoEngine.Core.Primitives;
using Cdm.GeoEngine.Core.Topology;

namespace Cdm.GeoEngine.Core.Pipeline;

/// <summary>
/// Split wall patches into a low foundation/base band and upper wall (ref component17-style strips).
/// </summary>
public static class PatchFoundationSplitter
{
    private const double MinFoundationAreaM2 = 0.04;
    private const double MinUpperAreaM2 = 0.06;

    public static IReadOnlyList<PatchCluster> SplitFoundationBands(
        MeshData mesh,
        IReadOnlyList<PatchCluster> patches,
        BuildingMeshProfile profile)
    {
        var az = profile.AxisZ.Normalized();
        var minZ = mesh.Vertices.Count > 0
            ? mesh.Vertices.Min(v => v.Dot(az))
            : 0.0;
        var floorTopZ = minZ + FoundationBandHeightM(profile);

        var result = new List<PatchCluster>();
        foreach (var patch in patches)
        {
            if (patch.SurfaceKind != PatchSurfaceKind.Wall)
            {
                result.Add(patch);
                continue;
            }

            if (ShouldKeepFullHeightEndWall(mesh, patch, profile))
            {
                result.Add(patch);
                continue;
            }

            result.AddRange(SplitOne(mesh, patch, profile, az, floorTopZ));
        }

        return result;
    }

    private static bool ShouldKeepFullHeightEndWall(
        MeshData mesh,
        PatchCluster patch,
        BuildingMeshProfile profile)
    {
        var n = patch.DominantNormal.Normalized();
        var ax = System.Math.Abs(n.Dot(profile.AxisX.Normalized()));
        var ay = System.Math.Abs(n.Dot(profile.AxisY.Normalized()));
        if (ax < 0.85 && ay < 0.85)
            return false;

        var verts = FaceBoundsObbFitter.CollectPatchFaceVertices(mesh, patch);
        return PatchHeightSplitter.HeightSpanM(verts, profile) > 1.35;
    }

    private static double FoundationBandHeightM(BuildingMeshProfile profile)
    {
        var h = profile.SizeM.Z > 0 ? profile.SizeM.Z : 4.0;
        return System.Math.Clamp(h * 0.22, 0.85, 1.15);
    }

    private static IEnumerable<PatchCluster> SplitOne(
        MeshData mesh,
        PatchCluster patch,
        BuildingMeshProfile profile,
        Vec3 az,
        double floorTopZ)
    {
        var low = new List<int>();
        var high = new List<int>();
        foreach (var fi in patch.FaceIndices)
        {
            if (fi < 0 || fi >= mesh.Faces.Count)
                continue;
            var z = FaceCentroid(mesh, mesh.Faces[fi]).Dot(az);
            if (z <= floorTopZ)
                low.Add(fi);
            else
                high.Add(fi);
        }

        if (low.Count == 0 || high.Count == 0)
        {
            yield return patch;
            yield break;
        }

        var lowPatch = BuildSubPatch(mesh, low, patch, MinFoundationAreaM2);
        var highPatch = BuildSubPatch(mesh, high, patch, MinUpperAreaM2);
        if (lowPatch != null)
            yield return lowPatch with { SurfaceKind = PatchSurfaceKind.Wall };
        else
            high.AddRange(low);
        if (highPatch != null)
            yield return highPatch with { SurfaceKind = PatchSurfaceKind.Wall };
        else if (lowPatch == null)
            yield return patch;
    }

    private static PatchCluster? BuildSubPatch(
        MeshData mesh,
        List<int> faceIndices,
        PatchCluster parent,
        double minAreaM2)
    {
        if (faceIndices.Count == 0)
            return null;

        var vertSet = new HashSet<int>();
        var area = 0.0;
        foreach (var fi in faceIndices)
        {
            area += MeshTopology.FaceArea(mesh, mesh.Faces[fi]);
            foreach (var vi in mesh.Faces[fi])
                vertSet.Add(vi);
        }

        if (vertSet.Count < 3 || area < minAreaM2)
            return null;

        return new PatchCluster(
            faceIndices,
            vertSet.Select(vi => mesh.Vertices[vi]).ToList(),
            area,
            parent.DominantNormal,
            parent.ReferenceIndex,
            PatchSurfaceKind.Wall);
    }

    private static Vec3 FaceCentroid(MeshData mesh, int[] face)
    {
        var sum = new Vec3(0, 0, 0);
        foreach (var vi in face)
            sum = sum.Add(mesh.Vertices[vi]);
        return sum.Scale(1.0 / face.Length);
    }
}
