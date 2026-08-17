using Cdm.GeoEngine.Core.Analysis;
using Cdm.GeoEngine.Core.Models;
using Cdm.GeoEngine.Core.Primitives;
using Cdm.GeoEngine.Core.Validation;

namespace Cdm.GeoEngine.Tests;

public class ResolutionCoverageTests
{
    [Fact]
    public void Coverage_FullBoxCoversAllFaceCentroids()
    {
        var mesh = new MeshData { Name = "Room" };
        var w = 4.0;
        var d = 3.0;
        var h = 2.5;
        mesh.Vertices.AddRange(new[]
        {
            new Vec3(0, 0, 0), new Vec3(w, 0, 0), new Vec3(w, d, 0), new Vec3(0, d, 0),
            new Vec3(0, 0, h), new Vec3(w, 0, h), new Vec3(w, d, h), new Vec3(0, d, h),
        });
        mesh.Faces.AddRange(new[]
        {
            new[] { 0, 1, 2, 3 },
            new[] { 4, 5, 6, 7 },
            new[] { 0, 1, 5, 4 },
        });

        var box = new OrientedBox
        {
            Center = new Vec3(w * 0.5, d * 0.5, h * 0.5),
            AxisU = new Vec3(1, 0, 0),
            AxisV = new Vec3(0, 1, 0),
            AxisN = new Vec3(0, 0, 1),
            ExtentU = w * 0.5,
            ExtentV = d * 0.5,
            ExtentN = h * 0.5,
        };

        var score = ResolutionCoverageScorer.Score(mesh, new[] { box });
        Assert.Equal(mesh.Faces.Count, score.SamplesTotal);
        Assert.Equal(1.0, score.FractionInside, 3);
    }
}
