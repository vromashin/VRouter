using VR.RoutePlanner.Domain.Graph;

namespace VR.RoutePlanner.Application.Startup;

/// <summary>
/// Строка <c>vrt_MapVersion</c>: версия карты и графа, к которой привязаны ширины ребер,
/// ресурсы, связи и конфликты.
/// </summary>
public interface IMapVersionRepository
{
    /// <summary>
    /// Возвращает идентификатор строки <c>vrt_MapVersion</c> для пары карта + версия графа,
    /// создавая ее при первом обращении.
    /// </summary>
    /// <param name="mapId">Внешний идентификатор карты, например <c>801</c>.</param>
    /// <param name="graph">Загруженный граф карты.</param>
    /// <param name="ct">Токен отмены.</param>
    /// <returns>Идентификатор строки <c>vrt_MapVersion</c>.</returns>
    Task<Guid> GetOrCreateAsync(string mapId, WarehouseGraph graph, CancellationToken ct);
}
