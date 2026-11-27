using VR.RoutePlanner.Domain.Graph;
using VR.RoutePlanner.Domain.Planning;

namespace VR.RoutePlanner.Application.Robots;

/// <summary>
/// Запрос сборки тела VDA-заказа: маршрут, граф и границы отправляемого куска.
/// Заменяет прежний набор необязательных параметров <c>VdaOrderBuilder.Build</c>.
///
/// Действия вил задаются явными типами: <see cref="AnchorActionType"/> — на узле-якоре
/// (сообщение начинается на узле, где действие выполняется), <see cref="TargetActionType"/> —
/// в последнем узле плана. Тип не выводится из типа узла или наличия груза: на узле-якоре
/// нужен <c>pick</c>, а <c>HasLoad = true</c> дал бы <c>drop</c>.
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
/// <param name="FirstNodeTheta">Отдавать ли курс в первом узле первого сообщения.</param>
/// <param name="RobotX">Координата X робота, если поза известна.</param>
/// <param name="RobotY">Координата Y робота, если поза известна.</param>
/// <param name="RobotTheta">Курс робота, если поза известна.</param>
/// <param name="TerminalActionType">Тип завершающего действия в последнем узле плана.</param>
/// <param name="ApproachTarget">Целевой рабочий узел, не попавший в маршрут (точка подхода).</param>
/// <param name="AnchorNode">
/// Узел-якорь: сообщение начинается на узле, где выполняется действие. План получает тройку
/// «узел-якорь без курса — ребро <c>VIRTUAL_START_&lt;nodeId&gt;</c> — дубль узла с курсом
/// к паллете и действием», затем маршрут. Маршрут может быть пустым: тогда в сообщении только
/// тройка (робот уже стоит в узле-якоре).
/// </param>
/// <param name="AnchorActionType">Тип действия вил на дубле узла-якоря; <c>null</c> — без действия.</param>
/// <param name="TargetActionType">Тип действия вил в последнем узле плана; <c>null</c> — без действия.</param>
/// <param name="SequenceIdBase">
/// База нумерации: узел <c>SequenceIdBase + index*2</c>, ребро <c>+1</c>. Первое сообщение
/// заказа — <c>0</c>, первое сообщение плеча N — <c>sequenceId</c> последнего узла предыдущего сообщения.
/// </param>
/// <param name="FirstNodeIsPose">
/// Первый узел сообщения получает фактическую позу робота и допуски 0.3/3.14.
/// <c>true</c> только для первого сообщения задачи; сообщение плеча N начинается узлом-якорем
/// с координатами карты и без курса.
/// </param>
/// <param name="TargetNodeActionType">
/// Действие в последнем узле плана без вилочных параметров: <c>startCharging</c> у узла
/// <c>CHARGING</c>. Ставится рядом с <see cref="TerminalActionType"/> и не меняет поведение вил.
/// </param>
/// <param name="TargetNodeActionBlockingType">
/// Блокирующий тип действия целевого узла; <c>null</c> — <c>SOFT</c>.
/// </param>
/// <param name="TargetNodeActionParameters">
/// Параметры действия целевого узла: устройство зарядки и точка стыковки. Вилочные ключи
/// <c>BuildAction</c> здесь не используются.
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
    bool FirstNodeTheta = false,
    double? RobotX = null,
    double? RobotY = null,
    double? RobotTheta = null,
    string? TerminalActionType = null,
    WarehouseNode? ApproachTarget = null,
    WarehouseNode? AnchorNode = null,
    string? AnchorActionType = null,
    string? TargetActionType = null,
    int SequenceIdBase = 0,
    bool FirstNodeIsPose = false,
    string? TargetNodeActionType = null,
    string? TargetNodeActionBlockingType = null,
    IReadOnlyDictionary<string, object?>? TargetNodeActionParameters = null);
