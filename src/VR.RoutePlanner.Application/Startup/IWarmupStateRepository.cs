namespace VR.RoutePlanner.Application.Startup;

/// <summary>
/// Хранилище последнего итога прогрева. Общее для Api и Workers: результат, записанный
/// процессом-исполнителем, читается из любого процесса сервиса.
/// </summary>
public interface IWarmupStateRepository
{
    /// <summary>
    /// Читает итог последнего прогрева карты.
    /// </summary>
    /// <param name="mapId">Внешний идентификатор карты, например <c>801</c>.</param>
    /// <param name="ct">Токен отмены.</param>
    /// <returns>Итог прогрева либо <c>null</c>, если прогрева еще не было.</returns>
    Task<WarmupResult?> GetAsync(string mapId, CancellationToken ct);

    /// <summary>
    /// Записывает итог прогрева, заменяя предыдущий итог этой карты.
    /// </summary>
    /// <param name="result">Итог прогрева.</param>
    /// <param name="ct">Токен отмены.</param>
    /// <returns>Задача записи.</returns>
    Task SaveAsync(WarmupResult result, CancellationToken ct);
}
