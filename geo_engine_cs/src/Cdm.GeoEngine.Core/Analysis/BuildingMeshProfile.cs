using Cdm.GeoEngine.Core.Primitives;

namespace Cdm.GeoEngine.Core.Analysis;

/// <summary>Structural readout from a Resolution/building mesh before OBB generation.</summary>
public sealed class BuildingMeshProfile
{
    public Vec3 Centroid { get; init; }
    public Vec3 BoundsMin { get; init; }
    public Vec3 BoundsMax { get; init; }
    public Vec3 SizeM { get; init; }
    public double FootprintAreaM2 { get; init; }
    public double HeightM { get; init; }

    /// <summary>Primary horizontal building axis (longest footprint direction).</summary>
    public Vec3 AxisX { get; init; }

    /// <summary>Secondary horizontal axis (perpendicular to AxisX).</summary>
    public Vec3 AxisY { get; init; }

    public Vec3 AxisZ { get; init; } = new(0, 0, 1);

    public int VertexCount { get; init; }
    public int FaceCount { get; init; }

    public double WallThicknessM { get; init; } = 0.15;
    public double HorizontalSlabM { get; init; } = 0.12;

    public BuildingMeshProfile WithExtents(double wallThicknessM, double horizontalSlabM) =>
        new()
        {
            Centroid = Centroid,
            BoundsMin = BoundsMin,
            BoundsMax = BoundsMax,
            SizeM = SizeM,
            FootprintAreaM2 = FootprintAreaM2,
            HeightM = HeightM,
            AxisX = AxisX,
            AxisY = AxisY,
            AxisZ = AxisZ,
            VertexCount = VertexCount,
            FaceCount = FaceCount,
            WallThicknessM = wallThicknessM,
            HorizontalSlabM = horizontalSlabM,
        };
}

public sealed class OrientedBox
{
    public Vec3 Center { get; init; }
    public Vec3 AxisN { get; init; }
    public Vec3 AxisU { get; init; }
    public Vec3 AxisV { get; init; }
    public double ExtentN { get; init; }
    public double ExtentU { get; init; }
    public double ExtentV { get; init; }
    public IReadOnlyList<Vec3> Corners { get; init; } = Array.Empty<Vec3>();
}
