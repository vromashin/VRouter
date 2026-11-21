using VR.RoutePlanner.Domain.Graph;

namespace VR.RoutePlanner.Application.Robots;

/// <summary>
/// Определяет стартовый узел робота: последний известный узел из состояния,
/// иначе ближайший активный узел к позиции. Второй путь нужен, когда робот
/// не публикует <c>lastNodeId</c> либо публикует заглушку.
/// </summary>
public static class RobotStartNodeResolver
{
    public static WarehouseNode? Resolve(WarehouseGraph graph, RobotSnapshot? snapshot)
    {
        ArgumentNullException.ThrowIfNull(graph);

        if (snapshot is null)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(snapshot.LastNodeId))
        {
            var byNodeId = graph.Nodes.Values
                .FirstOrDefault(node => string.Equals(node.NodeId, snapshot.LastNodeId, StringComparison.Ordinal));
            if (byNodeId is not null)
            {
                return byNodeId;
            }
        }

        if (snapshot.PositionX == 0 && snapshot.PositionY == 0)
        {
            return null;
        }

        return graph.Nodes.Values
            .Where(node => node.IsActive)
            .OrderBy(node => Math.Pow(node.X - snapshot.PositionX, 2) + Math.Pow(node.Y - snapshot.PositionY, 2))
            .FirstOrDefault();
    }
}
