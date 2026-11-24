using VR.RoutePlanner.Domain.Graph;
using VR.RoutePlanner.Domain.Planning;

namespace VR.RoutePlanner.Application.Robots;

/// <summary>
/// Запрос сборки тела VDA-заказа: маршрут, граф и границы отправляемого куска.
/// Заменяет прежний набор необязательных параметров <c>VdaOrderBuilder.Build</c>.
/// </summary>
/// <param name="Route">Построенный маршрут.</param>
/// <param name="Graph">Граф версии карты.</param>
/// <param name="MapId">Карта, например <c>801</c>.</param>
/// <param name="OrderId">Идентификатор заказа вида <c>ORD_04330</c>.</param>
/// <param name="Manufacturer">Производитель робота.</param>
/// <param name="SerialNumber">Серийный номер робота.</param>
/// <param name="HeaderId">Идентификатор сообщения.</param>
/// <param name="Timestamp">Момент сборки.</param>
/// <param name="OrderUpdateId">Версия заказа: <c>0</c> для первого сообщения.</param>
/// <param name="SliceStartIndex">Индекс первого узла куска в полном маршруте.</param>
/// <param name="SliceNodeCount">Число узлов куска; <c>0</c> и меньше — весь маршрут.</param>
/// <param name="SendForkActions">Ставить ли <c>pick</c>/<c>drop</c> на рабочих узлах.</param>
/// <param name="HasLoad">Робот везет груз: подсказка для узла без явного типа.</param>
/// <param name="FirstNodeTheta">Отдавать ли курс в первом узле первого сообщения.</param>
/// <param name="RobotX">Координата X робота, если поза известна.</param>
/// <param name="RobotY">Координата Y робота, если поза известна.</param>
/// <param name="RobotTheta">Курс робота, если поза известна.</param>
/// <param name="TerminalActionType">Тип завершающего действия в последнем узле плана.</param>
/// <param name="ApproachTarget">Целевой рабочий узел, не попавший в маршрут (точка подхода).</param>
/// <param name="StationaryNode">
/// Робот уже стоит в целевом рабочем узле: маршрут пуст, заказ состоит из узла позы и
/// дубля узла с действием вил, связанных виртуальным ребром.
/// </param>
/// <param name="SequenceIdBase">
/// База нумерации: узел <c>SequenceIdBase + index*2</c>, ребро <c>+1</c>. Первое сообщение
/// заказа — <c>0</c>, первое сообщение плеча N — <c>sequenceId</c> последнего узла предыдущего сообщения.
/// </param>
/// <param name="FirstNodeIsPose">
/// Первый узел сообщения получает фактическую позу робота и допуски 0.3/3.14.
/// <c>true</c> для первого сообщения каждого плеча.
/// </param>
public sealed record VdaOrderRequest(
    PlannedRoute Route,
    WarehouseGraph Graph,
    string MapId,
    string OrderId,
    string Manufacturer,
    string SerialNumber,
    long HeaderId,
    DateTimeOffset Timestamp,
    int OrderUpdateId = 0,
    int SliceStartIndex = 0,
    int SliceNodeCount = 0,
    bool SendForkActions = true,
    bool HasLoad = false,
    bool FirstNodeTheta = false,
    double? RobotX = null,
    double? RobotY = null,
    double? RobotTheta = null,
    string? TerminalActionType = null,
    WarehouseNode? ApproachTarget = null,
    WarehouseNode? StationaryNode = null,
    int SequenceIdBase = 0,
    bool FirstNodeIsPose = false);
