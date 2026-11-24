using System.Text.Json;
using VR.RoutePlanner.Application.Configuration;
using VR.RoutePlanner.Domain.Planning;
using VR.RoutePlanner.Domain.Traffic;

namespace VR.RoutePlanner.Application.Traffic;

/// <summary>
/// Горизонт резервирования по сохраненному плану заказа: чистый расчет без БД и без времени.
///
/// Нумерация VDA-плана: узел <c>2 * index</c>, ребро <c>2 * index + 1</c>. Горизонт начинается
/// с последнего достигнутого узла <c>lastNodeSequenceId</c> и занимает
/// <c>ReleaseHorizonNodes</c> узлов (<c>2 * ReleaseHorizonNodes</c> номеров) и все ребра
/// до последнего узла горизонта включительно.
/// </summary>
public static class TrafficHorizonCalculator
{
    /// <summary>Префикс виртуального стартового ребра заказа.</summary>
    private const string VirtualEdgePrefix = "VIRTUAL_START_";

    /// <summary>
    /// Считает горизонт ресурсов для робота.
    /// </summary>
    /// <param name="planJson">План заказа <c>vrt_RobotOrder.PlanJson</c>.</param>
    /// <param name="lastNodeSequenceId">Последний достигнутый узел из <c>state</c>.</param>
    /// <param name="map">Разрешение ресурсов карты и профиля робота.</param>
    /// <param name="options">Параметры <c>Routing</c>.</param>
    /// <returns>Горизонт; пустой, если плана, карты или ресурсов нет.</returns>
    public static TrafficHorizon Calculate(
        string? planJson,
        int? lastNodeSequenceId,
        ITrafficResourceMap? map,
        RoutePlannerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (map is null || string.IsNullOrWhiteSpace(planJson))
        {
            return TrafficHorizon.Empty;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(planJson);
        }
        catch (JsonException)
        {
            return TrafficHorizon.Empty;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("nodes", out var nodes)
                || nodes.ValueKind != JsonValueKind.Array
                || nodes.GetArrayLength() == 0)
            {
                return TrafficHorizon.Empty;
            }

            var start = Math.Max(0, lastNodeSequenceId ?? 0);
            var horizonEnd = start + (2 * Math.Max(0, options.ReleaseHorizonNodes));

            var entries = new Dictionary<Guid, TrafficHorizonEntry>();
            foreach (var node in nodes.EnumerateArray())
            {
                if (!TryReadSequence(node, out var sequence) || sequence < start || sequence > horizonEnd)
                {
                    continue;
                }

                var nodeId = node.TryGetProperty("nodeId", out var id) ? id.GetString() : null;
                if (string.IsNullOrWhiteSpace(nodeId))
                {
                    continue;
                }

                var resource = map.FindNode(nodeId!);
                if (resource is null || !IsReservableNode(resource.ResourceType))
                {
                    continue;
                }

                Add(entries, new TrafficHorizonEntry(
                    resource.ResourceId,
                    resource.ResourceCode,
                    resource.ResourceType,
                    resource.Capacity,
                    Direction: null,
                    sequence,
                    IsNode: true));
            }

            var missing = new List<string>();
            if (root.TryGetProperty("edges", out var edges) && edges.ValueKind == JsonValueKind.Array)
            {
                foreach (var edge in edges.EnumerateArray())
                {
                    // Ребра только до последнего узла горизонта включительно: ребро с номером
                    // horizonEnd начинается в последнем узле горизонта и ведет уже за горизонт.
                    if (!TryReadSequence(edge, out var sequence)
                        || sequence < start
                        || sequence >= horizonEnd)
                    {
                        continue;
                    }

                    var edgeId = edge.TryGetProperty("edgeId", out var code) ? code.GetString() : null;
                    if (string.IsNullOrWhiteSpace(edgeId))
                    {
                        continue;
                    }

                    // Виртуальное стартовое ребро VdaOrderBuilder (VIRTUAL_START_*) не лежит в карте.
                    // Признак по префиксу: maxSpeed в ребрах заказа больше нет, как в эталоне AGVControl.
                    if (edgeId!.StartsWith(VirtualEdgePrefix, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var startNodeId = edge.TryGetProperty("startNodeId", out var fromNode)
                        ? fromNode.GetString()
                        : null;
                    var resource = map.FindEdge(edgeId!, startNodeId);
                    if (resource is null)
                    {
                        missing.Add(edgeId!);
                        continue;
                    }

                    if (resource.ResourceType == TrafficResourceType.INDEPENDENT)
                    {
                        continue;
                    }

                    Add(entries, new TrafficHorizonEntry(
                        resource.ResourceId,
                        resource.ResourceCode,
                        resource.ResourceType,
                        resource.Capacity,
                        resource.Direction,
                        sequence,
                        IsNode: false));
                }
            }

            var ordered = entries.Values
                .OrderBy(entry => entry.SequenceId)
                .ThenBy(entry => entry.ResourceId)
                .ToList();

            return new TrafficHorizon(ordered, missing);
        }
    }

    /// <summary>
    /// Узловой <c>EXCLUSIVE</c> (узел степени не больше двух) не резервируется: он лежит внутри
    /// коридора, который уже держится отдельным ресурсом. <c>CONFLICT_ZONE</c> резервируется.
    /// </summary>
    private static bool IsReservableNode(TrafficResourceType resourceType) =>
        resourceType == TrafficResourceType.CONFLICT_ZONE;

    private static void Add(Dictionary<Guid, TrafficHorizonEntry> entries, TrafficHorizonEntry entry) =>
        entries.TryAdd(entry.ResourceId, entry);

    private static bool TryReadSequence(JsonElement item, out int sequence)
    {
        sequence = 0;
        return item.ValueKind == JsonValueKind.Object
            && item.TryGetProperty("sequenceId", out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out sequence);
    }
}
