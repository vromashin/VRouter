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
    /// <summary>Идентификатор строки <c>vrt_MapVersion</c>.</summary>
    /// <returns>Идентификатор строки <c>vrt_MapVersion</c>.</returns>
    Task<Guid> GetOrCreateAsync(string mapId, WarehouseGraph graph, CancellationToken ct);

    /// <summary>
    /// Ищет существующую версию карты без создания строки: планировщик в базу не пишет.
    /// </summary>
    /// <param name="mapId">Внешний идентификатор карты.</param>
    /// <param name="graphVersion">Версия графа из <see cref="WarehouseGraph.GraphVersion"/>.</param>
    /// <param name="ct">Токен отмены.</param>
    /// <returns>Идентификатор версии либо <c>null</c>, если прогрев еще не создал ее.</returns>
    Task<Guid?> TryGetAsync(string mapId, string graphVersion, CancellationToken ct);
}
