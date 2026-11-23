using System.Text.Json.Nodes;

namespace VR.RoutePlanner.Application.Robots;

/// <summary>
/// Обновление заказа VDA 5050: продолжение плана от последнего достигнутого узла.
///
/// Нумерация узлов и ребер не пересчитывается: первый узел обновления сохраняет свой
/// <c>sequenceId</c> из исходного плана, первое ребро — <c>sequenceId + 1</c>.
/// Обновление содержит узел-якорь (на котором стоит робот) и следующие
/// <c>releasedNodeCount - 1</c> узлов; все узлы и ребра куска <c>released = true</c>.
/// </summary>
public static class VdaOrderUpdater
{
    /// <summary>
    /// Собирает обновление заказа.
    /// </summary>
    /// <param name="planJson">Прежний план заказа.</param>
    /// <param name="orderUpdateId">Новый номер версии заказа.</param>
    /// <param name="releasedNodeCount">Сколько узлов в куске обновления включая узел-якорь.</param>
    /// <param name="headerId">Идентификатор сообщения.</param>
    /// <param name="timestamp">Момент сборки.</param>
    /// <param name="lastNodeId">Последний достигнутый узел.</param>
    /// <param name="lastNodeSequenceId">Номер последнего достигнутого узла в плане.</param>
    /// <param name="reachedNodeIds">Пройденные узлы: нужны, когда нет ни номера, ни узла.</param>
    /// <returns>Новый план либо <c>null</c>, если обновлять нечего.</returns>
    public static string? BuildUpdate(
        string planJson,
        int orderUpdateId,
        int releasedNodeCount,
        long headerId,
        DateTimeOffset timestamp,
        string? lastNodeId,
        int? lastNodeSequenceId,
        IReadOnlyCollection<string>? reachedNodeIds = null)
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

        var startIndex = FindStartIndex(allNodes, lastNodeId, lastNodeSequenceId, reachedNodeIds);
        if (startIndex < 0 || startIndex >= allNodes.Count - 1)
        {
            return null;
        }

        var startSequence = allNodes[startIndex]!["sequenceId"]!.GetValue<int>();
        var count = releasedNodeCount <= 0
            ? allNodes.Count - startIndex
            : Math.Min(releasedNodeCount, allNodes.Count - startIndex);

        var nodes = new JsonArray();
        for (var index = startIndex; index < startIndex + count; index++)
        {
            var node = allNodes[index]!.DeepClone().AsObject();
            node["released"] = true;
            nodes.Add(node);
        }

        var lastSequence = startSequence + ((count - 1) * 2);
        var edges = new JsonArray();
        foreach (var edge in allEdges)
        {
            var sequence = edge!["sequenceId"]!.GetValue<int>();
            if (sequence <= startSequence || sequence > lastSequence)
            {
                continue;
            }

            var clone = edge.DeepClone().AsObject();
            clone["released"] = true;
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
    /// Сколько узлов текущего куска осталось пройти после <paramref name="lastNodeSequenceId"/>.
    ///
    /// Основной источник — <c>released</c> из состояния робота: он возвращает ровно тот горизонт,
    /// который получил. Если узлов в состоянии нет, горизонт считается по плану шагом
    /// <c>releasedNodes - 1</c> от начала сохраненного плана.
    /// </summary>
    /// <param name="planJson">Сохраненный план заказа.</param>
    /// <param name="lastNodeSequenceId">Последний достигнутый <c>sequenceId</c> из состояния.</param>
    /// <param name="releasedNodes">Размер куска в узлах.</param>
    /// <param name="nodeStates">Узлы из состояния робота с признаком <c>released</c>.</param>
    /// <returns>Число непройденных узлов текущего куска.</returns>
    public static int CountUnreachedInChunk(
        string planJson,
        int lastNodeSequenceId,
        int releasedNodes,
        IReadOnlyCollection<RobotNodeState>? nodeStates = null)
    {
        if (nodeStates is { Count: > 0 })
        {
            return nodeStates.Count(state => state.Released && state.SequenceId > lastNodeSequenceId);
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(planJson);

        var plan = JsonNode.Parse(planJson) as JsonObject;
        var nodes = plan?["nodes"]?.AsArray();
        if (nodes is null || nodes.Count == 0)
        {
            return 0;
        }

        var firstIndex = nodes[0]!["sequenceId"]!.GetValue<int>() / 2;
        var step = Math.Max(1, releasedNodes - 1);
        var offset = Math.Max(0, (lastNodeSequenceId / 2) - firstIndex);
        var chunkEnd = firstIndex + ((offset / step) * step) + Math.Max(0, releasedNodes - 1);

        return nodes.Count(node =>
            node!["sequenceId"]!.GetValue<int>() > lastNodeSequenceId
            && node["sequenceId"]!.GetValue<int>() / 2 <= chunkEnd);
    }

    /// <summary>
    /// Сколько узлов плана ещё не пройдено, если номер узла неизвестен.
    /// </summary>
    /// <param name="planJson">Сохраненный план заказа.</param>
    /// <param name="reachedNodeIds">Достигнутые узлы.</param>
    /// <returns>Число непройденных узлов.</returns>
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

    /// <summary>
    /// Индекс узла-якоря: узел с <paramref name="lastNodeSequenceId"/>, иначе последний узел
    /// с <paramref name="lastNodeId"/>, иначе первый непройденный узел плана.
    /// </summary>
    private static int FindStartIndex(
        JsonArray nodes,
        string? lastNodeId,
        int? lastNodeSequenceId,
        IReadOnlyCollection<string>? reachedNodeIds)
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

        if (reachedNodeIds is not null)
        {
            for (var index = 0; index < nodes.Count; index++)
            {
                if (!reachedNodeIds.Contains(nodes[index]!["nodeId"]!.GetValue<string>()))
                {
                    return index;
                }
            }
        }

        return 0;
    }
}
