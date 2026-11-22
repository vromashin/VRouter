using System.Text.Json;
using VR.RoutePlanner.Domain.Graph;
using VR.RoutePlanner.Domain.Planning;

namespace VR.RoutePlanner.Application.Robots;

/// <summary>
/// Сборка тела VDA-заказа. Реализация живет в Infrastructure: Application не знает
/// про формат MQTT-сообщений.
/// </summary>
public interface IVdaOrderComposer
{
    /// <summary>
    /// Собирает заказ.
    /// </summary>
    /// <param name="route">Маршрут планировщика.</param>
    /// <param name="graph">Граф карты.</param>
    /// <param name="mapId">Карта.</param>
    /// <param name="orderId">Идентификатор заказа вида <c>ORD_04330</c>.</param>
    /// <param name="manufacturer">Производитель робота.</param>
    /// <param name="serialNumber">Серийный номер робота.</param>
    /// <param name="orderNumber">Числовая часть номера заказа.</param>
    /// <param name="headerId">Идентификатор сообщения.</param>
    /// <param name="releasedNodeCount">Сколько первых узлов входит в базовый план.</param>
    /// <param name="timestamp">Момент сборки.</param>
    /// <param name="firstNodeX">Координата X первого узла, если нужна не из графа.</param>
    /// <param name="firstNodeY">Координата Y первого узла.</param>
    /// <param name="firstNodeTheta">Ориентация первого узла, радианы.</param>
    /// <returns>JSON заказа.</returns>
    string Compose(
        PlannedRoute route,
        WarehouseGraph graph,
        string mapId,
        string orderId,
        string manufacturer,
        string serialNumber,
        long orderNumber,
        long headerId,
        int releasedNodeCount,
        DateTimeOffset timestamp,
        double? firstNodeX = null,
        double? firstNodeY = null,
        double? firstNodeTheta = null);
}

/// <summary>
/// Тело мгновенного действия VDA 5050.
/// </summary>
public static class VdaInstantActionPayload
{
    /// <summary>
    /// Собирает сообщение <c>instantActions</c> с одним действием.
    /// </summary>
    /// <param name="actionType">Тип действия: <c>pause</c>, <c>resume</c>, <c>cancelOrder</c>, <c>startCharging</c>, <c>stopCharging</c>.</param>
    /// <param name="actionId">Идентификатор действия.</param>
    /// <param name="timestamp">Момент отправки.</param>
    /// <returns>JSON сообщения.</returns>
    public static string Build(string actionType, string actionId, DateTimeOffset timestamp)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actionType);
        ArgumentException.ThrowIfNullOrWhiteSpace(actionId);

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("headerId", timestamp.UtcTicks);
            writer.WriteString("timestamp", timestamp.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture));
            writer.WriteString("version", "2.1.0");
            writer.WriteStartArray("actions");
            writer.WriteStartObject();
            writer.WriteString("actionType", actionType);
            writer.WriteString("actionId", actionId);
            writer.WriteString("blockingType", "HARD");
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }
}
