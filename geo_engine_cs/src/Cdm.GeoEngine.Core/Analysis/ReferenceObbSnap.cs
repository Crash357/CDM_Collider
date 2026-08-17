using Cdm.GeoEngine.Core.DayZ;
using Cdm.GeoEngine.Core.Models;
using Cdm.GeoEngine.Core.Primitives;

namespace Cdm.GeoEngine.Core.Analysis;

/// <summary>Build mesh components directly from a reference OrientedBox.</summary>
public static class ReferenceObbSnap
{
    public static MeshData BuildMesh(OrientedBox obb)
    {
        var mesh = new MeshData();
        if (obb.Corners is { Count: 8 })
        {
            foreach (var c in obb.Corners)
                mesh.Vertices.Add(c);
        }
        else
        {
            foreach (var c in BuildCorners(obb))
                mesh.Vertices.Add(c);
        }

        foreach (var face in GeometryLodConstants.BoxFaces)
            mesh.Faces.Add(face.ToArray());
        return mesh;
    }

    public static IReadOnlyList<Vec3> BuildCorners(OrientedBox obb)
    {
        var n = obb.AxisN;
        var u = obb.AxisU;
        var v = obb.AxisV;
        var specs = new (double sn, double su, double sv)[]
        {
            (-1, -1, -1), (-1, -1, 1), (-1, 1, -1), (-1, 1, 1),
            (1, -1, -1), (1, -1, 1), (1, 1, -1), (1, 1, 1),
        };
        return specs.Select(t =>
            obb.Center
                .Add(n.Scale(t.sn * obb.ExtentN))
                .Add(u.Scale(t.su * obb.ExtentU))
                .Add(v.Scale(t.sv * obb.ExtentV))).ToList();
    }
}
