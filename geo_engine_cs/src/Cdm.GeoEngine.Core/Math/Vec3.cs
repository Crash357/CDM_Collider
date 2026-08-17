using System.Numerics;

namespace Cdm.GeoEngine.Core.Primitives;

public readonly record struct Vec3(double X, double Y, double Z)
{
    public static Vec3 From(Vector3 v) => new(v.X, v.Y, v.Z);

    public static Vec3 Centroid(IReadOnlyList<Vec3> points)
    {
        if (points.Count == 0)
            return new Vec3(0, 0, 0);
        var sum = new Vec3(0, 0, 0);
        foreach (var p in points)
            sum = sum.Add(p);
        return sum.Scale(1.0 / points.Count);
    }

    public Vector3 ToVector3() => new((float)X, (float)Y, (float)Z);

    public Vec3 Add(Vec3 o) => new(X + o.X, Y + o.Y, Z + o.Z);

    public Vec3 Sub(Vec3 o) => new(X - o.X, Y - o.Y, Z - o.Z);

    public Vec3 Scale(double s) => new(X * s, Y * s, Z * s);

    public double Dot(Vec3 o) => X * o.X + Y * o.Y + Z * o.Z;

    public Vec3 Cross(Vec3 o) => new(
        Y * o.Z - Z * o.Y,
        Z * o.X - X * o.Z,
        X * o.Y - Y * o.X);

    public double Length() => System.Math.Sqrt(Dot(this));

    public Vec3 Normalized()
    {
        var len = Length();
        return len < 1e-12 ? new Vec3(0, 0, 1) : Scale(1.0 / len);
    }

    public static double Distance(Vec3 a, Vec3 b) => a.Sub(b).Length();
}
