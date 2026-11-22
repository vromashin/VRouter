using System.Text.Json;

namespace VR.RoutePlanner.Infrastructure.Kafka;

/// <summary>Действие задачи WMS.</summary>
public sealed record WmsTaskAction(int ActionOrder, string Location, int ActionType);

/// <summary>Задача WMS из топика <c>simple-tasks</c>.</summary>
public sealed record WmsTask(string TaskId, string AreaWmsId, DateTimeOffset CreatedAt, IReadOnlyList<WmsTaskAction> Actions);

/// <summary>Разбор сообщений задач WMS.</summary>
public static class WmsTaskParser
{
    /// <summary>Тип действия: подбор груза.</summary>
    public const int PickUp = 0;

    /// <summary>Тип действия: выгрузка груза.</summary>
    public const int DropOff = 1;

    public static WmsTask? Parse(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;

            if (!root.TryGetProperty("taskId", out var taskId) || taskId.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var area = root.TryGetProperty("areaWmsId", out var areaElement) && areaElement.ValueKind == JsonValueKind.String
                ? areaElement.GetString() ?? "801"
                : "801";

            var actions = new List<WmsTaskAction>();
            if (root.TryGetProperty("actions", out var actionsElement) && actionsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in actionsElement.EnumerateArray())
                {
                    var order = item.TryGetProperty("actionOrder", out var orderElement) ? orderElement.GetInt32() : 0;
                    var location = item.TryGetProperty("location", out var locationElement)
                        ? locationElement.GetString() ?? string.Empty
                        : string.Empty;
                    var type = item.TryGetProperty("actionType", out var typeElement) ? typeElement.GetInt32() : 0;

                    if (!string.IsNullOrWhiteSpace(location))
                    {
                        actions.Add(new WmsTaskAction(order, location, type));
                    }
                }
            }

            if (actions.Count == 0)
            {
                return null;
            }

            var createdAt = root.TryGetProperty("createdAt", out var createdElement)
                && createdElement.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(createdElement.GetString(), out var parsed)
                    ? parsed
                    : DateTimeOffset.MinValue;

            actions.Sort((left, right) => left.ActionOrder.CompareTo(right.ActionOrder));
            return new WmsTask(taskId.GetString()!, area, createdAt, actions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
