using Cdm.GeoEngine.Core.Analysis;
using Cdm.GeoEngine.Core.Primitives;

namespace Cdm.GeoEngine.Core.Pipeline;

public enum PatchSurfaceKind
{
    Wall,
    Horizontal,
    Plinth,
    Soffit,
    EndCap,
    Slope,
}

public static class PatchSurfaceClassifier
{
    private const double HorizontalDot = 0.85;
    private const double WallDot = 0.70;
    public const double WallMaxVerticalDot = 0.25;

    public static PatchSurfaceKind Classify(Vec3 normal, BuildingMeshProfile profile)
    {
        var n = normal.Normalized();
        var az = System.Math.Abs(n.Dot(profile.AxisZ.Normalized()));
        if (az >= HorizontalDot)
            return PatchSurfaceKind.Horizontal;

        var ax = System.Math.Abs(n.Dot(profile.AxisX.Normalized()));
        var ay = System.Math.Abs(n.Dot(profile.AxisY.Normalized()));
        if ((ax >= WallDot || ay >= WallDot) && az < WallMaxVerticalDot)
            return PatchSurfaceKind.Wall;

        return PatchSurfaceKind.Slope;
    }

    public static bool IsMergeProtected(PatchSurfaceKind kind) =>
        kind is PatchSurfaceKind.Plinth or PatchSurfaceKind.EndCap or PatchSurfaceKind.Soffit;

    public static bool CanMerge(PatchSurfaceKind a, PatchSurfaceKind b) =>
        !IsMergeProtected(a) && !IsMergeProtected(b) && a == b;

    public static bool CanMergeAntiparallel(PatchSurfaceKind a, PatchSurfaceKind b) =>
        a == PatchSurfaceKind.Wall && b == PatchSurfaceKind.Wall;
}
