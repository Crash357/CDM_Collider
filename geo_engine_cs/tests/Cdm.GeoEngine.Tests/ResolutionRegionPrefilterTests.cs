using Cdm.GeoEngine.Core.Analysis;
using Cdm.GeoEngine.Core.Models;
using Cdm.GeoEngine.Core.Pipeline;
using Cdm.GeoEngine.Core.Primitives;

namespace Cdm.GeoEngine.Tests;

public class ResolutionRegionPrefilterTests
{
    [Fact]
    public void Apply_ExtractsDoorsBeforeWallAxis()
    {
        var mesh = ShedLikeMesh();
        mesh.VertexGroups["doors1"] = new List<int> { 0, 1, 2, 3, 4, 5, 6, 7 };

        var profile = BuildingMeshAnalyzer.Analyze(mesh);
        var result = ResolutionRegionPrefilter.Apply(mesh, profile);

        Assert.Single(result.Components);
        Assert.StartsWith("Component", result.Components[0].Name);
        Assert.True(result.ConsumedFaceCount > 0);
        Assert.True(result.RemainingMesh.Faces.Count < mesh.Faces.Count);
    }

    [Fact]
    public void IsLinearProp_DetectsLadderShape()
    {
        var profile = new BuildingMeshProfile
        {
            SizeM = new Vec3(0.4, 0.12, 3.2),
            FootprintAreaM2 = 0.048,
            AxisX = new Vec3(1, 0, 0),
            AxisY = new Vec3(0, 1, 0),
            AxisZ = new Vec3(0, 0, 1),
        };

        Assert.True(ResolutionRegionPrefilter.IsLinearProp(profile));
    }

    [Fact]
    public void BuildLinearPropComponent_ReturnsSingleObb()
    {
        var mesh = LadderLikeMesh();
        var profile = BuildingMeshAnalyzer.Analyze(mesh);
        var comp = ResolutionRegionPrefilter.BuildLinearPropComponent(mesh, profile);

        Assert.NotNull(comp);
        Assert.Equal("Component01", comp!.Name);
        Assert.True(comp.Mesh.Vertices.Count >= 8);
    }

    private static MeshData ShedLikeMesh()
    {
        var mesh = new MeshData { Name = "Shed" };
        AddBox(mesh, new Vec3(0, 0, 0), new Vec3(2, 3, 2));
        return mesh;
    }

    private static MeshData LadderLikeMesh()
    {
        var mesh = new MeshData { Name = "Ladder" };
        AddBox(mesh, new Vec3(0, 0, 0), new Vec3(0.35, 0.12, 3.0));
        return mesh;
    }

    private static void AddBox(MeshData mesh, Vec3 min, Vec3 max)
    {
        var baseIdx = mesh.Vertices.Count;
        mesh.Vertices.AddRange(new[]
        {
            new Vec3(min.X, min.Y, min.Z),
            new Vec3(max.X, min.Y, min.Z),
            new Vec3(max.X, max.Y, min.Z),
            new Vec3(min.X, max.Y, min.Z),
            new Vec3(min.X, min.Y, max.Z),
            new Vec3(max.X, min.Y, max.Z),
            new Vec3(max.X, max.Y, max.Z),
            new Vec3(min.X, max.Y, max.Z),
        });
        mesh.Faces.AddRange(new[]
        {
            new[] { baseIdx, baseIdx + 1, baseIdx + 2, baseIdx + 3 },
            new[] { baseIdx + 4, baseIdx + 7, baseIdx + 6, baseIdx + 5 },
            new[] { baseIdx, baseIdx + 4, baseIdx + 5, baseIdx + 1 },
            new[] { baseIdx + 2, baseIdx + 6, baseIdx + 7, baseIdx + 3 },
            new[] { baseIdx, baseIdx + 3, baseIdx + 7, baseIdx + 4 },
            new[] { baseIdx + 1, baseIdx + 5, baseIdx + 6, baseIdx + 2 },
        });
    }
}
