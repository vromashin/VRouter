namespace VR.RoutePlanner.Domain.Graph;

/// <summary>
/// Узел складского графа. Совпадает по смыслу со строкой таблицы <c>public."Nodes"</c>.
/// </summary>
/// <param name="Id">Внутренний ключ БД, по нему связываются ребра.</param>
/// <param name="NodeId">Внешний идентификатор VDA, например <c>wa152</c>.</param>
/// <param name="MapKey">Значение <c>Maps.Id</c>.</param>
/// <param name="X">Координата X, метры.</param>
/// <param name="Y">Координата Y, метры.</param>
/// <param name="Theta">Ориентация, радианы.</param>
/// <param name="AllowedDeviationXY">Допустимое отклонение по XY, метры.</param>
/// <param name="AllowedDeviationTheta">Допустимое отклонение по Theta, радианы.</param>
/// <param name="NodeType">Тип узла: <c>WAYPOINT</c>, <c>WORKING</c>, <c>PARKING</c>, <c>CHARGING</c>.</param>
/// <param name="RackId">Ссылка на стеллаж, если узел привязан к стеллажу.</param>
/// <param name="Z">Высота, метры.</param>
/// <param name="IsActive">Признак активности узла.</param>
public sealed record WarehouseNode(
    Guid Id,
    string NodeId,
    Guid MapKey,
    double X,
    double Y,
    double Theta,
    double? AllowedDeviationXY,
    double? AllowedDeviationTheta,
    string NodeType,
    Guid? RackId,
    double? Z,
    bool IsActive);
