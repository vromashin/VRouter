using VR.RoutePlanner.Domain.Graph;
using VR.RoutePlanner.Domain.Planning;

namespace VR.RoutePlanner.Application.Planning;

/// <summary>
/// Геометрия ребра: длина по <c>TrajectoryPoints</c> и касательные на концах.
/// Касательные считаются по точкам траектории, а не по <c>Theta</c> узла: у узлов
/// theta нулевая и направление ребра не описывает.
/// </summary>
public static class EdgeGeometry
{
    /// <summary>Порог, ниже которого переход считается прямым, градусы.</summary>
    public const double TurnThresholdDegrees = 5;

    /// <summary>Прямой угол, градусы: выше него поворот запрещен при <c>RotationAllowed = false</c>.</summary>
    public const double RightAngleDegrees = 90;

    /// <summary>Развернутый угол, градусы.</summary>
    public const double StraightAngleDegrees = 180;

    /// <summary>
    /// Длина геометрии ребра в метрах: сумма отрезков между точками траектории,
    /// при отсутствии траектории — длина из <c>Edges.Length</c>.
    /// </summary>
    /// <param name="edge">Ребро.</param>
    /// <returns>Длина в метрах, не меньше нуля.</returns>
    public static double LengthMeters(WarehouseEdge edge)
    {
        ArgumentNullException.ThrowIfNull(edge);

        var points = edge.TrajectoryPoints;
        if (points is null || points.Count < 2)
        {
            return edge.Length < 0 ? 0 : edge.Length;
        }

        var total = 0.0;
        for (var i = 1; i < points.Count; i++)
        {
            total += Distance(points[i - 1], points[i]);
        }

        return total;
    }

    /// <summary>
    /// Единичная касательная ребра в точке его начала по геометрии <c>TrajectoryPoints</c>.
    /// </summary>
    /// <param name="edge">Ребро.</param>
    /// <param name="direction">Направление прохождения ребра.</param>
    /// <returns>Единичный вектор; при вырожденной геометрии — нулевой.</returns>
    public static (double X, double Y) TangentAtStart(WarehouseEdge edge, RouteEdgeDirection direction)
    {
        ArgumentNullException.ThrowIfNull(edge);

        var points = edge.TrajectoryPoints;
        if (points is null || points.Count < 2)
        {
            return (0, 0);
        }

        var (first, second) = direction == RouteEdgeDirection.Forward
            ? (points[0], points[1])
            : (points[^1], points[^2]);

        return Normalize(second.X - first.X, second.Y - first.Y);
    }

    /// <summary>
    /// Угол между входящим и исходящим ребрами в общем узле, градусы.
    /// </summary>
    /// <param name="incoming">Входящее ребро.</param>
    /// <param name="incomingDirection">Направление прохождения входящего ребра.</param>
    /// <param name="outgoing">Исходящее ребро.</param>
    /// <param name="outgoingDirection">Направление прохождения исходящего ребра.</param>
    /// <returns>Угол 0..180 градусов; 0 — движение прямо.</returns>
    public static double TurnDegrees(
        WarehouseEdge incoming,
        RouteEdgeDirection incomingDirection,
        WarehouseEdge outgoing,
        RouteEdgeDirection outgoingDirection)
    {
        ArgumentNullException.ThrowIfNull(incoming);
        ArgumentNullException.ThrowIfNull(outgoing);

        var vectorIn = ArrivalVector(incoming, incomingDirection);
        var vectorOut = TangentAtStart(outgoing, outgoingDirection);

        if (IsZero(vectorIn) || IsZero(vectorOut))
        {
            return 0;
        }

        var dot = (vectorIn.X * vectorOut.X) + (vectorIn.Y * vectorOut.Y);
        if (dot > 1)
        {
            dot = 1;
        }
        else if (dot < -1)
        {
            dot = -1;
        }

        return Math.Acos(dot) * StraightAngleDegrees / Math.PI;
    }

    /// <summary>
    /// Евклидово расстояние между узлами по координатам карты.
    /// </summary>
    /// <param name="from">Узел начала.</param>
    /// <param name="to">Узел конца.</param>
    /// <returns>Расстояние в метрах.</returns>
    public static double Distance(WarehouseNode from, WarehouseNode to)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);

        var dx = to.X - from.X;
        var dy = to.Y - from.Y;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }

    private static (double X, double Y) ArrivalVector(WarehouseEdge edge, RouteEdgeDirection direction)
    {
        var tangent = TangentAtStart(edge, direction);
        return (-tangent.X, -tangent.Y);
    }

    private static double Distance(MapPoint from, MapPoint to)
    {
        var dx = to.X - from.X;
        var dy = to.Y - from.Y;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }

    private static (double X, double Y) Normalize(double x, double y)
    {
        var length = Math.Sqrt((x * x) + (y * y));
        return length <= double.Epsilon ? (0, 0) : (x / length, y / length);
    }

    private static bool IsZero((double X, double Y) vector) =>
        vector.X == 0 && vector.Y == 0;
}
