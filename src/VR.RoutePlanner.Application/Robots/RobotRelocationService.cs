using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VR.RoutePlanner.Application.Configuration;
using VR.RoutePlanner.Application.Graph;
using VR.RoutePlanner.Domain.Graph;
using VR.RoutePlanner.Domain.Robots;

namespace VR.RoutePlanner.Application.Robots;

/// <summary>
/// Итог автопереезда: заказ, мгновенное действие либо отказ с кодом в <see cref="Message"/>.
/// </summary>
/// <param name="Success">Автопереезд принят.</param>
/// <param name="Message">Текст для админки; у отказа начинается с кода.</param>
/// <param name="OrderId">Созданный заказ либо <c>null</c>.</param>
/// <param name="OrderUpdateId">Версия заказа.</param>
/// <param name="ActionId">Мгновенное действие либо <c>null</c>.</param>
/// <param name="TargetNodeId">Целевой узел перемещения либо узел, на котором робот уже стоит.</param>
public sealed record RobotRelocationResult(
    bool Success,
    string Message,
    string? OrderId = null,
    int OrderUpdateId = 0,
    string? ActionId = null,
    string? TargetNodeId = null)
{
    /// <summary>Создан заказ до узла.</summary>
    /// <param name="order">Заказ.</param>
    /// <param name="nodeId">Целевой узел.</param>
    /// <returns>Успешный итог с заказом.</returns>
    public static RobotRelocationResult Order(RobotOrder order, string nodeId)
    {
        ArgumentNullException.ThrowIfNull(order);
        return new RobotRelocationResult(
            true,
            $"Заказ {nodeId} принят",
            order.OrderId,
            order.OrderUpdateId,
            null,
            nodeId);
    }

    /// <summary>Заказ не нужен: отправлено мгновенное действие.</summary>
    /// <param name="actionId">Идентификатор действия.</param>
    /// <param name="nodeId">Узел, на котором стоит робот.</param>
    /// <returns>Успешный итог с действием.</returns>
    public static RobotRelocationResult Immediate(string actionId, string nodeId) =>
        new(true, $"Робот уже на узле {nodeId}", null, 0, actionId, nodeId);

    /// <summary>Заказ не нужен и действий нет: робот уже на узле парковки.</summary>
    /// <param name="nodeId">Узел, на котором стоит робот.</param>
    /// <returns>Успешный итог без заказа и без действия.</returns>
    public static RobotRelocationResult Stayed(string nodeId) =>
        new(true, $"Робот уже на узле {nodeId}", null, 0, null, nodeId);

    /// <summary>Отказ без заказа и без действия.</summary>
    /// <param name="message">Текст отказа, у кодов из Д-8 совпадает с кодом.</param>
    /// <returns>Неуспешный итог.</returns>
    public static RobotRelocationResult Failure(string message) => new(false, message);
}

/// <summary>
/// Автопереезды робота на зарядку и парковку: выбор свободного узла, создание обычного
/// VDA-заказа через <see cref="RobotOrderService"/> и действие <c>startCharging</c> в целевом
/// узле <c>CHARGING</c>. Резервы узлов живут в памяти процесса (<see cref="RelocationOccupancy"/>),
/// авторитетная занятость — <c>vrt_RobotOrder.TargetNodeId</c> плюс live-состояние робота.
/// </summary>
public sealed class RobotRelocationService
{
    /// <summary>Код отказа: узлов <c>CHARGING</c> нет на карте.</summary>
    public const string CodeChargeNodeNotFound = "CHARGE_NODE_NOT_FOUND";

    /// <summary>Код отказа: узлов <c>PARKING</c> нет на карте.</summary>
    public const string CodeParkingNodeNotFound = "PARKING_NODE_NOT_FOUND";

    /// <summary>Код отказа: указанный узел не найден либо его тип не <c>CHARGING</c>.</summary>
    public const string CodeNodeNotCharging = "NODE_NOT_CHARGING";

    /// <summary>Код отказа: указанный узел не найден либо его тип не <c>PARKING</c>.</summary>
    public const string CodeNodeNotParking = "NODE_NOT_PARKING";

    /// <summary>Код отказа: все узлы <c>CHARGING</c> заняты.</summary>
    public const string CodeChargeNodeBusy = "CHARGE_NODE_BUSY";

    /// <summary>Код отказа: все узлы <c>PARKING</c> заняты.</summary>
    public const string CodeParkingNodeBusy = "PARKING_NODE_BUSY";

    /// <summary>
    /// Код отказа, которого нет в Д-8: робот ещё не публиковал <c>state</c>, поза неизвестна.
    /// Заказ в этом случае не создается: непонятно, от какого узла строить маршрут.
    /// </summary>
    public const string CodeStateNotFound = "AGV_STATE_NOT_FOUND";

    private readonly IWarehouseGraphRepository _graphs;
    private readonly IRobotOrderRepository _orders;
    private readonly IRobotStateReader _states;
    private readonly RobotOrderService _orderService;
    private readonly IRobotEventPublisher _events;
    private readonly IOptions<ChargingOptions> _charging;
    private readonly IOptions<RoutePlannerOptions> _routing;
    private readonly TimeProvider _time;
    private readonly ILogger<RobotRelocationService> _logger;

    /// <summary>Резервы узлов процесса: ключ это <c>NodeId</c>, значение — робот и момент резерва.</summary>
    private readonly ConcurrentDictionary<string, NodeReservation> _reservations = new(StringComparer.Ordinal);

    /// <summary>
    /// Создает сервис автопереездов.
    /// </summary>
    /// <param name="graphs">Чтение графа карты.</param>
    /// <param name="orders">Хранилище заказов: авторитетная занятость узлов.</param>
    /// <param name="states">Последние состояния роботов.</param>
    /// <param name="orderService">Создание заказов и мгновенных действий.</param>
    /// <param name="events">Публикация события завершения заряда.</param>
    /// <param name="charging">Параметры <c>Routing:Charging</c>.</param>
    /// <param name="routing">Параметры <c>Routing</c>: габариты робота для точки зарядки.</param>
    /// <param name="time">Источник времени.</param>
    /// <param name="logger">Журнал.</param>
    public RobotRelocationService(
        IWarehouseGraphRepository graphs,
        IRobotOrderRepository orders,
        IRobotStateReader states,
        RobotOrderService orderService,
        IRobotEventPublisher events,
        IOptions<ChargingOptions> charging,
        IOptions<RoutePlannerOptions> routing,
        TimeProvider time,
        ILogger<RobotRelocationService> logger)
    {
        ArgumentNullException.ThrowIfNull(graphs);
        ArgumentNullException.ThrowIfNull(orders);
        ArgumentNullException.ThrowIfNull(states);
        ArgumentNullException.ThrowIfNull(orderService);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(charging);
        ArgumentNullException.ThrowIfNull(routing);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);

        _graphs = graphs;
        _orders = orders;
        _states = states;
        _orderService = orderService;
        _events = events;
        _charging = charging;
        _routing = routing;
        _time = time;
        _logger = logger;
    }

    /// <summary>Резервы узлов процесса: <c>NodeId</c> → серийный номер робота.</summary>
    public IReadOnlyDictionary<string, string> Reservations => _reservations
        .ToDictionary(pair => pair.Key, pair => pair.Value.SerialNumber, StringComparer.Ordinal);

    /// <summary>Карта робота: <c>snapshot.MapId</c>, при пустом — <c>Routing:Charging:MapId</c>.</summary>
    /// <param name="snapshot">Состояние робота.</param>
    /// <returns>Идентификатор карты.</returns>
    public string ResolveMapId(RobotSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return string.IsNullOrWhiteSpace(snapshot.MapId) ? _charging.Value.MapId : snapshot.MapId!;
    }

    /// <summary>Загружает граф карты робота.</summary>
    /// <param name="snapshot">Состояние робота.</param>
    /// <param name="ct">Токен отмены.</param>
    /// <returns>Граф либо <c>null</c>, если карты нет.</returns>
    public Task<WarehouseGraph?> LoadGraphAsync(RobotSnapshot snapshot, CancellationToken ct) =>
        LoadMapAsync(ResolveMapId(snapshot), ct);

    /// <summary>
    /// Собирает занятость узлов из поз роботов и активных заказов. Резервы процесса добавляются
    /// при выборе узла, поэтому в пакетном тике свежие резервы видны следующему роботу.
    /// </summary>
    /// <param name="states">Последние состояния роботов.</param>
    /// <param name="activeOrders">Активные заказы из <c>vrt_RobotOrder</c>.</param>
    /// <returns>Занятость узлов.</returns>
    public RelocationOccupancy BuildOccupancy(
        IReadOnlyCollection<RobotSnapshot> states,
        IReadOnlyCollection<RobotOrder> activeOrders)
    {
        ArgumentNullException.ThrowIfNull(states);
        ArgumentNullException.ThrowIfNull(activeOrders);

        var poses = states
            .Select(snapshot => new RelocationRobotPose(
                snapshot.SerialNumber,
                snapshot.LastNodeId,
                snapshot.PositionX,
                snapshot.PositionY))
            .ToList();

        var targets = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var order in activeOrders)
        {
            if (!string.IsNullOrWhiteSpace(order.TargetNodeId))
            {
                targets.TryAdd(order.TargetNodeId!, order.AgvSerialNumber);
            }
        }

        return new RelocationOccupancy
        {
            Poses = poses,
            ActiveOrderTargets = targets,
            OccupiedRadiusMeters = _charging.Value.OccupiedRadiusMeters,
        };
    }

    /// <summary>Автопереезд на зарядку для HTTP: <c>POST /api/Order/move-to-charge/{serial}[/{nodeId}]</c>.</summary>
    /// <param name="serialNumber">Серийный номер робота.</param>
    /// <param name="nodeId">Явно заданный узел <c>CHARGING</c> либо <c>null</c>: ближайший свободный.</param>
    /// <param name="ct">Токен отмены.</param>
    /// <returns>Итог автопереезда.</returns>
    public Task<RobotRelocationResult> MoveToChargeAsync(
        string serialNumber,
        string? nodeId,
        CancellationToken ct) =>
        MoveAsync(serialNumber, RelocationNodeTypes.Charging, nodeId, ct);

    /// <summary>Автопереезд на парковку для HTTP: <c>POST /api/Order/move-to-parking/{serial}[/{nodeId}]</c>.</summary>
    /// <param name="serialNumber">Серийный номер робота.</param>
    /// <param name="nodeId">Явно заданный узел <c>PARKING</c> либо <c>null</c>: ближайший свободный.</param>
    /// <param name="ct">Токен отмены.</param>
    /// <returns>Итог автопереезда.</returns>
    public Task<RobotRelocationResult> MoveToParkingAsync(
        string serialNumber,
        string? nodeId,
        CancellationToken ct) =>
        MoveAsync(serialNumber, RelocationNodeTypes.Parking, nodeId, ct);

    /// <summary>
    /// Создает заказ на ближайший свободный узел нужного типа либо на явно заданный узел.
    /// Робот, который уже стоит на узле этого типа, не двигается: для зарядки уходит
    /// мгновенное действие <c>startCharging</c>, для парковки — просто успех.
    /// </summary>
    /// <param name="snapshot">Состояние робота.</param>
    /// <param name="nodeType">Тип узла: <c>CHARGING</c> либо <c>PARKING</c>.</param>
    /// <param name="explicitNodeId">Узел из пути запроса либо <c>null</c>.</param>
    /// <param name="occupancy">Занятость узлов на момент решения.</param>
    /// <param name="ct">Токен отмены.</param>
    /// <returns>Итог автопереезда.</returns>
    public async Task<RobotRelocationResult> TryRelocateAsync(
        RobotSnapshot snapshot,
        string nodeType,
        string? explicitNodeId,
        RelocationOccupancy occupancy,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeType);
        ArgumentNullException.ThrowIfNull(occupancy);

        var isCharging = string.Equals(nodeType, RelocationNodeTypes.Charging, StringComparison.Ordinal);
        var notFoundCode = isCharging ? CodeChargeNodeNotFound : CodeParkingNodeNotFound;
        var notTypeCode = isCharging ? CodeNodeNotCharging : CodeNodeNotParking;
        var busyCode = isCharging ? CodeChargeNodeBusy : CodeParkingNodeBusy;
        var tolerance = _charging.Value.IdleNodeToleranceMeters;

        var mapId = ResolveMapId(snapshot);
        var graph = await LoadMapAsync(mapId, ct).ConfigureAwait(false);
        if (graph is null)
        {
            return RobotRelocationResult.Failure(notFoundCode);
        }

        // Робот уже на узле нужного типа: не двигаем, для зарядки шлем startCharging (Д-8.1).
        var current = RobotRelocationStateMachine.NodeOfType(graph, snapshot, nodeType, tolerance);
        if (current is not null)
        {
            return isCharging
                ? await StartChargingAsync(snapshot.SerialNumber, current.NodeId, ct).ConfigureAwait(false)
                : RobotRelocationResult.Stayed(current.NodeId);
        }

        var effective = WithReservations(occupancy);
        var typeNodes = graph.Nodes.Values
            .Where(node => string.Equals(node.NodeType, nodeType, StringComparison.Ordinal))
            .ToList();
        if (typeNodes.Count == 0)
        {
            _logger.LogWarning(
                "Робот {Serial}: на карте {Map} нет узлов типа {Type}",
                snapshot.SerialNumber,
                mapId,
                nodeType);
            return RobotRelocationResult.Failure(notFoundCode);
        }

        WarehouseNode? target;
        if (!string.IsNullOrWhiteSpace(explicitNodeId))
        {
            target = RobotRelocationStateMachine.FindNode(graph, explicitNodeId);
            if (target is null || !string.Equals(target.NodeType, nodeType, StringComparison.Ordinal))
            {
                _logger.LogWarning(
                    "Робот {Serial}: узел {Node} не найден на карте {Map} или его тип не {Type}",
                    snapshot.SerialNumber,
                    explicitNodeId,
                    mapId,
                    nodeType);
                return RobotRelocationResult.Failure(notTypeCode);
            }

            if (effective.IsOccupied(target, snapshot.SerialNumber))
            {
                _logger.LogInformation(
                    "Робот {Serial}: узел {Node} занят",
                    snapshot.SerialNumber,
                    target.NodeId);
                return RobotRelocationResult.Failure(busyCode);
            }
        }
        else
        {
            var pose = new RelocationRobotPose(
                snapshot.SerialNumber,
                snapshot.LastNodeId,
                snapshot.PositionX,
                snapshot.PositionY);
            target = RelocationTargetSelector.Select(typeNodes, nodeType, pose, effective);
            if (target is null)
            {
                _logger.LogInformation(
                    "Робот {Serial}: все узлы {Type} на карте {Map} заняты",
                    snapshot.SerialNumber,
                    nodeType,
                    mapId);
                return RobotRelocationResult.Failure(busyCode);
            }
        }

        // Авторитетная перепроверка перед созданием заказа (правило (б) Д-3.2): между тиком и
        // выдачей заказ мог появиться из другого процесса.
        var active = await _orders.GetActiveAsync(ct).ConfigureAwait(false);
        if (active.Any(order => string.Equals(order.TargetNodeId, target.NodeId, StringComparison.Ordinal)
            && !string.Equals(order.AgvSerialNumber, snapshot.SerialNumber, StringComparison.Ordinal)))
        {
            _logger.LogInformation(
                "Робот {Serial}: узел {Node} уже занят активным заказом другого робота",
                snapshot.SerialNumber,
                target.NodeId);
            return RobotRelocationResult.Failure(busyCode);
        }

        // Заказ на зарядку — обычное одиночное плечо без вил, действие startCharging стоит
        // в целевом узле (Д-4). Для PARKING действий в узле нет.
        var parameters = isCharging
            ? ChargingNodeAction.Parameters(target, DefaultAgvLengthMeters(), _charging.Value.ChargerId)
            : null;

        try
        {
            var order = await _orderService
                .CreateTaskOrderAsync(
                    mapId,
                    snapshot.SerialNumber,
                    target.NodeId,
                    hasLoad: false,
                    target.NodeId,
                    targetActionType: null,
                    ct,
                    targetNodeActionType: isCharging ? ChargingNodeAction.ActionType : null,
                    targetNodeActionBlockingType: isCharging ? ChargingNodeAction.BlockingType : null,
                    targetNodeActionParameters: parameters)
                .ConfigureAwait(false);

            _reservations[target.NodeId] = new NodeReservation(snapshot.SerialNumber, _time.GetUtcNow());
            _logger.LogInformation(
                "Робот {Serial}: заказ {OrderId} на узел {Node} ({Type})",
                snapshot.SerialNumber,
                order.OrderId,
                target.NodeId,
                nodeType);
            return RobotRelocationResult.Order(order, target.NodeId);
        }
        catch (OrderInProgressException exception)
        {
            _logger.LogInformation(
                "Робот {Serial}: автопереезд не создан, активен заказ {OrderId} в статусе {Status}",
                snapshot.SerialNumber,
                exception.OrderId,
                exception.Status);
            return RobotRelocationResult.Failure(exception.Message);
        }
        catch (OrderRejectedException exception)
        {
            _logger.LogInformation(
                "Робот {Serial}: заказ на узел {Node} отклонен: {Reason} {Message}",
                snapshot.SerialNumber,
                target.NodeId,
                exception.Reason,
                exception.Message);
            return RobotRelocationResult.Failure($"{exception.Reason}: {exception.Message}");
        }
    }

    /// <summary>
    /// Публикует событие <c>agv-charging-completed</c>, если робот заряжается и пересек
    /// <c>Routing:Charging:TargetLimit</c>. Защёлку «событие уже отправлено» держит вызывающий:
    /// сервис публикует событие при каждом вызове, когда условие выполнено.
    /// </summary>
    /// <param name="snapshot">Состояние робота.</param>
    /// <param name="graph">Граф карты робота либо <c>null</c>.</param>
    /// <param name="ct">Токен отмены.</param>
    /// <returns><c>true</c>, если событие опубликовано.</returns>
    public async Task<bool> PublishChargingCompletedAsync(
        RobotSnapshot snapshot,
        WarehouseGraph? graph,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var options = _charging.Value;
        if (!options.PublishChargingCompleted || snapshot.BatteryCharge < options.TargetLimit)
        {
            return false;
        }

        var charging = snapshot.Charging
            || RobotRelocationStateMachine.NodeOfType(
                graph,
                snapshot,
                RelocationNodeTypes.Charging,
                options.IdleNodeToleranceMeters) is not null;
        if (!charging)
        {
            return false;
        }

        var payload = new RobotChargingCompletedEvent(
            Guid.NewGuid().ToString("N"),
            snapshot.SerialNumber,
            snapshot.BatteryCharge,
            options.TargetLimit,
            "OK",
            _time.GetUtcNow());

        try
        {
            await _events
                .PublishChargingCompletedAsync(payload, options.ChargingCompletedTopic, ct)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(
                exception,
                "Робот {Serial}: событие завершения заряда не опубликовано",
                snapshot.SerialNumber);
            return false;
        }

        _logger.LogInformation(
            "Робот {Serial}: опубликовано agv-charging-completed {EventId}, заряд {Charge:F1}% при пороге {Threshold}%",
            snapshot.SerialNumber,
            payload.EventId,
            payload.NewBatteryLevel,
            payload.Threshold);

        if (!options.AutoStopCharging)
        {
            return true;
        }

        try
        {
            var actionId = await _orderService
                .SendInstantActionAsync(
                    snapshot.SerialNumber,
                    ChargingNodeAction.StopActionType,
                    Guid.NewGuid().ToString("N"),
                    ct)
                .ConfigureAwait(false);
            _logger.LogInformation(
                "Робот {Serial}: заряд достиг порога, отправлен stopCharging {ActionId}",
                snapshot.SerialNumber,
                actionId);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(
                exception,
                "Робот {Serial}: stopCharging не отправлен",
                snapshot.SerialNumber);
        }

        return true;
    }

    /// <summary>
    /// Снимает резервы узлов: заказ стал терминальным, робот ушёл с узла или робот пропал
    /// из состояний дольше <c>RetryDelaySeconds * 2</c> (Д-3.3).
    /// </summary>
    /// <param name="activeOrders">Активные заказы тика.</param>
    /// <param name="states">Состояния роботов тика.</param>
    /// <param name="now">Момент тика.</param>
    /// <returns>Сколько резервов снято.</returns>
    public int ReleaseReservations(
        IReadOnlyCollection<RobotOrder> activeOrders,
        IReadOnlyCollection<RobotSnapshot> states,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(activeOrders);
        ArgumentNullException.ThrowIfNull(states);

        var activeSerials = activeOrders
            .Select(order => order.AgvSerialNumber)
            .ToHashSet(StringComparer.Ordinal);
        var presentSerials = states
            .Select(snapshot => snapshot.SerialNumber)
            .ToHashSet(StringComparer.Ordinal);
        var timeout = TimeSpan.FromSeconds(Math.Max(_charging.Value.RetryDelaySeconds, 1) * 2);

        var released = 0;
        foreach (var pair in _reservations)
        {
            var reservation = pair.Value;
            var stale = !activeSerials.Contains(reservation.SerialNumber)
                || (!presentSerials.Contains(reservation.SerialNumber)
                    && now - reservation.CreatedAt >= timeout);
            if (stale && _reservations.TryRemove(pair.Key, out _))
            {
                released++;
            }
        }

        return released;
    }

    private async Task<RobotRelocationResult> MoveAsync(
        string serialNumber,
        string nodeType,
        string? nodeId,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serialNumber);

        // Занятость узлов собирается по всем роботам: узел, на котором стоит другой робот
        // или на который он едет, для этого робота занят (Д-3.2).
        var states = await _states.GetAllAsync(ct).ConfigureAwait(false);
        var snapshot = states.FirstOrDefault(item =>
            string.Equals(item.SerialNumber, serialNumber, StringComparison.Ordinal));
        if (snapshot is null)
        {
            _logger.LogWarning("Робот {Serial}: автопереезд невозможен, состояние неизвестно", serialNumber);
            return RobotRelocationResult.Failure(CodeStateNotFound);
        }

        var activeOrders = await _orders.GetActiveAsync(ct).ConfigureAwait(false);
        var occupancy = BuildOccupancy(states, activeOrders);
        return await TryRelocateAsync(snapshot, nodeType, nodeId, occupancy, ct).ConfigureAwait(false);
    }

    private async Task<RobotRelocationResult> StartChargingAsync(
        string serialNumber,
        string nodeId,
        CancellationToken ct)
    {
        try
        {
            var actionId = await _orderService
                .SendInstantActionAsync(
                    serialNumber,
                    ChargingNodeAction.ActionType,
                    Guid.NewGuid().ToString("N"),
                    ct)
                .ConfigureAwait(false);
            _logger.LogInformation(
                "Робот {Serial} уже на узле {Node}: отправлен startCharging {ActionId}",
                serialNumber,
                nodeId,
                actionId);
            return RobotRelocationResult.Immediate(actionId, nodeId);
        }
        catch (OrderRejectedException exception)
        {
            return RobotRelocationResult.Failure($"{exception.Reason}: {exception.Message}");
        }
    }

    private async Task<WarehouseGraph?> LoadMapAsync(string mapId, CancellationToken ct)
    {
        try
        {
            return await _graphs.LoadAsync(mapId, ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException exception)
        {
            _logger.LogWarning(exception, "Карта {Map} недоступна для автопереезда", mapId);
            return null;
        }
    }

    private RelocationOccupancy WithReservations(RelocationOccupancy occupancy) => new()
    {
        Poses = occupancy.Poses,
        ActiveOrderTargets = occupancy.ActiveOrderTargets,
        Reservations = _reservations
            .ToDictionary(pair => pair.Key, pair => pair.Value.SerialNumber, StringComparer.Ordinal),
        OccupiedRadiusMeters = occupancy.OccupiedRadiusMeters,
    };

    private double DefaultAgvLengthMeters()
    {
        var routing = _routing.Value;
        return routing.AgvProfiles.TryGetValue(routing.DefaultAgvProfile, out var profile)
            && profile is not null
            ? profile.LengthMeters
            : 0;
    }

    /// <summary>Резерв узла за роботом: кто и когда получил автопереезд.</summary>
    private sealed record NodeReservation(string SerialNumber, DateTimeOffset CreatedAt);
}
