using Cdm.GeoEngine.Core.Models;
using Cdm.GeoEngine.Core.Pipeline;

namespace Cdm.GeoEngine.Core.Analysis;

/// <summary>
/// Blind patch fit: face vertices from the patch only → 8-corner box via <see cref="FaceBoundsObbFitter"/>.
/// </summary>
public static class ResolutionGuidedObbFitter
{
    public static MeshData? BuildPatchMesh(
        MeshData resolutionLod,
        PatchCluster patch,
        BuildingMeshProfile profile)
    {
        var verts = FaceBoundsObbFitter.CollectPatchFaceVertices(resolutionLod, patch);
        return ObbFitter.BuildPatchMeshTight(verts, patch.DominantNormal, profile)
            ?? ObbFitter.BuildPatchMesh(verts, patch.DominantNormal, profile);
    }
}
