namespace VR.RoutePlanner.Domain.Graph;

/// <summary>
/// Ребро складского графа. Совпадает по смыслу со строкой таблицы <c>public."Edges"</c>.
/// </summary>
/// <param name="Id">Внутренний ключ БД, по нему связываются узлы и ресурсы.</param>
/// <param name="EdgeId">Внешний идентификатор VDA, например <c>801-wa152-wa167</c>.</param>
/// <param name="FromNodeId">Ключ узла начала.</param>
/// <param name="ToNodeId">Ключ узла конца.</param>
/// <param name="MapKey">Значение <c>Maps.Id</c>.</param>
/// <param name="Length">Длина из БД в единицах поля: метры = <c>Length * 100</c>.</param>
/// <param name="MaxSpeed">Максимальная скорость, м/с.</param>
/// <param name="Bidirectional">Разрешено движение в обе стороны.</param>
/// <param name="Corridor">Метка коридора из БД, используется как ключ <c>ManualCorridorWidths</c>.</param>
/// <param name="MaxRotationSpeed">Максимальная скорость вращения, рад/с.</param>
/// <param name="Orientation">Ориентация на ребре, радианы.</param>
/// <param name="OrientationType">Тип ориентации: <c>FIXED</c>, <c>TANGENTIAL</c> и т.п.</param>
/// <param name="RotationAllowed">Разрешено ли вращение на ребре.</param>
/// <param name="TrajectoryPoints">Дискретизированная полилиния траектории в метрах.</param>
/// <param name="TrajectoryRaw">Исходный JSON траектории для диагностики.</param>
/// <param name="IsActive">Признак активности ребра.</param>
public sealed record WarehouseEdge(
    Guid Id,
    string EdgeId,
    Guid FromNodeId,
    Guid ToNodeId,
    Guid MapKey,
    double Length,
    double MaxSpeed,
    bool Bidirectional,
    string? Corridor,
    double? MaxRotationSpeed,
    double? Orientation,
    string? OrientationType,
    bool? RotationAllowed,
    IReadOnlyList<MapPoint> TrajectoryPoints,
    string? TrajectoryRaw,
    bool IsActive);
