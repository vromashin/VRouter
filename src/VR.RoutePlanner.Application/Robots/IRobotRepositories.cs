using VR.RoutePlanner.Domain.Robots;

namespace VR.RoutePlanner.Application.Robots;

/// <summary>
/// Хранилище последнего состояния робота: только таблица <c>vrt_RobotSession</c>.
/// </summary>
public interface IRobotSessionRepository
{
    /// <summary>
    /// Сохраняет состояние робота, заменяя предыдущее.
    /// </summary>
    /// <param name="session">Состояние.</param>
    /// <param name="ct">Токен отмены.</param>
    Task SaveAsync(RobotSession session, CancellationToken ct);

    /// <summary>
    /// Читает состояние робота.
    /// </summary>
    /// <param name="agvSerialNumber">Серийный номер робота.</param>
    /// <param name="ct">Токен отмены.</param>
    /// <returns>Состояние либо <c>null</c>, если робот еще не писал.</returns>
    Task<RobotSession?> GetAsync(string agvSerialNumber, CancellationToken ct);
}

/// <summary>
/// Хранилище заказов роботам: только таблицы <c>vrt_RobotOrder</c> и <c>vrt_RobotOrderNode</c>.
/// </summary>
public interface IRobotOrderRepository
{
    /// <summary>
    /// Сохраняет заказ и его узлы, заменяя узлы прежней версии заказа.
    /// </summary>
    /// <param name="order">Заказ с узлами.</param>
    /// <param name="ct">Токен отмены.</param>
    Task SaveAsync(RobotOrder order, CancellationToken ct);

    /// <summary>
    /// Читает заказ по идентификатору вместе с узлами.
    /// </summary>
    /// <param name="orderId">Идентификатор заказа.</param>
    /// <param name="ct">Токен отмены.</param>
    /// <returns>Заказ либо <c>null</c>.</returns>
    Task<RobotOrder?> GetAsync(string orderId, CancellationToken ct);

    /// <summary>
    /// Читает последний заказ робота вместе с узлами.
    /// </summary>
    /// <param name="agvSerialNumber">Серийный номер робота.</param>
    /// <param name="ct">Токен отмены.</param>
    /// <returns>Заказ либо <c>null</c>.</returns>
    Task<RobotOrder?> GetLatestAsync(string agvSerialNumber, CancellationToken ct);

    /// <summary>
    /// Читает заказы в незавершенных статусах: <c>Draft</c>, <c>Sent</c>, <c>Accepted</c>, <c>Executing</c>.
    /// </summary>
    /// <param name="ct">Токен отмены.</param>
    /// <returns>Заказы без узлов либо пустой список.</returns>
    Task<IReadOnlyList<RobotOrder>> GetActiveAsync(CancellationToken ct);

    /// <summary>
    /// Читает незавершенный заказ робота вместе с узлами. Нужен, когда робот очистил
    /// <c>orderId</c> в том же сообщении, где доехал до цели (риск Р-1).
    /// </summary>
    /// <param name="agvSerialNumber">Серийный номер робота.</param>
    /// <param name="ct">Токен отмены.</param>
    /// <returns>Активный заказ либо <c>null</c>.</returns>
    Task<RobotOrder?> GetActiveForRobotAsync(string agvSerialNumber, CancellationToken ct);
}
