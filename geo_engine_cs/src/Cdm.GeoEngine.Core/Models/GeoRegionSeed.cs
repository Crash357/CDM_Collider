using Cdm.GeoEngine.Core.Primitives;

namespace Cdm.GeoEngine.Core.Models;

/// <summary>Single picker hit: face index or world position + surface normal.</summary>
public sealed record GeoRegionSeed(
    GeoRegionKind Kind,
    int FaceIndex,
    Vec3 Position,
    Vec3 Normal);
