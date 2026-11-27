using VR.RoutePlanner.Domain.Graph;
using VR.RoutePlanner.Domain.Robots;

namespace VR.RoutePlanner.Application.Robots;

/// <summary>
/// Состояние робота в автомате автопереездов (Д-3.4).
/// </summary>
public enum RobotRelocationState
{
    /// <summary>Есть активный заказ WMS либо робот занят чем-то ещё: автопереезд запрещен.</summary>
    Busy,

    /// <summary>Есть активный заказ с целью на узле <c>CHARGING</c>.</summary>
    GoingToCharge,

    /// <summary>Робот заряжается: флаг <c>state</c> либо узел <c>CHARGING</c> под роботом.</summary>
    Charging,

    /// <summary>Есть активный заказ с целью на узле <c>PARKING</c>.</summary>
    GoingToParking,

    /// <summary>Робот стоит на узле <c>PARKING</c> без активного заказа.</summary>
    Parked,

    /// <summary>Заказа нет, робот не движется, ошибок нет, робот не на <c>CHARGING</c>/<c>PARKING</c>.</summary>
    Idle,
}

/// <summary>
/// Автомат состояния робота и поиск узла под роботом. Состояние выводится заново каждый тик
/// из базы и сообщения <c>state</c>, поэтому переживает рестарт сервиса; отдельно не хранится.
/// Функции чистые: время и сеть не используются, результат воспроизводим в тестах.
/// </summary>
public static class RobotRelocationStateMachine
{
    /// <summary>
    /// Ищет узел карты по идентификатору VDA.
    /// </summary>
    /// <param name="graph">Граф карты либо <c>null</c>.</param>
    /// <param name="nodeId">Идентификатор узла.</param>
    /// <returns>Узел либо <c>null</c>, если карты или узла нет.</returns>
    public static WarehouseNode? FindNode(WarehouseGraph? graph, string? nodeId)
    {
        if (graph is null || string.IsNullOrWhiteSpace(nodeId))
        {
            return null;
        }

        return graph.Nodes.Values
            .FirstOrDefault(node => string.Equals(node.NodeId, nodeId, StringComparison.Ordinal));
    }

    /// <summary>
    /// Определяет узел, на котором стоит робот: <c>LastNodeId</c> из <c>state</c>, иначе
    /// ближайший узел карты в радиусе <paramref name="toleranceMeters"/>. <c>LastNodeId</c>
    /// авторитетен: если он указывает на узел карты, радиус не применяется.
    /// </summary>
    /// <param name="graph">Граф карты либо <c>null</c>.</param>
    /// <param name="snapshot">Состояние робота.</param>
    /// <param name="toleranceMeters">Радиус «робот стоит на узле», метры.</param>
    /// <returns>Узел либо <c>null</c>.</returns>
    public static WarehouseNode? NodeUnderRobot(
        WarehouseGraph? graph,
        RobotSnapshot snapshot,
        double toleranceMeters)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (graph is null)
        {
            return null;
        }

        var byNodeId = FindNode(graph, snapshot.LastNodeId);
        if (byNodeId is not null)
        {
            return byNodeId;
        }

        var radius = Math.Max(toleranceMeters, 0);
        var radiusSquared = radius * radius;
        WarehouseNode? best = null;
        var bestDistanceSquared = double.MaxValue;

        foreach (var node in graph.Nodes.Values)
        {
            var deltaX = node.X - snapshot.PositionX;
            var deltaY = node.Y - snapshot.PositionY;
            var distanceSquared = (deltaX * deltaX) + (deltaY * deltaY);
            if (distanceSquared > radiusSquared)
            {
                continue;
            }

            if (best is null
                || distanceSquared < bestDistanceSquared
                || (distanceSquared == bestDistanceSquared
                    && string.CompareOrdinal(node.NodeId, best.NodeId) < 0))
            {
                best = node;
                bestDistanceSquared = distanceSquared;
            }
        }

        return best;
    }

    /// <summary>
    /// Определяет узел нужного типа под роботом.
    /// </summary>
    /// <param name="graph">Граф карты либо <c>null</c>.</param>
    /// <param name="snapshot">Состояние робота.</param>
    /// <param name="nodeType">Тип узла: <c>CHARGING</c> либо <c>PARKING</c>.</param>
    /// <param name="toleranceMeters">Радиус «робот стоит на узле», метры.</param>
    /// <returns>Узел нужного типа либо <c>null</c>.</returns>
    public static WarehouseNode? NodeOfType(
        WarehouseGraph? graph,
        RobotSnapshot snapshot,
        string nodeType,
        double toleranceMeters)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeType);

        var node = NodeUnderRobot(graph, snapshot, toleranceMeters);
        return node is not null && string.Equals(node.NodeType, nodeType, StringComparison.Ordinal)
            ? node
            : null;
    }

    /// <summary>
    /// Выводит состояние робота по таблице Д-3.4.
    /// </summary>
    /// <param name="snapshot">Состояние робота из топика <c>state</c>.</param>
    /// <param name="activeOrder">Активный заказ робота либо <c>null</c>.</param>
    /// <param name="graph">Граф карты робота либо <c>null</c>: без карты цель заказа не классифицируется.</param>
    /// <param name="idleToleranceMeters">Радиус «робот стоит на узле», метры.</param>
    /// <returns>Состояние автомата.</returns>
    public static RobotRelocationState Determine(
        RobotSnapshot snapshot,
        RobotOrder? activeOrder,
        WarehouseGraph? graph,
        double idleToleranceMeters)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (activeOrder is not null)
        {
            var targetType = FindNode(graph, activeOrder.TargetNodeId)?.NodeType;
            if (string.Equals(targetType, RelocationNodeTypes.Charging, StringComparison.Ordinal))
            {
                return RobotRelocationState.GoingToCharge;
            }

            if (string.Equals(targetType, RelocationNodeTypes.Parking, StringComparison.Ordinal))
            {
                return RobotRelocationState.GoingToParking;
            }

            return RobotRelocationState.Busy;
        }

        if (snapshot.Charging)
        {
            return RobotRelocationState.Charging;
        }

        var node = NodeUnderRobot(graph, snapshot, idleToleranceMeters);
        if (node is not null)
        {
            if (string.Equals(node.NodeType, RelocationNodeTypes.Charging, StringComparison.Ordinal))
            {
                return RobotRelocationState.Charging;
            }

            if (string.Equals(node.NodeType, RelocationNodeTypes.Parking, StringComparison.Ordinal))
            {
                return RobotRelocationState.Parked;
            }
        }

        if (!snapshot.Driving && snapshot.Errors.Count == 0)
        {
            return RobotRelocationState.Idle;
        }

        return RobotRelocationState.Busy;
    }
}
