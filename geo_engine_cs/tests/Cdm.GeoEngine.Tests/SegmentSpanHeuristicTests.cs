using Cdm.GeoEngine.Core.Analysis;
using Cdm.GeoEngine.Core.Primitives;
using Xunit;

namespace Cdm.GeoEngine.Tests;

public sealed class SegmentSpanHeuristicTests
{
    [Fact]
    public void CoarseSearch_SmallBuilding_IncludesLoosenAndTighten()
    {
        var profile = new BuildingMeshProfile { SizeM = new Vec3(4, 3, 2.5) };
        var factors = SegmentSpanHeuristic.CoarseSearchSpanFactors(profile, targetComponentCount: 22);
        Assert.Equal(7, factors.Count);
        Assert.All(factors, f => Assert.InRange(f, SegmentSpanHeuristic.MinSpanFactor, SegmentSpanHeuristic.MaxSpanFactor));
        Assert.Contains(factors, f => f > 1.0);
        Assert.Contains(factors, f => f < 1.0);
    }

    [Fact]
    public void CoarseSearch_LargeBuilding_ReturnsFewerFactors()
    {
        var profile = new BuildingMeshProfile { SizeM = new Vec3(24, 8, 3) };
        var factors = SegmentSpanHeuristic.CoarseSearchSpanFactors(profile, targetComponentCount: 50);
        Assert.Equal(3, factors.Count);
        Assert.Contains(1.0, factors);
    }

    [Fact]
    public void RefineSpanFactors_ClampsToRange()
    {
        var refined = SegmentSpanHeuristic.RefineSpanFactors(1.0).ToList();
        Assert.NotEmpty(refined);
        Assert.All(refined, f => Assert.InRange(f, SegmentSpanHeuristic.MinSpanFactor, SegmentSpanHeuristic.MaxSpanFactor));
        Assert.Contains(refined, f => System.Math.Abs(f - 1.0) < 1e-6);
    }
}
