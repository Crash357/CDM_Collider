using Cdm.GeoEngine.Core.Models;
using Cdm.GeoEngine.Core.Pipeline;
using Cdm.GeoEngine.Core.Primitives;

namespace Cdm.GeoEngine.Core.Analysis;

public sealed record PatchDiagnosticRecord(
    int Index,
    int FaceCount,
    double AreaM2,
    double TangentSpanM,
    double SecondarySpanM,
    double ThicknessM,
    double[] CenterM,
    double[] DominantNormal,
    string Kind);

/// <summary>Per-patch metrics for segmentation tuning (tangent span, thickness, center).</summary>
public static class PatchDiagnostics
{
    public static IReadOnlyList<PatchDiagnosticRecord> Analyze(
        MeshData mesh,
        IReadOnlyList<PatchCluster> patches,
        BuildingMeshProfile profile)
    {
        var list = new List<PatchDiagnosticRecord>(patches.Count);
        for (var i = 0; i < patches.Count; i++)
            list.Add(AnalyzeOne(mesh, patches[i], i, profile));
        return list;
    }

    public static PatchDiagnosticRecord AnalyzeOne(
        MeshData mesh,
        PatchCluster patch,
        int index,
        BuildingMeshProfile profile)
    {
        var verts = FaceBoundsObbFitter.CollectPatchFaceVertices(mesh, patch);
        var n = BuildingMeshAnalyzer.SnapNormalToBuildingAxes(patch.DominantNormal, profile);
        var u = BuildInPlaneU(n, profile);
        var v = n.Cross(u).Normalized();

        var uProj = verts.Select(p => p.Dot(u)).ToArray();
        var vProj = verts.Select(p => p.Dot(v)).ToArray();
        var nProj = verts.Select(p => p.Dot(n)).ToArray();

        var tangentSpan = uProj.Max() - uProj.Min();
        var secondarySpan = vProj.Max() - vProj.Min();
        var thickness = nProj.Max() - nProj.Min();

        if (tangentSpan < secondarySpan)
        {
            (tangentSpan, secondarySpan) = (secondarySpan, tangentSpan);
        }

        var center = new Vec3(
            verts.Average(p => p.X),
            verts.Average(p => p.Y),
            verts.Average(p => p.Z));

        return new PatchDiagnosticRecord(
            index,
            patch.FaceIndices.Count,
            patch.AreaM2,
            tangentSpan,
            secondarySpan,
            thickness,
            new[] { center.X, center.Y, center.Z },
            new[] { n.X, n.Y, n.Z },
            KindLabel(patch.SurfaceKind));
    }

    private static string KindLabel(PatchSurfaceKind kind) => kind switch
    {
        PatchSurfaceKind.Horizontal => "horizontal",
        PatchSurfaceKind.Plinth => "plinth",
        PatchSurfaceKind.Soffit => "soffit",
        PatchSurfaceKind.EndCap => "endcap",
        PatchSurfaceKind.Slope => "slope",
        _ => "wall",
    };

    private static string ClassifyKind(Vec3 n, BuildingMeshProfile profile)
    {
        var az = System.Math.Abs(n.Dot(profile.AxisZ.Normalized()));
        if (az >= 0.85)
            return "horizontal";
        var ax = System.Math.Abs(n.Dot(profile.AxisX.Normalized()));
        var ay = System.Math.Abs(n.Dot(profile.AxisY.Normalized()));
        if (ax >= 0.70 || ay >= 0.70)
            return "wall";
        return "slope";
    }

    private static Vec3 BuildInPlaneU(Vec3 n, BuildingMeshProfile profile)
    {
        var nn = n.Normalized();
        var az = System.Math.Abs(nn.Dot(profile.AxisZ));
        var ax = System.Math.Abs(nn.Dot(profile.AxisX));
        var ay = System.Math.Abs(nn.Dot(profile.AxisY));

        if (az < 0.85 && (ax >= 0.70 || ay >= 0.70))
        {
            var height = profile.AxisZ.Sub(nn.Scale(profile.AxisZ.Dot(nn)));
            if (height.Length() > 1e-6)
                return height.Normalized();
        }

        Vec3 tangent;
        if (az >= 0.85)
            tangent = profile.AxisX;
        else if (ax >= ay)
            tangent = profile.AxisY;
        else
            tangent = profile.AxisX;

        return tangent.Sub(nn.Scale(tangent.Dot(nn))).Normalized();
    }
}
