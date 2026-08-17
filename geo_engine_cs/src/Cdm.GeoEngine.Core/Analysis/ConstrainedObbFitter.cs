using Cdm.GeoEngine.Core.Models;
using Cdm.GeoEngine.Core.Primitives;

namespace Cdm.GeoEngine.Core.Analysis;

/// <summary>Fit patch mesh while blending toward a reference OBB (axes + center + extents).</summary>
public static class ConstrainedObbFitter
{
    public static MeshData? BuildPatchMesh(
        IReadOnlyList<Vec3> worldVerts,
        Vec3 hintNormal,
        OrientedBox reference,
        BuildingMeshProfile? profile = null,
        double referenceWeight = 0.65)
    {
        var fitted = ObbFitter.FitPatch(worldVerts, hintNormal, profile);
        if (fitted == null)
            return ReferenceObbSnap.BuildMesh(reference);

        var blended = Blend(fitted, reference, referenceWeight);
        return ReferenceObbSnap.BuildMesh(blended);
    }

    public static OrientedBox Blend(OrientedBox fitted, OrientedBox reference, double referenceWeight)
    {
        var w = System.Math.Clamp(referenceWeight, 0, 1);
        var fw = 1.0 - w;

        var center = fitted.Center.Scale(fw).Add(reference.Center.Scale(w));
        var extentN = fitted.ExtentN * fw + reference.ExtentN * w;
        var extentU = fitted.ExtentU * fw + reference.ExtentU * w;
        var extentV = fitted.ExtentV * fw + reference.ExtentV * w;

        var obb = new OrientedBox
        {
            Center = center,
            AxisN = reference.AxisN,
            AxisU = reference.AxisU,
            AxisV = reference.AxisV,
            ExtentN = extentN,
            ExtentU = extentU,
            ExtentV = extentV,
            Corners = ReferenceObbSnap.BuildCorners(new OrientedBox
            {
                Center = center,
                AxisN = reference.AxisN,
                AxisU = reference.AxisU,
                AxisV = reference.AxisV,
                ExtentN = extentN,
                ExtentU = extentU,
                ExtentV = extentV,
            }),
        };
        return obb;
    }
}
