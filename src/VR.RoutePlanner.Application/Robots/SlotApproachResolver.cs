using VR.RoutePlanner.Domain.Graph;

namespace VR.RoutePlanner.Application.Robots;

/// <summary>
/// Слот рабочего места (узлы <c>wo-*</c>) робот не заезжает: он останавливается
/// на соседнем узле и работает вилами. Старый AGVControl вел себя так же.
/// </summary>
public static class SlotApproachResolver
{
    /// <summary>Признак узла слота.</summary>
    public const string SlotPrefix = "wo-";

    /// <summary>
    /// Возвращает узел, до которого нужно строить маршрут, и узел слота, если цель это слот.
    /// </summary>
    /// <param name="graph">Граф карты.</param>
    /// <param name="target">Целевой узел заказа.</param>
    /// <param name="stopBeforeSlot">Разрешено ли останавливаться перед слотом.</param>
    /// <returns>Пара: узел маршрута и узел слота (или <c>null</c>).</returns>
    public static (WarehouseNode RouteTarget, WarehouseNode? Slot) Resolve(
        WarehouseGraph graph,
        WarehouseNode target,
        bool stopBeforeSlot)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(target);

        if (!stopBeforeSlot || !target.NodeId.StartsWith(SlotPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return (target, null);
        }

        var neighbour = graph.Edges.Values
            .SelectMany(list => list)
            .Where(edge => edge.FromNodeId == target.Id || edge.ToNodeId == target.Id)
            .Select(edge => edge.FromNodeId == target.Id ? edge.ToNodeId : edge.FromNodeId)
            .Distinct()
            .Select(id => graph.Nodes.GetValueOrDefault(id))
            .Where(node => node is not null)
            .Select(node => node!)
            .Where(node => !node.NodeId.StartsWith(SlotPrefix, StringComparison.OrdinalIgnoreCase))
            .OrderBy(node => node.NodeId, StringComparer.Ordinal)
            .FirstOrDefault();

        return neighbour is null ? (target, null) : (neighbour, target);
    }
}
