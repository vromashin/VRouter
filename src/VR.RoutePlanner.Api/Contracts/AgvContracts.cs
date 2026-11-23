using VR.RoutePlanner.Domain.Robots;

namespace VR.RoutePlanner.Api.Contracts;

/// <summary>
/// Тело <c>POST /api/agv/{serial}/order</c>.
/// </summary>
/// <param name="MapId">Карта.</param>
/// <param name="TargetNodeId">Целевой узел.</param>
/// <param name="HasLoad">Робот везет груз.</param>
public sealed record AgvOrderBody(string? MapId, string? TargetNodeId, bool HasLoad);

/// <summary>
/// Тело <c>POST /api/agv/{serial}/instant-action</c>.
/// </summary>
/// <param name="ActionType">Тип действия.</param>
/// <param name="ActionId">Идентификатор действия; не задан — генерируется.</param>
public sealed record AgvInstantActionBody(string? ActionType, string? ActionId);

/// <summary>
/// Ответ на создание заказа.
/// </summary>
/// <param name="OrderId">Идентификатор заказа.</param>
/// <param name="Status">Статус.</param>
/// <param name="OrderUpdateId">Версия заказа.</param>
/// <param name="Nodes">Узлы заказа.</param>
public sealed record AgvOrderResponse(
    string OrderId,
    string Status,
    int OrderUpdateId,
    IReadOnlyList<AgvOrderNodeResponse> Nodes);

/// <summary>
/// Узел заказа в ответе.
/// </summary>
/// <param name="SequenceId">Порядковый номер.</param>
/// <param name="NodeId">Узел карты.</param>
/// <param name="Released">Выпущен роботу.</param>
/// <param name="Reached">Достигнут.</param>
public sealed record AgvOrderNodeResponse(int SequenceId, string NodeId, bool Released, bool Reached);

/// <summary>
/// Состояние робота в ответе.
/// </summary>
/// <param name="SerialNumber">Серийный номер.</param>
/// <param name="ConnectionState">Состояние связи.</param>
/// <param name="Driving">Робот едет.</param>
/// <param name="OrderId">Текущий заказ.</param>
/// <param name="OrderUpdateId">Версия заказа.</param>
/// <param name="PositionX">Координата X.</param>
/// <param name="PositionY">Координата Y.</param>
/// <param name="Theta">Ориентация.</param>
/// <param name="MapId">Карта.</param>
/// <param name="BatteryCharge">Заряд.</param>
/// <param name="Errors">Ошибки.</param>
/// <param name="ReceivedAt">Момент получения состояния.</param>
public sealed record AgvStateResponse(
    string SerialNumber,
    string ConnectionState,
    bool Driving,
    string? OrderId,
    int OrderUpdateId,
    double PositionX,
    double PositionY,
    double Theta,
    string? MapId,
    double BatteryCharge,
    IReadOnlyList<string> Errors,
    DateTimeOffset ReceivedAt);

/// <summary>
/// Ответ на мгновенное действие.
/// </summary>
/// <param name="ActionId">Идентификатор действия.</param>
/// <param name="Accepted">Действие отправлено.</param>
public sealed record AgvInstantActionResponse(string ActionId, bool Accepted);

/// <summary>
/// Ответ с кодом причины.
/// </summary>
/// <param name="Code">Код причины.</param>
/// <param name="Message">Текст.</param>
public sealed record AgvProblemResponse(string Code, string Message);

/// <summary>
/// Узел карты в ответе <c>GET /api/AgvTask/lastnode/{serial}</c>.
/// Форма совпадает с <c>AGVModels.Def.Node</c> старого AGVControl.
/// </summary>
/// <param name="Id">Внутренний ключ узла.</param>
/// <param name="NodeId">Внешний идентификатор VDA.</param>
/// <param name="NodeDescription">Описание; всегда пустой объект <c>{}</c>.</param>
/// <param name="MapId">Ключ карты.</param>
/// <param name="PositionX">Координата X, метры.</param>
/// <param name="PositionY">Координата Y, метры.</param>
/// <param name="Theta">Ориентация, радианы.</param>
/// <param name="AllowedDeviationXY">Допуск по XY, метры.</param>
/// <param name="AllowedDeviationTheta">Допуск по курсу, радианы.</param>
/// <param name="NodeType">Тип узла.</param>
/// <param name="CreatedDate">Дата создания; в карте не хранится.</param>
/// <param name="UpdatedDate">Дата изменения; в карте не хранится.</param>
/// <param name="CreatedBy">Автор создания; в карте не хранится.</param>
/// <param name="ModifiedBy">Автор изменения; в карте не хранится.</param>
public sealed record AgvNodeResponse(
    Guid Id,
    string NodeId,
    string NodeDescription,
    Guid MapId,
    double PositionX,
    double PositionY,
    double Theta,
    double? AllowedDeviationXY,
    double? AllowedDeviationTheta,
    string NodeType,
    DateTimeOffset? CreatedDate,
    DateTimeOffset? UpdatedDate,
    Guid? CreatedBy,
    Guid? ModifiedBy);

/// <summary>
/// Отображение доменных заказов в DTO.
/// </summary>
public static class AgvDtoMapper
{
    /// <summary>Заказ в ответ.</summary>
    /// <param name="order">Заказ.</param>
    /// <returns>DTO.</returns>
    public static AgvOrderResponse ToResponse(RobotOrder order)
    {
        ArgumentNullException.ThrowIfNull(order);
        return new AgvOrderResponse(
            order.OrderId,
            order.Status,
            order.OrderUpdateId,
            order.Nodes
                .Select(node => new AgvOrderNodeResponse(node.SequenceId, node.NodeId, node.Released, node.Reached))
                .ToList());
    }

    /// <summary>Состояние робота в ответ.</summary>
    /// <param name="snapshot">Снимок.</param>
    /// <returns>DTO.</returns>
    public static AgvStateResponse ToResponse(Application.Robots.RobotSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new AgvStateResponse(
            snapshot.SerialNumber,
            snapshot.ConnectionState,
            snapshot.Driving,
            snapshot.OrderId,
            snapshot.OrderUpdateId,
            snapshot.PositionX,
            snapshot.PositionY,
            snapshot.Theta,
            snapshot.MapId,
            snapshot.BatteryCharge,
            snapshot.Errors,
            snapshot.ReceivedAt);
    }
}
