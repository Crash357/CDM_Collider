using Cdm.GeoEngine.Core.DayZ;
using Cdm.GeoEngine.Core.Models;
using Cdm.GeoEngine.Core.Primitives;
using Cdm.GeoEngine.Core.Validation;

namespace Cdm.GeoEngine.Tests;

public class ReferenceObbExtractorTests
{
    [Fact]
    public void ExtractFromGeometryLod_UsesMeshIslandsWhenVertexGroupsEmpty()
    {
        var mesh = BuildBoxMesh(new Vec3(0, 0, 0), new Vec3(2, 0.15, 1), new Vec3(1, 0, 0));
        mesh.Name = "Geometry";
        mesh.VertexGroups["component01"] = new List<int>();

        var obbs = ReferenceObbExtractor.ExtractFromGeometryLod(mesh);
        Assert.Single(obbs);
        var maxExtent = Math.Max(obbs[0].ExtentU, Math.Max(obbs[0].ExtentV, obbs[0].ExtentN));
        Assert.True(maxExtent > 0.9);
    }

    [Fact]
    public void ExtractFromGeometryLod_MultipleIslands_ReturnsOneObbPerBox()
    {
        var mesh = new MeshData { Name = "Geometry" };
        AppendBox(mesh, new Vec3(0, 0, 0), new Vec3(2, 0.15, 1), new Vec3(1, 0, 0));
        AppendBox(mesh, new Vec3(3, 0, 0), new Vec3(0.15, 2, 1), new Vec3(0, 1, 0));

        var obbs = ReferenceObbExtractor.ExtractFromGeometryLod(mesh);
        Assert.Equal(2, obbs.Count);
    }

    private static MeshData BuildBoxMesh(Vec3 origin, Vec3 u, Vec3 n)
    {
        var mesh = new MeshData();
        AppendBox(mesh, origin, u, n);
        return mesh;
    }

    private static void AppendBox(MeshData mesh, Vec3 origin, Vec3 u, Vec3 n)
    {
        var v = n.Cross(u).Normalized();
        var corners = new List<Vec3>();
        foreach (var sn in new[] { 0.0, 1.0 })
        foreach (var su in new[] { 0.0, 1.0 })
        foreach (var sv in new[] { 0.0, 1.0 })
            corners.Add(origin.Add(u.Scale(su)).Add(n.Scale(sn)).Add(v.Scale(sv)));

        var offset = mesh.Vertices.Count;
        mesh.Vertices.AddRange(corners);
        foreach (var face in GeometryLodConstants.BoxFaces)
            mesh.Faces.Add(face.Select(vi => offset + vi).ToArray());
    }
}
