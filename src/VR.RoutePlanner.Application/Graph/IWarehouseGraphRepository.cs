using VR.RoutePlanner.Domain.Graph;

namespace VR.RoutePlanner.Application.Graph;

/// <summary>
/// Чтение графа карты из хранилища с кэшированием в памяти.
/// </summary>
public interface IWarehouseGraphRepository
{
    /// <summary>
    /// Загружает граф карты. Повторный вызов возвращает данные из кэша.
    /// </summary>
    /// <param name="mapId">Внешний идентификатор карты, например <c>801</c>.</param>
    /// <param name="ct">Токен отмены.</param>
    /// <returns>Граф карты.</returns>
    /// <exception cref="InvalidOperationException">Для карты нет активных записей.</exception>
    Task<WarehouseGraph> LoadAsync(string mapId, CancellationToken ct);

    /// <summary>
    /// Возвращает версию графа: <c>MapVersion</c> плюс хеш состава узлов и ребер.
    /// </summary>
    /// <param name="mapId">Внешний идентификатор карты.</param>
    /// <param name="ct">Токен отмены.</param>
    /// <returns>Строка версии графа.</returns>
    Task<string> GetGraphVersionAsync(string mapId, CancellationToken ct);

    /// <summary>
    /// Сбрасывает кэш графа для указанной карты.
    /// </summary>
    /// <param name="mapId">Внешний идентификатор карты.</param>
    /// <param name="ct">Токен отмены.</param>
    Task InvalidateAsync(string mapId, CancellationToken ct);
}
