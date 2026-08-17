using Cdm.GeoEngine.Core.Analysis;
using Cdm.GeoEngine.Core.IO;
using Cdm.GeoEngine.Core.Models;
using Cdm.GeoEngine.Core.Pipeline;
using Cdm.GeoEngine.Core.Validation;

namespace Cdm.GeoEngine.Tests;

public class RegionGuidedDecomposerTests
{
    [Fact]
    public void RegionSeedExpander_AssignsFacesFromAutoSeeds()
    {
        var mesh = LoadResolution("sheds/shed_w1");
        if (mesh == null)
            return;

        var profile = BuildingMeshAnalyzer.Analyze(mesh);
        var seeds = RegionSeedAutoExtractor.Extract(mesh, profile);
        Assert.NotEmpty(seeds);

        var plan = RegionSeedExpander.BuildPlan(mesh, seeds, profile);
        Assert.True(plan.AllGuidedFaces.Count > mesh.Faces.Count / 4);
        Assert.True(plan.FacesByKind[GeoRegionKind.WallOuter].Count > 0);
    }

    [Fact]
    public void RegionGuided_GeneratesComponents_ForShedW1()
    {
        var mesh = LoadResolution("sheds/shed_w1");
        var geoPath = GeometryPath("sheds/shed_w1");
        if (mesh == null || geoPath == null)
            return;

        var profile = BuildingMeshAnalyzer.Analyze(mesh);
        var seeds = RegionSeedAutoExtractor.Extract(mesh, profile);
        var refGeo = JsonMeshLoader.LoadGeometryFromFile(geoPath);
        var target = refGeo.VertexGroups.Keys.Count(k =>
            k.StartsWith("component", StringComparison.OrdinalIgnoreCase));
        var wallSpan = SegmentSpanCalibrator.EstimateWallMaxSpanM(profile, target);

        var result = BuildingGeometryEngine.Generate(mesh, new BuildingGeometryOptions
        {
            Decomposition = BuildingDecompositionMode.RegionGuided,
            RegionSeeds = seeds,
            TargetComponentCount = target,
            WallSegmentMaxSpanM = wallSpan,
            RequireDoorVertices = false,
            Profile = profile,
            ResolutionSource = mesh,
        });

        Assert.InRange(result.Components.Count, target - 2, target + 2);
        var compare = GeometricComponentComparer.Compare(refGeo, result.Components);
        Assert.True(compare.MeanCenterDeltaM < 2.5, $"mean_ctr={compare.MeanCenterDeltaM:F2}m");
        var coverage = ResolutionCoverageScorer.ScoreFromComponents(mesh, result.Components, profile);
        Assert.True(coverage.FractionInside >= 0.85, $"coverage={coverage.FractionInside:P0}");
    }

    [Fact]
    public void RegionGuided_GeneratesComponents_ForShedM4()
    {
        var mesh = LoadResolution("sheds/shed_m4");
        var geoPath = GeometryPath("sheds/shed_m4");
        if (mesh == null || geoPath == null)
            return;

        var profile = BuildingMeshAnalyzer.Analyze(mesh);
        var seeds = RegionSeedAutoExtractor.Extract(mesh, profile);
        var refGeo = JsonMeshLoader.LoadGeometryFromFile(geoPath);
        var target = refGeo.VertexGroups.Keys.Count(k =>
            k.StartsWith("component", StringComparison.OrdinalIgnoreCase));
        var wallSpan = SegmentSpanCalibrator.EstimateWallMaxSpanM(profile, target);

        var result = BuildingGeometryEngine.Generate(mesh, new BuildingGeometryOptions
        {
            Decomposition = BuildingDecompositionMode.RegionGuided,
            RegionSeeds = seeds,
            TargetComponentCount = target,
            WallSegmentMaxSpanM = wallSpan,
            RequireDoorVertices = false,
            Profile = profile,
            ResolutionSource = mesh,
        });

        Assert.InRange(result.Components.Count, target - 1, target + 1);
        var coverage = ResolutionCoverageScorer.ScoreFromComponents(mesh, result.Components, profile);
        Assert.True(coverage.FractionInside >= 0.85, $"coverage={coverage.FractionInside:P0}");
    }

    private static MeshData? LoadResolution(string modelId)
    {
        var path = ResolutionPath(modelId);
        return path != null && File.Exists(path)
            ? JsonMeshLoader.LoadResolutionFromFile(path)
            : null;
    }

    private static string? ResolutionPath(string modelId)
    {
        var root = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", ".."));
        var candidates = new[]
        {
            Path.Combine(root, "p3d_files", "_corpus", "meshes", modelId.Replace('/', Path.DirectorySeparatorChar), "resolution_lod_1.json"),
            Path.Combine(root, "p3d_files", "residential", "_sandbox", "meshes", modelId.Replace('/', Path.DirectorySeparatorChar), "resolution_lod_1.json"),
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static string? GeometryPath(string modelId)
    {
        var root = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", ".."));
        var candidates = new[]
        {
            Path.Combine(root, "p3d_files", "_corpus", "meshes", modelId.Replace('/', Path.DirectorySeparatorChar), "geometry_lod.json"),
            Path.Combine(root, "p3d_files", "residential", "_sandbox", "meshes", modelId.Replace('/', Path.DirectorySeparatorChar), "geometry_lod.json"),
        };
        return candidates.FirstOrDefault(File.Exists);
    }
}
