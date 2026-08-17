namespace Cdm.GeoEngine.Core.Models;

/// <summary>Semantic building region — one sparse user pick per kind, engine expands faces.</summary>
public enum GeoRegionKind
{
    WallOuter,
    WallInner,
    Floor,
    Roof,
    Gable,
    Plinth,
    Soffit,
}
