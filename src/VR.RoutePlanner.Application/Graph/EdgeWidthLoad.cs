using VR.RoutePlanner.Domain.Graph;

namespace VR.RoutePlanner.Application.Graph;

/// <summary>
/// Результат чтения ширин ребер: сами ширины и признак того, что они взяты из
/// <c>vrt_EdgeWidth</c>, а не посчитаны построителем заново.
/// </summary>
/// <param name="Widths">Ширины всех активных ребер версии карты.</param>
/// <param name="Reused">Строки уже были в базе, растр не читался.</param>
public sealed record EdgeWidthLoad(IReadOnlyList<EdgeWidth> Widths, bool Reused);
