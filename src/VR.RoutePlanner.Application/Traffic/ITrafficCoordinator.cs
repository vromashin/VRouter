using VR.RoutePlanner.Application.Robots;

namespace VR.RoutePlanner.Application.Traffic;

/// <summary>
/// Диспетчеризация трафика: резервирование горизонта за роботом, освобождение проеханного
/// и свип просроченных резервов. Второго автомата статусов не создает: вызывается из
/// <see cref="RobotOrderService"/> после обработки <c>state</c>.
/// </summary>
public interface ITrafficCoordinator
{
    /// <summary>
    /// Проверяет и захватывает весь горизонт заказа перед публикацией.
    /// </summary>
    /// <param name="snapshot">Последнее состояние робота.</param>
    /// <param name="planJson">Полный план заказа.</param>
    /// <param name="ct">Токен отмены.</param>
    /// <returns>Принятый итог либо отказ с причиной.</returns>
    Task<TrafficReservationResult> TryReserveForOrderAsync(
        RobotSnapshot snapshot,
        string? planJson,
        CancellationToken ct);

    /// <summary>
    /// Продлевает горизонт по новому <c>state</c>: пройденное уходит в грейс, недостающее
    /// добирается best-effort.
    /// </summary>
    /// <param name="snapshot">Снимок робота.</param>
    /// <param name="planJson">Сохраненный план заказа.</param>
    /// <param name="ct">Токен отмены.</param>
    /// <returns>Итог: отказ не рвет заказ, а только логируется.</returns>
    Task<TrafficReservationResult> OnStateAsync(
        RobotSnapshot snapshot,
        string? planJson,
        CancellationToken ct);

    /// <summary>
    /// Немедленно снимает все резервы робота: терминальный статус, провал по таймауту,
    /// очищенный роботом <c>orderId</c>.
    /// </summary>
    /// <param name="agvSerialNumber">Серийный номер робота.</param>
    /// <param name="ct">Токен отмены.</param>
    /// <returns>Сколько резервов снято.</returns>
    Task<int> ReleaseAsync(string agvSerialNumber, CancellationToken ct);

    /// <summary>
    /// Снимает просроченные резервы всех роботов.
    /// </summary>
    /// <param name="now">Текущий момент.</param>
    /// <param name="ct">Токен отмены.</param>
    /// <returns>Сколько резервов снято.</returns>
    Task<int> SweepAsync(DateTimeOffset now, CancellationToken ct);
}
