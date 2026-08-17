using Cdm.GeoEngine.Core.Analysis;
using Cdm.GeoEngine.Core.Primitives;

namespace Cdm.GeoEngine.Tests;

public class SegmentSpanCalibratorTests
{
    [Fact]
    public void ShedSizedFootprint_With22Components_YieldsShortSegments()
    {
        var profile = new BuildingMeshProfile
        {
            SizeM = new Vec3(4, 3, 2.5),
            HeightM = 2.5,
        };
        var span = SegmentSpanCalibrator.EstimateWallMaxSpanM(profile, 22);
        Assert.InRange(span, 1.25, 1.85);
    }

    [Fact]
    public void LargeBarracks_With26Components_ModerateSpan()
    {
        var profile = new BuildingMeshProfile
        {
            SizeM = new Vec3(18, 12, 4),
            HeightM = 4,
        };
        var span = SegmentSpanCalibrator.EstimateWallMaxSpanM(profile, 26);
        Assert.InRange(span, 2.0, 4.5);
    }
}
