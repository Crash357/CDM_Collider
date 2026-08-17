using Cdm.GeoEngine.Core.Analysis;
using Cdm.GeoEngine.Core.Models;
using Cdm.GeoEngine.Core.Primitives;
using Cdm.GeoEngine.Core.Topology;

namespace Cdm.GeoEngine.Core.Pipeline;

/// <summary>Face-driven decomposition constrained by expanded region plans from sparse seeds.</summary>
public static class RegionGuidedDecomposer
{
    public static IReadOnlyList<PatchCluster> Split(
        MeshData mesh,
        double minAreaM2,
        BuildingMeshProfile profile,
        IReadOnlyList<DoorRegion>? doorRegions,
        double wallMaxSpanM,
        RegionGuidedFacePlan plan)
        => FaceDrivenDecomposer.Split(
            mesh,
            minAreaM2,
            profile,
            doorRegions,
            useWallEdgeSegmentation: true,
            wallMaxSpanM: wallMaxSpanM,
            regionPlan: plan);
}
