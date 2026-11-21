using System.Text.Json;
using VR.RoutePlanner.Application.Robots;

namespace VR.RoutePlanner.Infrastructure.Mqtt;

/// <summary>
/// Разбор состояния заказа из сообщения <c>state</c>: те же версии, что у
/// <see cref="RobotStateParser"/>, но берется только то, что нужно для контроля выполнения.
/// </summary>
public static class RobotOrderStatusParser
{
    /// <summary>
    /// Разбирает сообщение <c>state</c>.
    /// </summary>
    /// <param name="payload">Тело сообщения в UTF-8.</param>
    /// <param name="status">Состояние заказа либо <c>null</c>.</param>
    /// <param name="error">Причина отказа либо <c>null</c>.</param>
    /// <returns><c>true</c>, если состояние разобрано.</returns>
    public static bool TryParse(string? payload, out RobotOrderStatus? status, out string? error)
    {
        status = null;
        error = null;

        if (!RobotStateParser.TryParse(payload, out var snapshot, out error) || snapshot is null)
        {
            return false;
        }

        status = new RobotOrderStatus(
            snapshot.OrderId,
            snapshot.OrderUpdateId,
            snapshot.Driving,
            snapshot.NodeStates,
            snapshot.Errors);

        return true;
    }

    /// <summary>
    /// Разбирает сообщение и отдает состояние заказа без снимка робота.
    /// </summary>
    /// <param name="payload">Тело сообщения в UTF-8.</param>
    /// <returns>Состояние заказа либо <c>null</c> для битого сообщения.</returns>
    public static RobotOrderStatus? ParseOrNull(string? payload) =>
        TryParse(payload, out var status, out _) ? status : null;

    /// <summary>
    /// Проверяет, что сообщение относится к заказу.
    /// </summary>
    /// <param name="payload">Тело сообщения в UTF-8.</param>
    /// <returns><c>true</c>, если в сообщении есть <c>orderId</c>.</returns>
    public static bool HasOrder(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(payload);
            return document.RootElement.TryGetProperty("orderId", out var orderId)
                && orderId.ValueKind == JsonValueKind.String;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
