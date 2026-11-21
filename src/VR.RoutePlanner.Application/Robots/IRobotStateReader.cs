namespace VR.RoutePlanner.Application.Robots;

/// <summary>
/// Чтение последнего состояния роботов. Данные приходят из MQTT, база не используется.
/// </summary>
public interface IRobotStateReader
{
    /// <summary>
    /// Отдает последнее состояние робота.
    /// </summary>
    /// <param name="serialNumber">Серийный номер робота.</param>
    /// <returns>Снимок состояния либо <c>null</c>, если сообщений от робота еще не было.</returns>
    RobotSnapshot? GetLatest(string serialNumber);

    /// <summary>
    /// Отдает последние состояния всех роботов, от которых приходили сообщения.
    /// </summary>
    /// <returns>Снимки по серийному номеру.</returns>
    IReadOnlyCollection<RobotSnapshot> GetAll();
}
