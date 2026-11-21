namespace VR.RoutePlanner.Application.Planning;

/// <summary>
/// Запрос на построение маршрута.
/// </summary>
/// <param name="MapId">Внешний идентификатор карты, например <c>801</c>.</param>
/// <param name="Profile">Имя профиля робота из <c>Routing:AgvProfiles</c>.</param>
/// <param name="FromNodeId">Внутренний ключ узла старта.</param>
/// <param name="ToNodeId">Внутренний ключ узла цели.</param>
/// <param name="HasLoad">Робот перевозит груз: скорость умножается на <c>LoadSpeedFactor</c>.</param>
public sealed record RoutePlanRequest(
    string MapId,
    string Profile,
    Guid FromNodeId,
    Guid ToNodeId,
    bool HasLoad);
