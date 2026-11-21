namespace VR.RoutePlanner.Domain.Planning;

/// <summary>
/// Сегмент маршрута: одно ребро и разложение его цены.
/// </summary>
/// <param name="EdgeCode">Внешний код ребра, например <c>801-wa145-wa146</c>.</param>
/// <param name="Direction">Направление прохождения.</param>
/// <param name="LengthMeters">Длина ребра по геометрии траектории, метры.</param>
/// <param name="SpeedMetersPerSecond">Скорость на ребре по полосе запаса и профилю.</param>
/// <param name="ClearanceMeters">Запас по ширине: ширина минус ширина профиля.</param>
/// <param name="CostSeconds">Полная цена ребра в секундах, включая поворот и ожидание.</param>
/// <param name="CumulativeSeconds">Накопленное время от старта маршрута до конца ребра.</param>
/// <param name="IsNarrow">Запас меньше <c>Routing:Cost:NarrowClearanceMeters</c>.</param>
/// <param name="TrafficResourceId">
/// Ресурс ребра этого профиля и направления: <c>vrt_TrafficResourceEdge.TrafficResourceId</c>.
/// По нему этап 3 резервирует ребро на робота.
/// </param>
/// <param name="Passable">
/// Признак проходимости ребра: у выбранных сегментов всегда <c>true</c>, поле нужно для
/// симметрии с диагностикой.
/// </param>
public sealed record RouteSegment(
    string EdgeCode,
    RouteEdgeDirection Direction,
    double LengthMeters,
    double SpeedMetersPerSecond,
    double ClearanceMeters,
    double CostSeconds,
    double CumulativeSeconds,
    bool IsNarrow,
    Guid TrafficResourceId,
    bool Passable);
