using System.Globalization;
using System.Text.Json;
using VR.RoutePlanner.Application.Robots;

namespace VR.RoutePlanner.Infrastructure.Mqtt;

/// <summary>
/// Разбор сообщений <c>state</c> VDA 5050 версий 2.0.0 и 2.1.0.
/// Битый JSON не бросает исключение: результат равен <c>null</c>, причина возвращается через
/// параметр <c>error</c>.
/// </summary>
public static class RobotStateParser
{
    private static readonly string[] SupportedVersions = ["2.0", "2.1"];

    /// <summary>
    /// Разбирает полезную нагрузку сообщения.
    /// </summary>
    /// <param name="payload">Тело сообщения в UTF-8.</param>
    /// <param name="snapshot">Разобранное состояние либо <c>null</c>.</param>
    /// <param name="error">Причина отказа либо <c>null</c>.</param>
    /// <returns><c>true</c>, если состояние разобрано.</returns>
    public static bool TryParse(string? payload, out RobotSnapshot? snapshot, out string? error)
    {
        snapshot = null;
        error = null;

        if (string.IsNullOrWhiteSpace(payload))
        {
            error = "Пустое сообщение state.";
            return false;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(payload);
        }
        catch (JsonException exception)
        {
            error = $"Битый JSON в сообщении state: {exception.Message}";
            return false;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "Сообщение state не является JSON-объектом.";
                return false;
            }

            var version = ReadString(root, "version") ?? string.Empty;
            if (!IsSupported(version))
            {
                error = $"Версия протокола {version} не поддерживается: нужны 2.0.0 или 2.1.0.";
                return false;
            }

            var serialNumber = ReadString(root, "serialNumber");
            if (string.IsNullOrWhiteSpace(serialNumber))
            {
                error = "В сообщении state нет serialNumber.";
                return false;
            }

            var position = ReadObject(root, "agvPosition");
            var battery = ReadObject(root, "batteryState");

            snapshot = new RobotSnapshot(
                serialNumber!,
                "ONLINE",
                ReadBool(root, "driving"),
                ReadString(root, "orderId"),
                ReadInt(root, "orderUpdateId"),
                ReadDouble(position, "x"),
                ReadDouble(position, "y"),
                ReadDouble(position, "theta"),
                ReadString(position, "mapId"),
                ReadDouble(battery, "batteryCharge"),
                ReadErrors(root),
                ReadNodeStates(root),
                DateTimeOffset.UtcNow,
                version,
                ReadString(root, "lastNodeId"),
                ReadNullableInt(root, "lastNodeSequenceId"),
            ReadBool(root, "newBaseRequest"),
            ReadActionStates(root));

            return true;
        }
    }

    private static bool IsSupported(string version) =>
        SupportedVersions.Any(prefix => version.StartsWith(prefix, StringComparison.Ordinal));

    private static JsonElement? ReadObject(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object ? value : null;

    private static string? ReadString(JsonElement? element, string name)
    {
        if (element is null || !element.Value.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Null => null,
            _ => value.ToString(),
        };
    }

    private static double ReadDouble(JsonElement? element, string name)
    {
        if (element is null || !element.Value.TryGetProperty(name, out var value))
        {
            return 0;
        }

        return value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) ? number : 0;
    }

    private static int ReadInt(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number)
        {
            return 0;
        }

        return value.TryGetInt32(out var number) ? number : (int)Math.Round(value.GetDouble(), MidpointRounding.AwayFromZero);
    }

    private static int? ReadNullableInt(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        return value.TryGetInt32(out var number) ? number : null;
    }

    private static bool ReadBool(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static IReadOnlyList<RobotActionState> ReadActionStates(JsonElement root)
    {
        var result = new List<RobotActionState>();
        if (!root.TryGetProperty("actionStates", out var actions) || actions.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var item in actions.EnumerateArray())
        {
            var id = ReadString(item, "actionId");
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            result.Add(new RobotActionState(id!, ReadString(item, "actionType"), ReadString(item, "actionStatus")));
        }

        return result;
    }

    private static IReadOnlyList<string> ReadErrors(JsonElement root)
    {
        if (!root.TryGetProperty("errors", out var errors) || errors.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        var result = new List<string>();
        foreach (var item in errors.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                result.Add(item.GetString()!);
                continue;
            }

            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var description = ReadString(item, "errorDescription")
                ?? ReadString(item, "errorHint")
                ?? ReadString(item, "errorType");
            if (!string.IsNullOrWhiteSpace(description))
            {
                var level = ReadString(item, "errorLevel") ?? ReadString(item, "level");
                result.Add(string.IsNullOrWhiteSpace(level) ? description! : $"{level}|{description}");
            }
        }

        return result;
    }

    private static IReadOnlyList<RobotNodeState> ReadNodeStates(JsonElement root)
    {
        if (!root.TryGetProperty("nodeStates", out var nodes) || nodes.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<RobotNodeState>();
        }

        var result = new List<RobotNodeState>();
        foreach (var item in nodes.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var nodeId = ReadString(item, "nodeId");
            if (string.IsNullOrWhiteSpace(nodeId))
            {
                continue;
            }

            var sequence = item.TryGetProperty("sequenceId", out var sequenceValue)
                && sequenceValue.ValueKind == JsonValueKind.Number
                && sequenceValue.TryGetInt32(out var parsed)
                ? parsed
                : 0;

            var released = item.TryGetProperty("released", out var releasedValue)
                && releasedValue.ValueKind == JsonValueKind.True;

            result.Add(new RobotNodeState(nodeId!, sequence, released));
        }

        return result;
    }

    /// <summary>
    /// Строковое представление снимка для логов.
    /// </summary>
    /// <param name="snapshot">Снимок.</param>
    /// <returns>Краткое описание.</returns>
    public static string Describe(RobotSnapshot snapshot) => string.Create(
        CultureInfo.InvariantCulture,
        $"{snapshot.SerialNumber} v{snapshot.Version} order={snapshot.OrderId ?? "нет"} "
        + $"pos=({snapshot.PositionX:F2}, {snapshot.PositionY:F2}) battery={snapshot.BatteryCharge:F1}");
}
