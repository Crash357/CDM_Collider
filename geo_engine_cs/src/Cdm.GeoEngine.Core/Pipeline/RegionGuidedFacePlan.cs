using Cdm.GeoEngine.Core.Models;

namespace Cdm.GeoEngine.Core.Pipeline;

/// <summary>Expanded face sets per semantic region (from sparse seeds).</summary>
public sealed class RegionGuidedFacePlan
{
    public IReadOnlyDictionary<GeoRegionKind, HashSet<int>> FacesByKind { get; init; }
        = new Dictionary<GeoRegionKind, HashSet<int>>();

    public HashSet<int> AllGuidedFaces { get; init; } = new();

    public bool BlindFallbackForUnassigned { get; init; } = true;

    public int UnassignedFaceCount { get; init; }
}
