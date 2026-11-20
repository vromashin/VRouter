namespace VR.RoutePlanner.Application.Startup;

/// <summary>
/// Межпроцессная блокировка прогрева карты. Api и Workers живут в разных процессах,
/// поэтому мьютекс внутри одного процесса не годится: блокировка живет в базе
/// и видна всем экземплярам сервиса.
/// </summary>
public interface IWarmupLock
{
    /// <summary>
    /// Берет блокировку прогрева карты, при необходимости дожидаясь ее освобождения.
    /// </summary>
    /// <param name="mapId">Внешний идентификатор карты, например <c>801</c>.</param>
    /// <param name="ct">Токен отмены ожидания.</param>
    /// <returns>Владение блокировкой: освобождается при завершении <c>await using</c>.</returns>
    Task<IAsyncDisposable> AcquireAsync(string mapId, CancellationToken ct);
}
