namespace VR.RoutePlanner.Application.Graph;

/// <summary>
/// Получение растра инфлированной карты препятствий.
/// </summary>
public interface IMapRasterProvider
{
    /// <summary>
    /// Возвращает растр карты. Растр и поле расстояний кэшируются в памяти.
    /// </summary>
    /// <param name="mapId">Внешний идентификатор карты, например <c>801</c>.</param>
    /// <param name="ct">Токен отмены.</param>
    /// <returns>Растр карты.</returns>
    /// <exception cref="InvalidOperationException">Файл растра недоступен или не соответствует карте.</exception>
    Task<MapRaster> GetAsync(string mapId, CancellationToken ct);
}
