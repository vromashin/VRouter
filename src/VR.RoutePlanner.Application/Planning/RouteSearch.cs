using VR.RoutePlanner.Domain.Graph;
using VR.RoutePlanner.Domain.Planning;

namespace VR.RoutePlanner.Application.Planning;

/// <summary>
/// A* по подготовленным прохождениям ребер. Состояние включает ребро входа,
/// потому что цена поворота зависит от предыдущего ребра.
/// </summary>
internal sealed class RouteSearch
{
    private readonly Dictionary<Guid, List<EdgeTraversal>> _outgoing = new();
    private readonly EdgeCostCalculator _costs;
    private readonly HeuristicEstimator _heuristic;

    /// <summary>
    /// Создает поиск по списку прохождений.
    /// </summary>
    /// <param name="nodes">Узлы карты.</param>
    /// <param name="traversals">Допустимые прохождения ребер.</param>
    /// <param name="costs">Функция стоимости.</param>
    public RouteSearch(
        IReadOnlyDictionary<Guid, WarehouseNode> nodes,
        IEnumerable<EdgeTraversal> traversals,
        EdgeCostCalculator costs)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(traversals);
        ArgumentNullException.ThrowIfNull(costs);

        _costs = costs;
        _heuristic = new HeuristicEstimator(nodes, costs.TopSpeedMetersPerSecond);

        foreach (var traversal in traversals)
        {
            if (!_outgoing.TryGetValue(traversal.FromNodeId, out var list))
            {
                list = new List<EdgeTraversal>();
                _outgoing[traversal.FromNodeId] = list;
            }

            list.Add(traversal);
        }
    }

    /// <summary>
    /// Ищет маршрут между узлами.
    /// </summary>
    /// <param name="fromNodeId">Стартовый узел.</param>
    /// <param name="toNodeId">Целевой узел.</param>
    /// <param name="avoidNarrow">Исключать ребра с запасом ниже <c>NarrowClearanceMeters</c>.</param>
    /// <returns>Маршрут либо <c>null</c>, если пути нет.</returns>
    public SearchOutcome? Find(Guid fromNodeId, Guid toNodeId, bool avoidNarrow)
    {
        var open = new PriorityQueue<SearchState, double>();
        var best = new Dictionary<SearchState, double> { [new SearchState(fromNodeId, Guid.Empty)] = 0 };
        var cameFrom = new Dictionary<SearchState, (SearchState Previous, EdgeTraversal Traversal)>();

        open.Enqueue(new SearchState(fromNodeId, Guid.Empty), _heuristic.Estimate(fromNodeId, toNodeId));

        while (open.TryDequeue(out var state, out var priority))
        {
            if (state.NodeId == toNodeId)
            {
                return Reconstruct(state, cameFrom, best[state]);
            }

            var known = best[state];
            if (priority > known + _heuristic.Estimate(state.NodeId, toNodeId) + 1e-9)
            {
                continue;
            }

            var incoming = cameFrom.TryGetValue(state, out var entry) ? entry.Traversal : null;
            if (!_outgoing.TryGetValue(state.NodeId, out var candidates))
            {
                continue;
            }

            foreach (var traversal in candidates)
            {
                if (avoidNarrow && _costs.IsNarrow(traversal.Breakdown.ClearanceMeters))
                {
                    continue;
                }

                var isReversal = incoming is not null && incoming.Edge.Id == traversal.Edge.Id;
                var turnDegrees = incoming is null
                    ? 0
                    : EdgeGeometry.TurnDegrees(
                        incoming.Edge,
                        incoming.Direction,
                        traversal.Edge,
                        traversal.Direction);

                if (isReversal)
                {
                    // Разворот на том же ребре: геометрия дает ноль, потому что ребро одно и то же,
                    // физически это разворот на 180 градусов, поэтому цена поворота максимальная.
                    // Флаг RotationAllowed на таком маневре не проверяем: иначе тупики карты
                    // становятся недостижимыми, а данные о разрешении поворота неполные.
                    turnDegrees = EdgeGeometry.StraightAngleDegrees;
                }

                if (_costs.RespectRotationAllowed
                    && !isReversal
                    && incoming is not null
                    && turnDegrees > EdgeGeometry.RightAngleDegrees
                    && (incoming.Edge.RotationAllowed == false || traversal.Edge.RotationAllowed == false))
                {
                    continue;
                }

                var next = new SearchState(traversal.ToNodeId, traversal.Edge.Id);

                var candidate = known
                    + traversal.BaseCost
                    + _costs.TurnCost(turnDegrees)
                    + (traversal.Direction == RouteEdgeDirection.Backward
                        ? _costs.BackwardPenaltySeconds
                        : 0);
                if (best.TryGetValue(next, out var stored) && stored <= candidate)
                {
                    continue;
                }

                best[next] = candidate;
                cameFrom[next] = (state, traversal);
                open.Enqueue(next, candidate + _heuristic.Estimate(traversal.ToNodeId, toNodeId));
            }
        }

        return null;
    }

    private static bool HasVisited(
        SearchState state,
        Guid candidate,
        Dictionary<SearchState, (SearchState Previous, EdgeTraversal Traversal)> cameFrom,
        Guid startNodeId)
    {
        var current = state;
        while (true)
        {
            if (current.NodeId == candidate)
            {
                return true;
            }

            if (!cameFrom.TryGetValue(current, out var entry))
            {
                break;
            }

            current = entry.Previous;
        }

        return candidate == startNodeId;
    }

    private static SearchOutcome Reconstruct(
        SearchState goal,
        Dictionary<SearchState, (SearchState Previous, EdgeTraversal Traversal)> cameFrom,
        double cost)
    {
        var path = new List<EdgeTraversal>();
        var current = goal;
        while (cameFrom.TryGetValue(current, out var entry))
        {
            path.Add(entry.Traversal);
            current = entry.Previous;
        }

        path.Reverse();
        return new SearchOutcome(path, cost);
    }

}
