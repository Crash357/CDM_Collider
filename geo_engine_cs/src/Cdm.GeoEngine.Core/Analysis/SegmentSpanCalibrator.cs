using Cdm.GeoEngine.Core.IO;

namespace Cdm.GeoEngine.Core.Analysis;

/// <summary>
/// Blind wall-segment length from footprint + target component count (corpus metadata, not OBB cheat).
/// Reference strips: sheds ~1.2–2.0 m tangent, barracks ~1.5–3.5 m.
/// </summary>
public static class SegmentSpanCalibrator
{
    /// <summary>Perimeter strips per target component (walls + slabs + roof).</summary>
    private const double TargetCountSpanDivisor = 0.55;

    public static double EstimateWallMaxSpanM(BuildingMeshProfile profile, int targetComponentCount = 0)
    {
        var sx = profile.SizeM.X;
        var sy = profile.SizeM.Y;
        var longH = System.Math.Max(sx, sy);
        var shortH = System.Math.Min(sx, sy);
        var perimeter = 2.0 * (sx + sy);

        if (targetComponentCount > 0)
        {
            var fromCount = perimeter / (targetComponentCount * TargetCountSpanDivisor);
            var minSpan = longH < 6.0 ? 1.25 : 1.45;
            var maxSpan = longH < 6.5 ? 2.05 : System.Math.Min(4.5, longH * 0.85);
            return System.Math.Clamp(fromCount, minSpan, maxSpan);
        }

        if (longH < 5.5)
            return System.Math.Clamp(longH / 2.4, 1.35, 2.0);
        if (longH < 8.0)
            return System.Math.Clamp(longH / 3.0, 2.0, 3.0);
        return System.Math.Clamp(longH / 3.5, 2.5, 4.5);
    }
}
