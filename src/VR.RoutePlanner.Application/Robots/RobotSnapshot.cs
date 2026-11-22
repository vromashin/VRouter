namespace VR.RoutePlanner.Application.Robots;

/// <summary>
/// Узел маршрута из сообщения <c>state</c> VDA 5050.
/// </summary>
/// <param name="NodeId">Идентификатор узла.</param>
/// <param name="SequenceId">Порядковый номер узла в заказе.</param>
/// <param name="Released">Узел пройден и освобожден.</param>
public sealed record RobotNodeState(string NodeId, int SequenceId, bool Released);

/// <summary>
/// Последнее известное состояние робота из топика <c>state</c> VDA 5050.
/// </summary>
/// <param name="SerialNumber">Серийный номер робота из сообщения.</param>
/// <param name="ConnectionState">Состояние связи: <c>ONLINE</c> для полученного сообщения.</param>
/// <param name="Driving">Робот в движении.</param>
/// <param name="OrderId">Идентификатор заказа либо <c>null</c>.</param>
/// <param name="OrderUpdateId">Версия заказа.</param>
/// <param name="PositionX">Координата X, метры.</param>
/// <param name="PositionY">Координата Y, метры.</param>
/// <param name="Theta">Ориентация, радианы.</param>
/// <param name="MapId">Идентификатор карты.</param>
/// <param name="BatteryCharge">Заряд батареи, проценты.</param>
/// <param name="Errors">Ошибки робота в текстовом виде.</param>
/// <param name="NodeStates">Состояния узлов маршрута.</param>
/// <param name="ReceivedAt">Момент получения сообщения сервисом.</param>
/// <param name="Version">Версия протокола из сообщения.</param>
/// <param name="LastNodeId">Последний узел маршрута из сообщения.</param>
/// <param name="LastNodeSequenceId">Номер последнего узла в плане.</param>
/// <summary>Состояние действия робота из state.actionStates.</summary>
/// <param name="ActionId">Идентификатор действия.</param>
/// <param name="ActionType">Тип действия.</param>
/// <param name="ActionStatus">Статус: WAITING, INITIALIZING, RUNNING, FINISHED, FAILED.</param>
public sealed record RobotActionState(string ActionId, string? ActionType, string? ActionStatus);

public sealed record RobotSnapshot(
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
    IReadOnlyList<RobotNodeState> NodeStates,
    DateTimeOffset ReceivedAt,
    string Version,
    string? LastNodeId = null,
    int? LastNodeSequenceId = null,
    bool NewBaseRequest = false,
    IReadOnlyList<RobotActionState>? ActionStates = null);
