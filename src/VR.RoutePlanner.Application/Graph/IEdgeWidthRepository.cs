using VR.RoutePlanner.Domain.Graph;

namespace VR.RoutePlanner.Application.Graph;

/// <summary>
/// Ширины ребер, посчитанные один раз на версию карты: чтение из <c>vrt_EdgeWidth</c>
/// и расчет при первом обращении.
/// </summary>
public interface IEdgeWidthRepository
{
    /// <summary>
    /// Возвращает ширины ребер версии карты, при отсутствии строк считает их по растру и сохраняет.
    /// </summary>
    /// <param name="mapVersionId">Идентификатор строки <c>vrt_MapVersion</c>.</param>
    /// <param name="graph">Загруженный граф карты.</param>
    /// <param name="mapId">Внешний идентификатор карты, для диагностики.</param>
    /// <param name="ct">Токен отмены.</param>
    /// <returns>Ширины в порядке <c>EdgeId</c>.</returns>
    Task<IReadOnlyList<EdgeWidth>> GetOrCreateAsync(
        Guid mapVersionId,
        WarehouseGraph graph,
        string mapId,
        CancellationToken ct);
}
