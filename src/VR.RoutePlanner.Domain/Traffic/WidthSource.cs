namespace VR.RoutePlanner.Domain.Traffic;

/// <summary>
/// Источник значения ширины коридора.
/// </summary>
public enum WidthSource
{
    /// <summary>Ширина считается из растра препятствий через distance transform.</summary>
    Raster,

    /// <summary>Ширина берется из ручной настройки по метке <c>Edges.Corridor</c> или общим значением.</summary>
    Manual,

    /// <summary>Ширина равна <c>Routing:DefaultCorridorWidth</c>.</summary>
    Default,
}
