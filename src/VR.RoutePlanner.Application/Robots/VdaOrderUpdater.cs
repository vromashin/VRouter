using System.Text.Json.Nodes;

namespace VR.RoutePlanner.Application.Robots;

/// <summary>
/// Обновление заказа VDA 5050: из прежнего плана берется хвост от последнего достигнутого
/// узла, первые <c>releasedNodeCount</c> узлов остаются базовым планом, остальное — горизонтом.
/// </summary>
public static class VdaOrderUpdater
{
    /// <summary>
    /// Собирает обновление заказа.
    /// </summary>
    /// <param name="planJson">Прежний план заказа.</param>
    /// <param name="orderUpdateId">Новый номер версии заказа.</param>
    /// <param name="releasedNodeCount">Сколько узлов обновления входит в базовый план.</param>
    /// <param name="headerId">Идентификатор сообщения.</param>
    /// <param name="timestamp">Момент сборки.</param>
    /// <param name="lastNodeId">Последний достигнутый узел.</param>
    /// <param name="lastNodeSequenceId">Номер последнего достигнутого узла в плане.</param>
    /// <returns>Новый план либо <c>null</c>, если обновлять нечего.</returns>
    public static string? BuildUpdate(
        string planJson,
        int orderUpdateId,
        int releasedNodeCount,
        long headerId,
        DateTimeOffset timestamp,
        string? lastNodeId,
        int? lastNodeSequenceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(planJson);

        var plan = JsonNode.Parse(planJson) as JsonObject
            ?? throw new InvalidOperationException("План заказа не является JSON-объектом.");

        var allNodes = plan["nodes"]?.AsArray();
        if (allNodes is null || allNodes.Count < 2)
        {
            return null;
        }

        var allEdges = plan["edges"]?.AsArray() ?? new JsonArray();

        var startIndex = FindStartIndex(allNodes, lastNodeId, lastNodeSequenceId);
        if (startIndex < 0 || startIndex >= allNodes.Count - 1)
        {
            return null;
        }

        var startSequence = allNodes[startIndex]!["sequenceId"]!.GetValue<int>();

        var nodes = new JsonArray();
        var releasedNodes = new List<int>();
        for (var index = startIndex; index < allNodes.Count; index++)
        {
            var node = allNodes[index]!.DeepClone().AsObject();
            var released = index - startIndex < releasedNodeCount;
            node["released"] = released;
            if (released)
            {
                releasedNodes.Add(node["sequenceId"]!.GetValue<int>());
            }

            nodes.Add(node);
        }

        var firstReleased = releasedNodes.Count > 0 ? releasedNodes[0] : startSequence;
        var lastReleased = releasedNodes.Count > 0 ? releasedNodes[^1] : startSequence;
        var edges = new JsonArray();
        foreach (var edge in allEdges)
        {
            var sequence = edge!["sequenceId"]!.GetValue<int>();
            if (sequence <= startSequence)
            {
                continue;
            }

            var clone = edge.DeepClone().AsObject();
            clone["released"] = releasedNodes.Count > 1
                && sequence > firstReleased
                && sequence < lastReleased;
            edges.Add(clone);
        }

        if (nodes.Count < 2)
        {
            return null;
        }

        plan["headerId"] = headerId;
        plan["timestamp"] = timestamp.UtcDateTime.ToString(
            "yyyy-MM-ddTHH:mm:ss.fffZ",
            System.Globalization.CultureInfo.InvariantCulture);
        plan["orderUpdateId"] = orderUpdateId;
        plan["nodes"] = nodes;
        plan["edges"] = edges;

        return plan.ToJsonString();
    }

    /// <summary>
    /// Считает, сколько узлов базового плана осталось непройденными.
    /// </summary>
    /// <param name="planJson">План заказа.</param>
    /// <param name="reachedNodeIds">Достигнутые узлы.</param>
    /// <returns>Число непройденных узлов с <c>released = true</c>.</returns>
    public static int CountUnreachedReleased(string planJson, IReadOnlyCollection<string> reachedNodeIds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(planJson);
        ArgumentNullException.ThrowIfNull(reachedNodeIds);

        var plan = JsonNode.Parse(planJson) as JsonObject;
        var nodes = plan?["nodes"]?.AsArray();
        if (nodes is null)
        {
            return 0;
        }

        return nodes.Count(node =>
            node!["released"]!.GetValue<bool>()
            && !reachedNodeIds.Contains(node["nodeId"]!.GetValue<string>()));
    }

    private static int FindStartIndex(JsonArray nodes, string? lastNodeId, int? lastNodeSequenceId)
    {
        if (lastNodeSequenceId is not null)
        {
            for (var index = 0; index < nodes.Count; index++)
            {
                if (nodes[index]!["sequenceId"]!.GetValue<int>() == lastNodeSequenceId.Value)
                {
                    return index;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(lastNodeId))
        {
            for (var index = nodes.Count - 1; index >= 0; index--)
            {
                if (string.Equals(
                        nodes[index]!["nodeId"]!.GetValue<string>(),
                        lastNodeId,
                        StringComparison.Ordinal))
                {
                    return index;
                }
            }
        }

        return 0;
    }
}
