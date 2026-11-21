namespace VR.RoutePlanner.Application.Robots;

/// <summary>
/// Источник номера заказа роботу: номер входит в <c>orderId</c> вида <c>ORD_04330</c>.
/// </summary>
public interface IRobotOrderNumberSource
{
    /// <summary>
    /// Отдает следующий номер заказа.
    /// </summary>
    /// <param name="ct">Токен отмены.</param>
    /// <returns>Номер заказа, начиная с 1.</returns>
    Task<long> NextAsync(CancellationToken ct);
}

/// <summary>
/// Публикация заказа роботу. Реализация решает транспорт; на шаге B2 публикует только тест.
/// </summary>
public interface IRobotOrderPublisher
{
    /// <summary>
    /// Публикует заказ в топик <c>order</c> робота.
    /// </summary>
    /// <param name="manufacturer">Производитель из сообщений робота.</param>
    /// <param name="serialNumber">Серийный номер робота.</param>
    /// <param name="orderJson">Тело заказа.</param>
    /// <param name="ct">Токен отмены.</param>
    Task PublishAsync(string manufacturer, string serialNumber, string orderJson, CancellationToken ct);

    /// <summary>
    /// Публикует мгновенное действие в топик <c>instantActions</c> робота.
    /// </summary>
    /// <param name="manufacturer">Производитель.</param>
    /// <param name="serialNumber">Серийный номер робота.</param>
    /// <param name="payload">Тело сообщения.</param>
    /// <param name="ct">Токен отмены.</param>
    Task PublishInstantActionAsync(
        string manufacturer,
        string serialNumber,
        string payload,
        CancellationToken ct);
}

/// <summary>
/// Транспорт публикации: отдает сообщение в MQTT-топик.
/// </summary>
public interface IRobotOrderTransport
{
    /// <summary>
    /// Публикует сообщение.
    /// </summary>
    /// <param name="topic">Топик.</param>
    /// <param name="payload">Тело в UTF-8.</param>
    /// <param name="ct">Токен отмены.</param>
    Task PublishAsync(string topic, string payload, CancellationToken ct);
}

/// <summary>
/// Состояние заказа из сообщения <c>state</c>.
/// </summary>
/// <param name="OrderId">Идентификатор заказа либо <c>null</c>.</param>
/// <param name="OrderUpdateId">Версия заказа.</param>
/// <param name="Driving">Робот в движении.</param>
/// <param name="NodeStates">Состояния узлов.</param>
/// <param name="Errors">Ошибки в текстовом виде.</param>
public sealed record RobotOrderStatus(
    string? OrderId,
    int OrderUpdateId,
    bool Driving,
    IReadOnlyList<RobotNodeState> NodeStates,
    IReadOnlyList<string> Errors);
