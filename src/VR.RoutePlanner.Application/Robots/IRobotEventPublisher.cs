namespace VR.RoutePlanner.Application.Robots;

/// <summary>
/// Событие <c>agv-charging-completed</c>: робот пересек порог заряда на станции (Д-7).
/// </summary>
/// <param name="EventId">Собственный идентификатор события: <c>Guid.NewGuid().ToString("N")</c>.</param>
/// <param name="AgvSerialNumber">Серийный номер робота.</param>
/// <param name="NewBatteryLevel">Достигнутый заряд, проценты.</param>
/// <param name="Threshold">Порог <c>Routing:Charging:TargetLimit</c>, проценты.</param>
/// <param name="Status">Статус события, как в эталоне: <c>OK</c>.</param>
/// <param name="CompletedAt">Момент публикации события.</param>
public sealed record RobotChargingCompletedEvent(
    string EventId,
    string AgvSerialNumber,
    double NewBatteryLevel,
    double Threshold,
    string Status,
    DateTimeOffset CompletedAt);

/// <summary>
/// Публикация событий роботов в Kafka. Ошибка публикации не должна ронять тик сервиса.
/// </summary>
public interface IRobotEventPublisher
{
    /// <summary>
    /// Публикует событие завершения заряда. Ключ сообщения — серийный номер робота.
    /// </summary>
    /// <param name="payload">Тело события.</param>
    /// <param name="topic">Топик из <c>Routing:Charging:ChargingCompletedTopic</c>.</param>
    /// <param name="ct">Токен отмены.</param>
    Task PublishChargingCompletedAsync(
        RobotChargingCompletedEvent payload,
        string topic,
        CancellationToken ct);
}
