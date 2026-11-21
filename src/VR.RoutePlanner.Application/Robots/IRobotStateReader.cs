namespace VR.RoutePlanner.Application.Robots;

/// <summary>
/// Чтение последнего состояния роботов. Реализация может смотреть в память процесса
/// и в таблицу <c>vrt_RobotSession</c>, чтобы API и Workers видели одно состояние.
/// </summary>
public interface IRobotStateReader
{
    /// <summary>
    /// Отдает последнее состояние робота.
    /// </summary>
    /// <param name="serialNumber">Серийный номер робота.</param>
    /// <param name="ct">Токен отмены.</param>
    /// <returns>Снимок состояния либо <c>null</c>, если свежих данных нет.</returns>
    Task<RobotSnapshot?> GetLatestAsync(string serialNumber, CancellationToken ct);

    /// <summary>
    /// Отдает последние состояния всех роботов.
    /// </summary>
    /// <param name="ct">Токен отмены.</param>
    /// <returns>Снимки по серийному номеру.</returns>
    Task<IReadOnlyCollection<RobotSnapshot>> GetAllAsync(CancellationToken ct);
}
