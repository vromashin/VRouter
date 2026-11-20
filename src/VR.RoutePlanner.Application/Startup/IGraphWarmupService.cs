namespace VR.RoutePlanner.Application.Startup;

/// <summary>
/// Прогрев ресурсов карты: загрузка графа, версия карты, ширины ребер, построение
/// и сохранение ресурсов одним проходом.
/// </summary>
public interface IGraphWarmupService
{
    /// <summary>
    /// Отдает итог последнего успешного прогрева либо <c>null</c>, если прогрева еще не было.
    /// Результат читается из базы, поэтому виден и тому процессу, который прогрев не выполнял.
    /// </summary>
    /// <param name="mapId">Внешний идентификатор карты, например <c>801</c>.</param>
    /// <param name="ct">Токен отмены.</param>
    /// <returns>Итог прогрева либо <c>null</c>.</returns>
    Task<WarmupResult?> GetLastResultAsync(string mapId, CancellationToken ct);

    /// <summary>
    /// Выполняет прогрев карты. Параллельный вызов, в том числе из другого процесса сервиса,
    /// не запускает второй расчет: он ждет завершения текущего и получает тот же результат.
    /// </summary>
    /// <param name="mapId">Внешний идентификатор карты, например <c>801</c>.</param>
    /// <param name="ct">Токен отмены.</param>
    /// <returns>Итог прогрева.</returns>
    /// <exception cref="WarmupStepException">Один из шагов прогрева завершился ошибкой.</exception>
    Task<WarmupResult> WarmupAsync(string mapId, CancellationToken ct);
}
