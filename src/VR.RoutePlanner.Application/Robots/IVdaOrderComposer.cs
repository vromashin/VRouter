using System.Text.Json;

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
    /// <param name="request">Запрос сборки: маршрут, граф и границы куска.</param>
    /// <returns>JSON заказа.</returns>
    string Compose(VdaOrderRequest request);
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
    /// <param name="parameters">Параметры действия.</param>
    /// <returns>JSON сообщения.</returns>
    public static string Build(
        string actionType,
        string actionId,
        DateTimeOffset timestamp,
        IReadOnlyDictionary<string, object?>? parameters = null)
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
            writer.WriteString("blockingType", parameters is { Count: > 0 } ? "SOFT" : "HARD");
            if (parameters is { Count: > 0 })
            {
                writer.WriteStartArray("actionParameters");
                foreach (var pair in parameters)
                {
                    writer.WriteStartObject();
                    writer.WriteString("key", pair.Key);
                    writer.WritePropertyName("value");
                    switch (pair.Value)
                    {
                        case bool flag: writer.WriteBooleanValue(flag); break;
                        case int number: writer.WriteNumberValue(number); break;
                        case double real: writer.WriteNumberValue(real); break;
                        case null: writer.WriteNullValue(); break;
                        default: writer.WriteStringValue(pair.Value.ToString()); break;
                    }

                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
            }
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }
}
