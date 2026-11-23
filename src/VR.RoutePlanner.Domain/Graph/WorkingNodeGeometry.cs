namespace VR.RoutePlanner.Domain.Graph;

/// <summary>
/// Геометрия рабочего узла: точка остановки робота перед паллетой.
/// Правило перенесено из AGVControl (<c>Pallet.WrkLength</c> и <c>Node.AgvStopPositionXY</c>):
/// робот останавливается на смещении по оси вил, ближайшем к точке подхода.
/// </summary>
public static class WorkingNodeGeometry
{
    /// <summary>
    /// Смещение точки остановки от узла по оси вил, метры.
    /// Складывается как (длина паллеты + длина робота) / 2 + зазор между ними.
    /// </summary>
    public const double WrkLength = 1.475;

    /// <summary>Префикс кодов узлов стеллажных слотов.</summary>
    public const string SlotPrefix = "wo-";

    private static readonly string[] ForkTypes = ["WORKING", "LOADING", "UNLOADING"];

    private static readonly string[] ThetaTypes = ["LOADING", "UNLOADING"];

    /// <summary>
    /// Узел требует остановки перед паллетой: тип WORKING/LOADING/UNLOADING либо код слота.
    /// </summary>
    /// <param name="node">Узел карты.</param>
    /// <returns><c>true</c>, если к узлу применяются вилы.</returns>
    public static bool IsWorking(WarehouseNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return IsSlot(node) || Contains(ForkTypes, node.NodeType);
    }

    /// <summary>
    /// Узел является стеллажным слотом по коду (<c>wo-*</c>).
    /// </summary>
    /// <param name="node">Узел карты.</param>
    /// <returns><c>true</c>, если код узла начинается с <c>wo-</c>.</returns>
    public static bool IsSlot(WarehouseNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return node.NodeId.StartsWith(SlotPrefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Узел отдает курс в <c>nodePosition</c>: как в AGVControl, только LOADING и UNLOADING.
    /// </summary>
    /// <param name="node">Узел карты.</param>
    /// <returns><c>true</c>, если поле <c>theta</c> узла задается.</returns>
    public static bool HasTheta(WarehouseNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return Contains(ThetaTypes, node.NodeType);
    }

    /// <summary>
    /// Точка остановки перед узлом: смещение от узла против направления курса.
    /// </summary>
    /// <param name="node">Узел карты.</param>
    /// <param name="theta">Курс робота на узле, радианы.</param>
    /// <returns>Координаты точки остановки «передом»: <c>N - 1.475*(cos, sin)</c>.</returns>
    public static (double X, double Y) Front(WarehouseNode node, double theta)
    {
        ArgumentNullException.ThrowIfNull(node);
        return (node.X - (WrkLength * Math.Cos(theta)), node.Y - (WrkLength * Math.Sin(theta)));
    }

    /// <summary>
    /// Точка остановки за узлом: смещение от узла по направлению курса (робот подъезжает кормой).
    /// </summary>
    /// <param name="node">Узел карты.</param>
    /// <param name="theta">Курс робота на узле, радианы.</param>
    /// <returns>Координаты точки остановки «задом»: <c>N + 1.475*(cos, sin)</c>.</returns>
    public static (double X, double Y) Rear(WarehouseNode node, double theta)
    {
        ArgumentNullException.ThrowIfNull(node);
        return (node.X + (WrkLength * Math.Cos(theta)), node.Y + (WrkLength * Math.Sin(theta)));
    }

    /// <summary>
    /// Точка остановки робота у рабочего узла: из двух смещений берется ближайшее
    /// к точке подхода, чтобы робот не объезжал стеллаж.
    /// </summary>
    /// <param name="node">Рабочий узел.</param>
    /// <param name="theta">Курс подхода к узлу, радианы.</param>
    /// <param name="approachX">Координата X точки подхода.</param>
    /// <param name="approachY">Координата Y точки подхода.</param>
    /// <returns>Координаты точки остановки.</returns>
    public static (double X, double Y) StopPoint(
        WarehouseNode node,
        double theta,
        double approachX,
        double approachY)
    {
        ArgumentNullException.ThrowIfNull(node);

        var front = Front(node, theta);
        var rear = Rear(node, theta);
        var frontDistance = Distance(front.X, front.Y, approachX, approachY);
        var rearDistance = Distance(rear.X, rear.Y, approachX, approachY);
        return frontDistance <= rearDistance ? front : rear;
    }

    /// <summary>
    /// Курс от точки подхода к узлу. Если точки совпали, курс нулевой.
    /// </summary>
    /// <param name="fromX">Координата X точки подхода.</param>
    /// <param name="fromY">Координата Y точки подхода.</param>
    /// <param name="toX">Координата X узла.</param>
    /// <param name="toY">Координата Y узла.</param>
    /// <returns>Угол в радианах, <c>0</c> при совпадении точек.</returns>
    public static double Course(double fromX, double fromY, double toX, double toY)
    {
        var deltaX = toX - fromX;
        var deltaY = toY - fromY;
        return Distance(fromX, fromY, toX, toY) < 1e-9 ? 0 : Math.Atan2(deltaY, deltaX);
    }

    private static double Distance(double x1, double y1, double x2, double y2) =>
        Math.Sqrt(Math.Pow(x2 - x1, 2) + Math.Pow(y2 - y1, 2));

    private static bool Contains(string[] values, string? value) =>
        value is not null && Array.Exists(values, item => string.Equals(item, value, StringComparison.OrdinalIgnoreCase));
}
