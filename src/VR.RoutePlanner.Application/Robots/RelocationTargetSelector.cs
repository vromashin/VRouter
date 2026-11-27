using VR.RoutePlanner.Domain.Graph;

namespace VR.RoutePlanner.Application.Robots;

/// <summary>
/// Типы узлов карты, на которые робот уезжает сам: зарядка и парковка.
/// Тип сравнивается строго со <see cref="WarehouseNode.NodeType"/>, а не по префиксу кода узла.
/// </summary>
public static class RelocationNodeTypes
{
    /// <summary>Узел зарядки.</summary>
    public const string Charging = "CHARGING";

    /// <summary>Узел парковки.</summary>
    public const string Parking = "PARKING";
}

/// <summary>
/// Поза робота на карте: из нее считается расстояние до узла и признак «робот стоит на узле».
/// </summary>
/// <param name="SerialNumber">Серийный номер: свой робот сам себя не блокирует.</param>
/// <param name="LastNodeId">Последний узел из <c>state</c> либо <c>null</c>.</param>
/// <param name="X">Координата X, метры.</param>
/// <param name="Y">Координата Y, метры.</param>
public sealed record RelocationRobotPose(string SerialNumber, string? LastNodeId, double X, double Y);

/// <summary>
/// Занятость узлов карты, собранная снаружи: позы роботов, активные заказы и резервы процесса.
/// Сам выбор узла в базу и сеть не ходит, поэтому проверяется юнит-тестами без инфраструктуры.
/// Узел занят, если там физически стоит другой робот, если на него уже едет другой робот
/// по активному заказу либо если узел зарезервирован за другим роботом.
/// </summary>
public sealed class RelocationOccupancy
{
    /// <summary>Позы роботов: <c>LastNodeId</c> либо координаты в радиусе узла.</summary>
    public IReadOnlyCollection<RelocationRobotPose> Poses { get; init; } = Array.Empty<RelocationRobotPose>();

    /// <summary>
    /// Узлы, на которые уже едут роботы: ключ это <c>NodeId</c> узла, значение это
    /// <c>TargetNodeId</c> активного заказа робота.
    /// </summary>
    public IReadOnlyDictionary<string, string> ActiveOrderTargets { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// Резервы процесса: ключ это <c>NodeId</c> узла, значение это серийный номер робота,
    /// которому выдан автопереезд.
    /// </summary>
    public IReadOnlyDictionary<string, string> Reservations { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Радиус, в котором робот считается стоящим на узле, метры.</summary>
    public double OccupiedRadiusMeters { get; init; } = 0.75;

    /// <summary>
    /// Занят ли узел для робота <paramref name="selfSerialNumber"/>: свои собственные признаки
    /// занятостью не считаются, робот не блокирует себя.
    /// </summary>
    /// <param name="node">Узел карты.</param>
    /// <param name="selfSerialNumber">Серийный номер своего робота либо <c>null</c>.</param>
    /// <returns><c>true</c>, если узел занят другим роботом, чужим заказом либо чужим резервом.</returns>
    public bool IsOccupied(WarehouseNode node, string? selfSerialNumber = null)
    {
        ArgumentNullException.ThrowIfNull(node);

        var radiusSquared = OccupiedRadiusMeters * OccupiedRadiusMeters;
        foreach (var pose in Poses)
        {
            if (IsSelf(pose.SerialNumber, selfSerialNumber))
            {
                continue;
            }

            if (string.Equals(pose.LastNodeId, node.NodeId, StringComparison.Ordinal))
            {
                return true;
            }

            var deltaX = pose.X - node.X;
            var deltaY = pose.Y - node.Y;
            if ((deltaX * deltaX) + (deltaY * deltaY) <= radiusSquared)
            {
                return true;
            }
        }

        return IsClaimedByForeignRobot(ActiveOrderTargets, node.NodeId, selfSerialNumber)
            || IsClaimedByForeignRobot(Reservations, node.NodeId, selfSerialNumber);
    }

    private static bool IsClaimedByForeignRobot(
        IReadOnlyDictionary<string, string> claims,
        string nodeId,
        string? selfSerialNumber) =>
        claims.TryGetValue(nodeId, out var serialNumber) && !IsSelf(serialNumber, selfSerialNumber);

    private static bool IsSelf(string? serialNumber, string? selfSerialNumber) =>
        selfSerialNumber is not null && string.Equals(serialNumber, selfSerialNumber, StringComparison.Ordinal);
}

/// <summary>
/// Выбор целевого узла автопереезда: ближайший свободный узел нужного типа на карте робота.
/// Функция чистая: узлы и занятость приходят параметрами, время и база не используются,
/// поэтому результат воспроизводим в тестах и при повторных прогонах.
/// </summary>
public static class RelocationTargetSelector
{
    /// <summary>
    /// Выбирает ближайший свободный узел заданного типа.
    /// Сортировка кандидатов: квадрат расстояния до робота, при равенстве — <c>NodeId</c>
    /// по <see cref="StringComparer.Ordinal"/>.
    /// </summary>
    /// <param name="nodes">Узлы карты робота.</param>
    /// <param name="nodeType">Тип узла: <see cref="RelocationNodeTypes.Charging"/> либо <see cref="RelocationNodeTypes.Parking"/>.</param>
    /// <param name="robot">Поза робота: точка отсчета расстояния и серийный номер для проверки занятости.</param>
    /// <param name="occupancy">Занятость узлов, собранная снаружи.</param>
    /// <returns>Выбранный узел либо <c>null</c>: узлов нужного типа нет либо все заняты.</returns>
    public static WarehouseNode? Select(
        IEnumerable<WarehouseNode> nodes,
        string nodeType,
        RelocationRobotPose robot,
        RelocationOccupancy occupancy)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeType);
        ArgumentNullException.ThrowIfNull(robot);
        ArgumentNullException.ThrowIfNull(occupancy);

        WarehouseNode? best = null;
        var bestDistanceSquared = double.MaxValue;

        foreach (var node in nodes)
        {
            if (node is null || !string.Equals(node.NodeType, nodeType, StringComparison.Ordinal))
            {
                continue;
            }

            if (occupancy.IsOccupied(node, robot.SerialNumber))
            {
                continue;
            }

            var deltaX = node.X - robot.X;
            var deltaY = node.Y - robot.Y;
            var distanceSquared = (deltaX * deltaX) + (deltaY * deltaY);

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
}
