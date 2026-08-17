namespace Cdm.GeoEngine.Core.Analysis;

/// <summary>
/// Heuristic search grid for wall segment span scale (&lt;1 shorter strips / more components / better geo;
/// &gt;1 longer strips / fewer components). Used by adaptive search — not a fixed constant.
/// </summary>
public static class SegmentSpanHeuristic
{
    public const double MinSpanFactor = 0.82;
    public const double MaxSpanFactor = 1.15;

    public static IReadOnlyList<double> CoarseSearchSpanFactors(
        BuildingMeshProfile profile,
        int targetComponentCount)
    {
        var longH = System.Math.Max(profile.SizeM.X, profile.SizeM.Y);
        if (longH > 10.0 || targetComponentCount > 40)
            return new[] { 0.94, 1.0, 1.06 };

        return new[] { 0.84, 0.88, 0.92, 0.96, 1.0, 1.05, 1.10 };
    }

    public static IEnumerable<double> RefineSpanFactors(double center)
    {
        if (center <= 0)
            center = 1.0;

        foreach (var t in new[]
                 {
                     center * 0.94,
                     center * 0.97,
                     center,
                     center * 1.03,
                     center * 1.06,
                 })
            yield return ClampSpanFactor(t);
    }

    public static double ClampSpanFactor(double factor) =>
        System.Math.Clamp(factor, MinSpanFactor, MaxSpanFactor);
}
