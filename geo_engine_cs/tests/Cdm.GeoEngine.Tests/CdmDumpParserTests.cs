using Cdm.GeoEngine.Core.IO;
using Cdm.GeoEngine.Core.Primitives;
using Cdm.GeoEngine.Core.Models;
using Cdm.GeoEngine.Core.Pipeline;

namespace Cdm.GeoEngine.Tests;

public class CdmDumpParserTests
{
    [Fact]
    public void ParseFactoryBuildingDump_HasVerticesAndFaces()
    {
        var dumpDir = FindGeoDumpDir();
        var path = Path.Combine(dumpDir, "Factory_32_00_gebaeude.txt");
        if (!File.Exists(path))
            return;

        var mesh = CdmDumpParser.ParseMeshDump(File.ReadAllText(path));
        Assert.Equal("Factory_32", mesh.Name);
        Assert.True(mesh.VertexCount > 3000);
        Assert.True(mesh.FaceCount > 3000);
    }

    [Fact]
    public void CollectFromGeoDump_FindsFactoryPair()
    {
        var dumpDir = FindGeoDumpDir();
        if (!Directory.Exists(dumpDir))
            return;

        var datasets = DatasetCollector.CollectFromDirectory(dumpDir);
        Assert.NotEmpty(datasets);

        var factory = datasets.FirstOrDefault(d => d.ModelName.Contains("Factory_32", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(factory);
        Assert.NotNull(factory!.ResolutionLod);
        Assert.NotNull(factory.GeometryLod);
        Assert.True(factory.ReferenceComponents.Count > 0 || factory.GeometryLod!.VertexGroups.Count > 0);
    }

    [Fact]
    public void GenerateFactoryGeometry_ProducesComponents()
    {
        var dumpDir = FindGeoDumpDir();
        var path = Path.Combine(dumpDir, "Factory_32_00_gebaeude.txt");
        if (!File.Exists(path))
            return;

        var mesh = CdmDumpParser.ParseMeshDump(File.ReadAllText(path));
        var result = BuildingGeometryEngine.Generate(mesh, new BuildingGeometryOptions
        {
            MinAreaM2 = 0.25,
            AngleThresholdDeg = 30,
            RequireDoorVertices = false,
        });

        Assert.True(result.Components.Count > 50);
        Assert.True(result.GeometryLod.VertexCount > result.Components.Count * 6);
        Assert.Equal(1.0e13, (double)result.GeometryLod.Properties["LOD"]);
    }

    [Fact]
    public void ObbBox_BuildsEightVertices()
    {
        var verts = new[]
        {
            new Vec3(0, 0, 0), new Vec3(1, 0, 0), new Vec3(1, 1, 0), new Vec3(0, 1, 0),
            new Vec3(0, 0, 2), new Vec3(1, 0, 2),
        };
        var box = ObbBoxBuilder.BuildPatchBox(verts, new Vec3(0, 0, 1));
        Assert.NotNull(box);
        Assert.Equal(8, box!.VertexCount);
        Assert.Equal(6, box.FaceCount);
    }

    private static string FindGeoDumpDir()
    {
        var dir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "geo_dump"));
        if (Directory.Exists(dir))
            return dir;
        dir = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "..", "..", "geo_dump"));
        return dir;
    }
}
