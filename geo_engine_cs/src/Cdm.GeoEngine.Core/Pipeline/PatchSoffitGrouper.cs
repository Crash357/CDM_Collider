using Cdm.GeoEngine.Core.Analysis;
using Cdm.GeoEngine.Core.Models;
using Cdm.GeoEngine.Core.Primitives;
using Cdm.GeoEngine.Core.Topology;

namespace Cdm.GeoEngine.Core.Pipeline;

/// <summary>Rear eave/soffit strip (ref component08) — disabled until resolution mesh exposes a stable pocket.</summary>
public static class PatchSoffitGrouper
{
    public static (HashSet<int> SoffitFaces, IReadOnlyList<PatchCluster> SoffitPatches) Extract(
        MeshData mesh,
        BuildingMeshProfile profile,
        double minAreaM2 = 0.03)
        => (new HashSet<int>(), Array.Empty<PatchCluster>());
}
