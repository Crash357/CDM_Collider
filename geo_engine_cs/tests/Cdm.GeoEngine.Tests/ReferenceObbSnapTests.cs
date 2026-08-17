using Cdm.GeoEngine.Core.Analysis;
using Cdm.GeoEngine.Core.Models;
using Cdm.GeoEngine.Core.Primitives;
using Cdm.GeoEngine.Core.Validation;

namespace Cdm.GeoEngine.Tests;

public class ReferenceObbSnapTests
{
    [Fact]
    public void Snap_MatchesReferenceObbScore()
    {
        var refObb = new OrientedBox
        {
            Center = new Vec3(1, 2, 0.5),
            AxisN = new Vec3(0, 1, 0),
            AxisU = new Vec3(1, 0, 0),
            AxisV = new Vec3(0, 0, 1),
            ExtentN = 0.075,
            ExtentU = 1.2,
            ExtentV = 0.8,
            Corners = ReferenceObbSnap.BuildCorners(new OrientedBox
            {
                Center = new Vec3(1, 2, 0.5),
                AxisN = new Vec3(0, 1, 0),
                AxisU = new Vec3(1, 0, 0),
                AxisV = new Vec3(0, 0, 1),
                ExtentN = 0.075,
                ExtentU = 1.2,
                ExtentV = 0.8,
            }),
        };

        var mesh = ReferenceObbSnap.BuildMesh(refObb);
        Assert.Equal(8, mesh.Vertices.Count);
        Assert.Equal(6, mesh.Faces.Count);

        foreach (var c in refObb.Corners)
        {
            Assert.Contains(mesh.Vertices, v => Vec3.Distance(v, c) < 1e-6);
        }
    }

    [Fact]
    public void Constrained_BlendMovesTowardReference()
    {
        var reference = new OrientedBox
        {
            Center = new Vec3(0, 0, 0),
            AxisN = new Vec3(1, 0, 0),
            AxisU = new Vec3(0, 1, 0),
            AxisV = new Vec3(0, 0, 1),
            ExtentN = 0.1,
            ExtentU = 2,
            ExtentV = 1,
        };
        var fitted = new OrientedBox
        {
            Center = new Vec3(0.5, 0, 0),
            AxisN = new Vec3(1, 0, 0),
            AxisU = new Vec3(0, 1, 0),
            AxisV = new Vec3(0, 0, 1),
            ExtentN = 0.2,
            ExtentU = 3,
            ExtentV = 1.5,
        };

        var blended = ConstrainedObbFitter.Blend(fitted, reference, 0.5);
        Assert.True(Vec3.Distance(blended.Center, reference.Center) <
                    Vec3.Distance(fitted.Center, reference.Center));
        Assert.True(System.Math.Abs(blended.ExtentU - reference.ExtentU) <
                    System.Math.Abs(fitted.ExtentU - reference.ExtentU));
    }
}
