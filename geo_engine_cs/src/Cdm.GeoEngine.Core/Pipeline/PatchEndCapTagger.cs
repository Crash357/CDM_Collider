using Cdm.GeoEngine.Core.Analysis;
using Cdm.GeoEngine.Core.Models;
using Cdm.GeoEngine.Core.Primitives;

namespace Cdm.GeoEngine.Core.Pipeline;

/// <summary>Mark full-height ±X/±Y gable/end panels so trim/span passes do not fragment them (ref04/ref19).</summary>
public static class PatchEndCapTagger
{
    private const double MinEndCapHeightM = 1.85;

    public static IReadOnlyList<PatchCluster> TagEndCaps(
        MeshData mesh,
        IReadOnlyList<PatchCluster> patches,
        BuildingMeshProfile profile)
    {
        var result = new List<PatchCluster>(patches.Count);
        foreach (var patch in patches)
            result.Add(ShouldTag(mesh, patch, profile) ? patch with { SurfaceKind = PatchSurfaceKind.EndCap } : patch);
        return result;
    }

    private static bool ShouldTag(MeshData mesh, PatchCluster patch, BuildingMeshProfile profile)
    {
        if (patch.SurfaceKind != PatchSurfaceKind.Wall)
            return false;

        var n = patch.DominantNormal.Normalized();
        var absAx = System.Math.Abs(n.Dot(profile.AxisX.Normalized()));
        var absAy = System.Math.Abs(n.Dot(profile.AxisY.Normalized()));
        if (absAx < 0.85 && absAy < 0.85)
            return false;

        var verts = FaceBoundsObbFitter.CollectPatchFaceVertices(mesh, patch);
        if (PatchHeightSplitter.HeightSpanM(verts, profile) < MinEndCapHeightM)
            return false;

        var ax = profile.AxisX.Normalized();
        var ay = profile.AxisY.Normalized();
        var maxAlongX = mesh.Vertices.Max(v => v.Dot(ax));
        var maxAlongY = mesh.Vertices.Max(v => v.Dot(ay));
        var cx = verts.Sum(v => v.Dot(ax)) / verts.Count;
        var cy = verts.Sum(v => v.Dot(ay)) / verts.Count;
        var posXMin = maxAlongX - System.Math.Clamp(profile.SizeM.X * 0.14, 0.35, 0.55);
        var posYMin = maxAlongY - System.Math.Clamp(profile.SizeM.Y * 0.22, 0.45, 0.65);

        if (absAx >= 0.85 && cx >= posXMin)
            return false;
        if (absAy >= 0.85 && cy >= posYMin)
            return false;

        return true;
    }
}
