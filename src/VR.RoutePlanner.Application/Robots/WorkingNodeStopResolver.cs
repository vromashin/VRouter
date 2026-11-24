using VR.RoutePlanner.Domain.Graph;

namespace VR.RoutePlanner.Application.Robots;

/// <summary>
/// Точка подхода к рабочему узлу и точка остановки робота.
/// </summary>
/// <param name="ApproachX">Координата X точки подхода.</param>
/// <param name="ApproachY">Координата Y точки подхода.</param>
/// <param name="Theta">Курс от точки подхода к узлу, радианы.</param>
/// <param name="StopX">Координата X точки остановки робота.</param>
/// <param name="StopY">Координата Y точки остановки робота.</param>
public readonly record struct WorkingNodeStop(
    double ApproachX,
    double ApproachY,
    double Theta,
    double StopX,
    double StopY);

/// <summary>
/// Считает точку остановки робота у рабочего узла.
///
/// Правило перенесено из AGVControl (<c>PositionBeforeWorkingNode</c>): точка подхода это
/// единственный входящий узел, иначе предыдущий узел маршрута, иначе текущая поза робота;
/// курс считается от точки подхода к узлу, а остановка смещается по оси вил на
/// <see cref="WorkingNodeGeometry.WrkLength"/> в ближайшую к подъезду сторону.
/// </summary>
public static class WorkingNodeStopResolver
{
    /// <summary>
    /// Считает точку остановки для узла маршрута.
    /// </summary>
    /// <param name="graph">Граф версии карты.</param>
    /// <param name="planNodes">Узлы маршрута по порядку проезда.</param>
    /// <param name="index">Индекс рабочего узла в маршруте.</param>
    /// <param name="robotX">Координата X робота, если поза известна.</param>
    /// <param name="robotY">Координата Y робота, если поза известна.</param>
    /// <returns>Точка подхода, курс и точка остановки.</returns>
    public static WorkingNodeStop Resolve(
        WarehouseGraph graph,
        IReadOnlyList<WarehouseNode> planNodes,
        int index,
        double robotX,
        double robotY)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(planNodes);
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, planNodes.Count);

        var node = planNodes[index];
        var incoming = IncomingNodes(graph, node);

        double approachX;
        double approachY;
        if (incoming.Count == 1)
        {
            approachX = incoming[0].X;
            approachY = incoming[0].Y;
        }
        else if (index > 0)
        {
            approachX = planNodes[index - 1].X;
            approachY = planNodes[index - 1].Y;
        }
        else
        {
            approachX = robotX;
            approachY = robotY;
        }

        // Стеллажи на карте выровнены по осям: курс к паллете должен быть кратен 90 градусам.
        // Иначе диагональный подход дает 45 градусов и вилы смотрят мимо стеллажа.
        var rawTheta = WorkingNodeGeometry.Course(approachX, approachY, node.X, node.Y);
        var quarter = Math.PI / 2;
        var theta = Math.Round(rawTheta / quarter) * quarter;
        theta = Math.Atan2(Math.Sin(theta), Math.Cos(theta));
        var stop = WorkingNodeGeometry.StopPoint(node, theta, approachX, approachY);
        return new WorkingNodeStop(approachX, approachY, theta, stop.X, stop.Y);
    }

    private static IReadOnlyList<WarehouseNode> IncomingNodes(WarehouseGraph graph, WarehouseNode node)
    {
        var result = new List<WarehouseNode>();
        var seen = new HashSet<Guid>();

        foreach (var list in graph.Edges.Values)
        {
            foreach (var edge in list)
            {
                if (edge.FromNodeId != node.Id && edge.ToNodeId != node.Id)
                {
                    continue;
                }

                var otherId = edge.FromNodeId == node.Id ? edge.ToNodeId : edge.FromNodeId;
                if (!seen.Add(otherId))
                {
                    continue;
                }

                if (graph.Nodes.TryGetValue(otherId, out var other))
                {
                    result.Add(other);
                }
            }
        }

        return result;
    }
}
