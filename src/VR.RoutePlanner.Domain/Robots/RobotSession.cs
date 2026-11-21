namespace VR.RoutePlanner.Domain.Robots;

/// <summary>
/// Последнее состояние робота из таблицы <c>vrt_RobotSession</c>.
/// </summary>
/// <param name="AgvSerialNumber">Серийный номер робота.</param>
/// <param name="Manufacturer">Производитель из сообщения.</param>
/// <param name="ProtocolVersion">Версия протокола.</param>
/// <param name="ConnectionState">Состояние связи.</param>
/// <param name="LastSeenAt">Момент последнего сообщения, UTC.</param>
/// <param name="StateJson">Тело последнего сообщения <c>state</c>.</param>
/// <param name="UpdatedAt">Момент записи строки, UTC.</param>
public sealed record RobotSession(
    string AgvSerialNumber,
    string? Manufacturer,
    string? ProtocolVersion,
    string? ConnectionState,
    DateTimeOffset LastSeenAt,
    string? StateJson,
    DateTimeOffset UpdatedAt);

/// <summary>
/// Статусы заказа роботу из <c>vrt_RobotOrder.Status</c>.
/// </summary>
public static class RobotOrderStatuses
{
    /// <summary>Черновик.</summary>
    public const string Draft = "Draft";

    /// <summary>Отправлен роботу.</summary>
    public const string Sent = "Sent";

    /// <summary>Принят роботом.</summary>
    public const string Accepted = "Accepted";

    /// <summary>Выполняется.</summary>
    public const string Executing = "Executing";

    /// <summary>Выполнен.</summary>
    public const string Completed = "Completed";

    /// <summary>Отклонен роботом.</summary>
    public const string Rejected = "Rejected";

    /// <summary>Провален.</summary>
    public const string Failed = "Failed";

    /// <summary>Отменен.</summary>
    public const string Cancelled = "Cancelled";
}

/// <summary>
/// Заказ роботу из таблицы <c>vrt_RobotOrder</c>.
/// </summary>
/// <param name="OrderId">Идентификатор заказа вида <c>ORD_04330</c>.</param>
/// <param name="AgvSerialNumber">Серийный номер робота.</param>
/// <param name="OrderUpdateId">Версия заказа для робота.</param>
/// <param name="Status">Статус из <see cref="RobotOrderStatuses"/>.</param>
/// <param name="PlanJson">Тело заказа или план маршрута.</param>
/// <param name="ReleasedNodeCount">Сколько узлов выпущено роботу.</param>
/// <param name="TargetNodeId">Целевой узел.</param>
/// <param name="ApproachNodeId">Узел подхода.</param>
/// <param name="CreatedAt">Момент создания, UTC.</param>
/// <param name="SentAt">Момент отправки, UTC.</param>
/// <param name="AcceptedAt">Момент подтверждения, UTC.</param>
/// <param name="CompletedAt">Момент завершения, UTC.</param>
/// <param name="FailureReason">Причина отказа или провала.</param>
/// <param name="Nodes">Узлы заказа.</param>
public sealed record RobotOrder(
    string OrderId,
    string AgvSerialNumber,
    int OrderUpdateId,
    string Status,
    string? PlanJson,
    int ReleasedNodeCount,
    string? TargetNodeId,
    string? ApproachNodeId,
    DateTimeOffset CreatedAt,
    DateTimeOffset? SentAt,
    DateTimeOffset? AcceptedAt,
    DateTimeOffset? CompletedAt,
    string? FailureReason,
    IReadOnlyList<RobotOrderNode> Nodes);

/// <summary>
/// Узел заказа из таблицы <c>vrt_RobotOrderNode</c>.
/// </summary>
/// <param name="OrderId">Идентификатор заказа.</param>
/// <param name="SequenceId">Порядковый номер узла.</param>
/// <param name="NodeId">Идентификатор узла карты.</param>
/// <param name="Released">Узел выпущен роботу.</param>
/// <param name="Reached">Узел достигнут.</param>
/// <param name="ActionCount">Число действий в узле.</param>
public sealed record RobotOrderNode(
    string OrderId,
    int SequenceId,
    string NodeId,
    bool Released,
    bool Reached,
    int ActionCount);
