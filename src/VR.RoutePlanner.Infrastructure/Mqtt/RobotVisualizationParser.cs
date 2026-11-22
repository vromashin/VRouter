using System.Globalization;
using System.Text.Json;
using VR.RoutePlanner.Application.Robots;

namespace VR.RoutePlanner.Infrastructure.Mqtt;

/// <summary>
/// Разбор сообщений <c>visualization</c> VDA 5050: позиция, батарея, движение,
/// защитное поле, вилы и скорость.
/// </summary>
public static class RobotVisualizationParser
{
    /// <summary>
    /// Разбирает тело сообщения.
    /// </summary>
    /// <param name="payload">Тело в UTF-8.</param>
    /// <param name="visualization">Результат либо <c>null</c>.</param>
    /// <param name="error">Причина отказа либо <c>null</c>.</param>
    /// <returns><c>true</c>, если сообщение разобрано.</returns>
    public static bool TryParse(string? payload, out RobotVisualization? visualization, out string? error)
    {
        visualization = null;
        error = null;

        if (string.IsNullOrWhiteSpace(payload))
        {
            error = "Пустое сообщение visualization.";
            return false;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(payload);
        }
        catch (JsonException exception)
        {
            error = $"Битый JSON в сообщении visualization: {exception.Message}";
            return false;
        }

        using (document)
        {
            var root = document.RootElement;
            var serialNumber = ReadString(root, "serialNumber");
            if (string.IsNullOrWhiteSpace(serialNumber))
            {
                error = "В сообщении visualization нет serialNumber.";
                return false;
            }

            var position = ReadObject(root, "agvPosition");
            var battery = ReadObject(root, "batteryState");
            var safety = ReadObject(root, "safetyState");
            var fork = ReadObject(root, "fork");
            var velocity = ReadObject(root, "velocity");

            var vx = ReadDouble(velocity, "vx");
            var vy = ReadDouble(velocity, "vy");

            visualization = new RobotVisualization(
                serialNumber!,
                ReadString(root, "manufacturer"),
                ReadString(root, "version") ?? string.Empty,
                ReadDouble(position, "x"),
                ReadDouble(position, "y"),
                ReadDouble(position, "theta"),
                ReadString(position, "mapId"),
                ReadDouble(battery, "batteryCharge"),
                ReadBool(root, "driving"),
                ReadBool(root, "paused"),
                ReadString(safety, "eStop"),
                ReadBool(safety, "fieldViolation"),
                fork is null ? null : ReadDouble(fork, "liftPos"),
                Math.Sqrt((vx * vx) + (vy * vy)),
                DateTimeOffset.UtcNow);

            return true;
        }
    }

    /// <summary>
    /// Краткое описание для журнала.
    /// </summary>
    /// <param name="visualization">Визуализация.</param>
    /// <returns>Текст.</returns>
    public static string Describe(RobotVisualization visualization) => string.Create(
        CultureInfo.InvariantCulture,
        $"{visualization.SerialNumber} pos=({visualization.PositionX:F2}, {visualization.PositionY:F2}) "
        + $"theta={visualization.Theta:F3} v={visualization.Velocity:F2} driving={visualization.Driving}");

    private static JsonElement? ReadObject(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object ? value : null;

    private static string? ReadString(JsonElement? element, string name)
    {
        if (element is null || !element.Value.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static double ReadDouble(JsonElement? element, string name)
    {
        if (element is null || !element.Value.TryGetProperty(name, out var value))
        {
            return 0;
        }

        return value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) ? number : 0;
    }

    private static bool ReadBool(JsonElement? element, string name) =>
        element is not null
        && element.Value.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.True;
}
