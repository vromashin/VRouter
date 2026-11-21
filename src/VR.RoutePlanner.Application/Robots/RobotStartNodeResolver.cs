using VR.RoutePlanner.Domain.Graph;

namespace VR.RoutePlanner.Application.Robots;

/// <summary>
/// Определяет стартовый узел заказа. VDA требует, чтобы первый узел заказа совпадал
/// с текущим узлом робота, поэтому при известной позиции берется ближайший активный узел
/// к координатам: позиция точнее, чем <c>lastNodeId</c>, который робот публикует редко.
/// Если позиции нет, используется последний узел из состояния.
/// </summary>
public static class RobotStartNodeResolver
{
    /// <summary>Допуск совпадения позиции с узлом, метры.</summary>
    public const double PositionToleranceMeters = 0.5;

    public static WarehouseNode? Resolve(WarehouseGraph graph, RobotSnapshot? snapshot)
    {
        ArgumentNullException.ThrowIfNull(graph);

        if (snapshot is null)
        {
            return null;
        }

        if (snapshot.PositionX != 0 || snapshot.PositionY != 0)
        {
            return graph.Nodes.Values
                .Where(node => node.IsActive)
                .OrderBy(node => Math.Pow(node.X - snapshot.PositionX, 2) + Math.Pow(node.Y - snapshot.PositionY, 2))
                .FirstOrDefault();
        }

        if (!string.IsNullOrWhiteSpace(snapshot.LastNodeId))
        {
            return graph.Nodes.Values
                .FirstOrDefault(node => string.Equals(node.NodeId, snapshot.LastNodeId, StringComparison.Ordinal));
        }

        return null;
    }
}
