using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VR.RoutePlanner.Application.Configuration;
using VR.RoutePlanner.Application.Graph;
using VR.RoutePlanner.Application.Planning;
using VR.RoutePlanner.Application.Traffic;
using VR.RoutePlanner.Domain.Planning;
using VR.RoutePlanner.Domain.Graph;
using VR.RoutePlanner.Domain.Robots;

namespace VR.RoutePlanner.Application.Robots;

/// <summary>
/// Заказы роботам: построение плана, сборка и публикация VDA-заказа, ведение статуса
/// по сообщениям <c>state</c>, таймауты, восстановление после рестарта и мгновенные действия.
/// </summary>
public sealed class RobotOrderService
{
    /// <summary>Код причины отказа при расхождении состояния робота и заказа.</summary>
    public const string ReasonStateMismatch = "STATE_MISMATCH";

    /// <summary>Код причины отказа при отсутствии подтверждения робота.</summary>
    public const string ReasonNoAccept = "NO_ACCEPT";

    /// <summary>Код причины отказа при молчании робота.</summary>
    public const string ReasonStateSilence = "STATE_SILENCE";

    /// <summary>Код причины отказа при отсутствии прогресса по узлам.</summary>
    public const string ReasonProgressTimeout = "PROGRESS_TIMEOUT";

    /// <summary>Код причины отказа при ошибке робота в <c>state</c>.</summary>
    public const string ReasonRobotError = "ROBOT_ERROR";

    /// <summary>Код причины отказа: робот не поднял <c>orderUpdateId</c> до опубликованного обновления.</summary>
    public const string ReasonUpdateNotAccepted = "UPDATE_NOT_ACCEPTED";

    /// <summary>Код отказа <see cref="AppendLegAsync"/>: заказ не найден или уже не в работе.</summary>
    public const string ReasonOrderNotActive = "ORDER_NOT_ACTIVE";

    /// <summary>Код отказа <see cref="AppendLegAsync"/>: исчерпан предел <c>MaxOrderUpdates</c>.</summary>
    public const string ReasonMaxOrderUpdates = "MAX_ORDER_UPDATES";

    private static readonly TimeSpan UpdateThrottle = TimeSpan.FromSeconds(2);

    private static readonly string[] ActiveStatuses =
    [
        RobotOrderStatuses.Draft,
        RobotOrderStatuses.Sent,
        RobotOrderStatuses.Accepted,
        RobotOrderStatuses.Executing,
    ];

    /// <summary>Статусы, в которых заказ может получить обновление плана.</summary>
    private static readonly string[] ExtendStatuses =
    [
        RobotOrderStatuses.Sent,
        RobotOrderStatuses.Accepted,
        RobotOrderStatuses.Executing,
    ];

    private readonly IPathPlanner _planner;
    private readonly IVdaOrderComposer composer;
    private readonly IHeaderIdSource _headers;
    private readonly IWarehouseGraphRepository _graphs;
    private readonly IRobotOrderNumberSource _numbers;
    private readonly IRobotOrderPublisher _publisher;
    private readonly IRobotOrderRepository _orders;
    private readonly IRobotStateReader _states;
    private readonly ITrafficCoordinator _traffic;
    private readonly IOptions<RoutePlannerOptions> _options;
    private readonly TimeProvider _time;
    private readonly ILogger<RobotOrderService> _logger;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastUpdate = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _lastUpdateSequence = new(StringComparer.Ordinal);

    /// <summary>Последний замеченный прогресс заказа: узел, номер узла и момент.</summary>
    private readonly ConcurrentDictionary<string, OrderProgress> _progress = new(StringComparer.Ordinal);

    /// <summary>
    /// По одному обработчику на робота: два <c>state</c> подряд не должны дать два продления
    /// с одним <c>orderUpdateId</c> (АС-4) и два разных резерва.
    /// </summary>
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _serialGates = new(StringComparer.Ordinal);

    /// <summary>
    /// Последнее опубликованное обновление заказа (продление или новое плечо): по нему
    /// <see cref="CheckTimeoutsAsync"/> ловит <c>UPDATE_NOT_ACCEPTED</c>. После рестарта теряется.
    /// </summary>
    private readonly ConcurrentDictionary<string, UpdatePublication> _updates = new(StringComparer.Ordinal);

    /// <summary>
    /// Создает сервис заказов.
    /// </summary>
    /// <param name="planner">Планировщик маршрута.</param>
    /// <param name="composer">Сборка тела заказа.</param>
    /// <param name="headers">Источник <c>headerId</c>.</param>
    /// <param name="graphs">Графы карт.</param>
    /// <param name="numbers">Источник номеров заказов.</param>
    /// <param name="publisher">Публикация заказа.</param>
    /// <param name="orders">Хранилище заказов.</param>
    /// <param name="states">Последние состояния роботов.</param>
    /// <param name="options">Параметры <c>Routing</c>.</param>
    /// <param name="time">Источник времени.</param>
    /// <param name="logger">Журнал.</param>
    /// <param name="traffic">Диспетчер трафика: резервы горизонта на <c>state</c> и при выдаче заказа.</param>
    /// <param name="simulatorReset">
    /// Не используется: сброс симулятора убран из потока заказа. Оставлен для совместимости вызовов.
    /// </param>
    public RobotOrderService(
        IPathPlanner planner,
        IVdaOrderComposer composer,
        IHeaderIdSource headers,
        IWarehouseGraphRepository graphs,
        IRobotOrderNumberSource numbers,
        IRobotOrderPublisher publisher,
        IRobotOrderRepository orders,
        IRobotStateReader states,
        IOptions<RoutePlannerOptions> options,
        TimeProvider time,
        ILogger<RobotOrderService> logger,
        ITrafficCoordinator traffic,
        ISimulatorResetService? simulatorReset = null)
    {
        _ = simulatorReset;
        ArgumentNullException.ThrowIfNull(planner);
        ArgumentNullException.ThrowIfNull(composer);
        ArgumentNullException.ThrowIfNull(headers);
        ArgumentNullException.ThrowIfNull(graphs);
        ArgumentNullException.ThrowIfNull(numbers);
        ArgumentNullException.ThrowIfNull(publisher);
        ArgumentNullException.ThrowIfNull(orders);
        ArgumentNullException.ThrowIfNull(states);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(traffic);

        _planner = planner;
        this.composer = composer;
        _headers = headers;
        _graphs = graphs;
        _numbers = numbers;
        _publisher = publisher;
        _orders = orders;
        _states = states;
        _traffic = traffic;
        _options = options;
        _time = time;
        _logger = logger;
    }

    /// <summary>
    /// Создает и публикует заказ роботу на один узел. Тип действия вил в цели выводится из
    /// <paramref name="hasLoad"/>: порожний робот забирает паллету, груженый ставит.
    /// </summary>
    /// <param name="mapId">Карта.</param>
    /// <param name="serialNumber">Серийный номер робота.</param>
    /// <param name="targetNodeId">Целевой узел.</param>
    /// <param name="hasLoad">Робот везет груз.</param>
    /// <param name="ct">Токен отмены.</param>
    /// <returns>Созданный заказ.</returns>
    /// <exception cref="OrderInProgressException">У робота уже есть незавершенный заказ.</exception>
    /// <exception cref="OrderRejectedException">Заказ не создан: карта, узел или маршрут недоступны.</exception>
    public Task<RobotOrder> CreateOrderAsync(
        string mapId,
        string serialNumber,
        string targetNodeId,
        bool hasLoad,
        CancellationToken ct) =>
        CreateTaskOrderAsync(
            mapId,
            serialNumber,
            targetNodeId,
            hasLoad,
            targetNodeId,
            hasLoad ? "drop" : "pick",
            ct);

    /// <summary>
    /// Создает и публикует заказ на всю задачу: первое сообщение (<c>orderUpdateId = 0</c>) ведет
    /// робота к цели первого плеча, а заказ закрывается только в цели всей задачи. Следующие
    /// плечи добавляет <see cref="AppendLegAsync"/>.
    ///
    /// Первое сообщение задачи действий вил не несет: робот только едет к узлу-якорю первого
    /// действия. Действие ставится в последнем узле плана, только если задача им заканчивается
    /// (плечо последнее).
    /// </summary>
    /// <param name="mapId">Карта.</param>
    /// <param name="serialNumber">Серийный номер робота.</param>
    /// <param name="firstLegTargetNodeId">Цель первого плеча: до нее строится маршрут.</param>
    /// <param name="hasLoad">Робот везет груз на первом плече.</param>
    /// <param name="finalTargetNodeId">Цель всей задачи: по ней заказ получает <c>Completed</c>.</param>
    /// <param name="targetActionType">
    /// Тип действия вил в цели плеча; <c>null</c> — действий нет (за задачей есть следующее плечо,
    /// и действие уедет в сообщении, которое начнется на этом узле).
    /// </param>
    /// <param name="ct">Токен отмены.</param>
    /// <param name="targetNodeActionType">
    /// Действие в последнем узле плана без вилочных параметров (<c>startCharging</c> у узла
    /// <c>CHARGING</c>); <c>null</c> — без действия.
    /// </param>
    /// <param name="targetNodeActionBlockingType">Блокирующий тип этого действия; <c>null</c> — <c>SOFT</c>.</param>
    /// <param name="targetNodeActionParameters">Параметры действия целевого узла.</param>
    /// <returns>Созданный заказ.</returns>
    /// <exception cref="OrderInProgressException">У робота уже есть незавершенный заказ.</exception>
    /// <exception cref="OrderRejectedException">Заказ не создан: карта, узел или маршрут недоступны.</exception>
    public async Task<RobotOrder> CreateTaskOrderAsync(
        string mapId,
        string serialNumber,
        string firstLegTargetNodeId,
        bool hasLoad,
        string finalTargetNodeId,
        string? targetActionType,
        CancellationToken ct,
        string? targetNodeActionType = null,
        string? targetNodeActionBlockingType = null,
        IReadOnlyDictionary<string, object?>? targetNodeActionParameters = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mapId);
        ArgumentException.ThrowIfNullOrWhiteSpace(serialNumber);
        ArgumentException.ThrowIfNullOrWhiteSpace(firstLegTargetNodeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(finalTargetNodeId);
        var targetNodeId = firstLegTargetNodeId;
        var singleLeg = string.Equals(firstLegTargetNodeId, finalTargetNodeId, StringComparison.Ordinal);

        var options = _options.Value;
        var current = await _orders.GetLatestAsync(serialNumber, ct).ConfigureAwait(false);
        if (current is not null && ActiveStatuses.Contains(current.Status))
        {
            throw new OrderInProgressException(current.OrderId, current.Status);
        }

        // Прежний orderId в state робота — норма: заказ публикуется сразу, без cancelOrder
        // и ожидания очистки состояния (Д-8 spec-order-updates).
        var snapshot = await _states.GetLatestAsync(serialNumber, ct).ConfigureAwait(false);

        var graph = await _graphs.LoadAsync(mapId, ct).ConfigureAwait(false);
        if (graph is null)
        {
            throw new OrderRejectedException(RoutePlanReason.MapNotFound, $"Карта '{mapId}' не найдена.");
        }

        // Как AGVControl: поза робота перед заказом не корректируется. Первый узел получает
        // фактические X/Y/theta с широкими допусками, поэтому initPosition не нужен.
        // В потоке заказа AGVControl его не отправляет: для рабочего узла такая коррекция
        // уводила робота внутрь стеллажа к паллете.
        var from = RobotStartNodeResolver.Resolve(graph, snapshot);
        var to = graph.Nodes.Values.FirstOrDefault(node => node.NodeId == targetNodeId);
        if (from is null || to is null)
        {
            throw new OrderRejectedException(RoutePlanReason.NodeNotFound, "Узел старта или цели не найден на карте.");
        }

        // Действие вил первого плеча: только если задача им заканчивается. Иначе действие
        // уедет в сообщении, которое начнется на этом узле (плечо N > 0).
        var legAction = options.Order.SendForkActions ? targetActionType : null;

        // Робот уже стоит в целевом рабочем узле, а действие вил нужно: движение не требуется,
        // но сообщение публикуется по раскладке узла-якоря (эталон ref-rev-leg2-pick-drop).
        var stationary = from.Id == to.Id
            && WorkingNodeGeometry.IsWorking(to)
            && legAction is not null;

        if (from.Id == to.Id && !stationary)
        {
            // Робот уже стоит на узле подъезда: движение не нужно, сразу фиксируем выполнение.
            var immediateNumber = await _numbers.NextAsync(ct).ConfigureAwait(false);
            var immediateId = $"ORD_{immediateNumber:D5}";
            var immediateNow = _time.GetUtcNow();
            var immediate = new RobotOrder(
                immediateId,
                serialNumber,
                0,
                RobotOrderStatuses.Completed,
                "{}",
                0,
                to.NodeId,
                null,
                immediateNow,
                immediateNow,
                immediateNow,
                immediateNow,
                null,
                Array.Empty<RobotOrderNode>());
            await _orders.SaveAsync(immediate, ct).ConfigureAwait(false);
            _logger.LogInformation(
                "Заказ {OrderId}: робот уже на узле {Node}, движение не требуется",
                immediateId,
                to.NodeId);
            return immediate;
        }

        // При стоянке в цели планировщик не вызывается: маршрут пуст, узлы собирает сборщик заказа.
        var plan = stationary
            ? RoutePlanResult.Success(
                new PlannedRoute([], 0, 0, 0, 0, double.PositiveInfinity, false, null),
                null)
            : await _planner
                .PlanAsync(new RoutePlanRequest(mapId, options.DefaultAgvProfile, from.Id, to.Id, hasLoad), ct)
                .ConfigureAwait(false);
        if (!plan.Found || plan.Route is null)
        {
            throw new OrderRejectedException(plan.Reason ?? RoutePlanReason.NoPath, "Маршрут не построен.");
        }

        _logger.LogInformation(
            "Заказ {OrderId}: старт {From} -> {To}, lastNodeId={LastNode}, seq={Seq}, позиция {X}/{Y}",
            targetNodeId,
            from.NodeId,
            to.NodeId,
            snapshot?.LastNodeId,
            snapshot?.LastNodeSequenceId,
            snapshot?.PositionX,
            snapshot?.PositionY);

        var number = await _numbers.NextAsync(ct).ConfigureAwait(false);
        var orderId = $"ORD_{number:D5}";
        var now = _time.GetUtcNow();
        var headerId = _headers.Next();

        // Маршрут кончился на узле подхода (цель внутри слота непроезжая): действие pick/drop
        // ставится в последнем узле маршрута, координаты паллеты берутся у целевого узла.
        var approachTarget = plan.ApproachNodeId is not null
            && WorkingNodeGeometry.IsWorking(to)
            ? to
            : null;

        var fullRequest = new VdaOrderRequest(
            plan.Route,
            graph,
            mapId,
            orderId,
            options.Order.Manufacturer,
            serialNumber,
            headerId,
            now,
            OrderUpdateId: 0,
            FirstNodeTheta: options.Order.FirstNodeTheta,
            RobotX: snapshot?.PositionX,
            RobotY: snapshot?.PositionY,
            RobotTheta: snapshot?.Theta,
            // Завершающее действие только в последнем плече задачи (Д-4). Заказ на зарядку несет
            // в целевом узле ровно одно действие — startCharging: detectObject у станции не нужен
            // и на симуляторе не завершается, оставляя робота в WORKING.
            TerminalActionType: singleLeg && targetNodeActionType is null ? options.Order.TerminalActionType : null,
            ApproachTarget: approachTarget,
            // Стоянка в узле: сообщение начинается тройкой узла-якоря, маршрута нет,
            // действие вил стоит на дубле узла.
            AnchorNode: stationary ? to : null,
            AnchorActionType: stationary ? legAction : null,
            TargetActionType: stationary ? null : legAction,
            SequenceIdBase: 0,
            FirstNodeIsPose: true,
            // Действие целевого узла (startCharging у CHARGING): без вилочных параметров (Д-4).
            TargetNodeActionType: targetNodeActionType,
            TargetNodeActionBlockingType: targetNodeActionBlockingType,
            TargetNodeActionParameters: targetNodeActionParameters);

        // В PlanJson лежит полный план: нарезка нужна только для отправки и обновлений.
        var planJson = composer.Compose(fullRequest);
        var planNodeCount = CountNodes(planJson);
        var chunks = OrderChunkPlanner.Slice(planNodeCount, options.Order.ReleasedNodes);
        var chunk = chunks.Count > 0 ? chunks[0] : new OrderChunk(0, planNodeCount);
        var orderJson = chunk.StartIndex == 0 && chunk.NodeCount >= planNodeCount
            ? planJson
            : composer.Compose(fullRequest with
            {
                SliceStartIndex = chunk.StartIndex,
                SliceNodeCount = chunk.NodeCount,
            });

        // Трафик влияет только на выдачу заказа: горизонт нового плана резервируется атомарно,
        // при занятом коридоре заказ не публикуется и не сохраняется (409 TRAFFIC_BLOCKED).
        var traffic = await _traffic
            .TryReserveForOrderAsync(TrafficSnapshot(serialNumber, mapId, snapshot, now), planJson, ct)
            .ConfigureAwait(false);

        if (!traffic.Accepted && traffic.ReasonCode == TrafficReservationResult.ReasonBlocked)
        {
            throw new OrderRejectedException(
                TrafficReservationResult.ReasonBlocked,
                $"Коридор занят роботом {traffic.BlockedBySerial}: ресурс {traffic.BlockedResourceCode}.");
        }

        await _publisher
            .PublishAsync(options.Order.Manufacturer, serialNumber, orderJson, ct)
            .ConfigureAwait(false);

        var nodes = BuildNodes(planJson, orderId, chunk);
        var order = new RobotOrder(
            orderId,
            serialNumber,
            0,
            RobotOrderStatuses.Sent,
            planJson,
            chunk.NodeCount,
            // Цель всей задачи: Completed только здесь, конец промежуточного плеча заказ не закрывает.
            finalTargetNodeId,
            plan.ApproachNodeId,
            now,
            now,
            null,
            null,
            null,
            nodes);

        await _orders.SaveAsync(order, ct).ConfigureAwait(false);
        LogPublication(orderId, 0, orderJson, to.NodeId, "заказ");
        _logger.LogInformation(
            "Заказ {OrderId} роботу {Serial}: узлов {Nodes} (в сообщении {Sent}, кусков {Chunks}), длина {Length:F2} м",
            orderId,
            serialNumber,
            nodes.Count,
            chunk.NodeCount,
            chunks.Count,
            plan.Route.TotalLengthMeters);

        return order;
    }

    /// <summary>
    /// Добавляет к заказу задачи следующее плечо: публикует <c>order update</c> с тем же
    /// <c>orderId</c>, <c>orderUpdateId + 1</c> и нумерацией, продолжающей прошлое сообщение.
    ///
    /// Сообщение плеча начинается на узле-якоре — последнем узле плана заказа, где робот
    /// выполняет действие вил: узел-якорь без курса, ребро <c>VIRTUAL_START_&lt;nodeId&gt;</c>,
    /// дубль узла с курсом к паллете и действием <paramref name="anchorActionType"/>, затем
    /// маршрут до цели плеча с действием <paramref name="targetActionType"/>. Подтверждение
    /// прошлого плеча по <c>state</c> не ожидается: публикация идет сразу (поправка 2, П-1).
    /// </summary>
    /// <param name="orderId">Заказ задачи.</param>
    /// <param name="targetNodeId">Цель плеча.</param>
    /// <param name="hasLoad">Робот везет груз на этом плече.</param>
    /// <param name="anchorActionType">
    /// Тип действия вил на узле-якоре (действие предыдущего плеча); <c>null</c> — без действия.
    /// </param>
    /// <param name="targetActionType">
    /// Тип действия вил в цели плеча; <c>null</c> — без действия (за плечом есть следующее).
    /// </param>
    /// <param name="ct">Токен отмены.</param>
    /// <returns>Заказ после публикации плеча.</returns>
    /// <exception cref="OrderRejectedException">
    /// <c>ORDER_NOT_ACTIVE</c>, <c>MAX_ORDER_UPDATES</c> или причина планировщика; сообщение
    /// при этом не публикуется.
    /// </exception>
    public async Task<RobotOrder> AppendLegAsync(
        string orderId,
        string targetNodeId,
        bool hasLoad,
        string? anchorActionType,
        string? targetActionType,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(orderId);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetNodeId);

        var order = await _orders.GetAsync(orderId, ct).ConfigureAwait(false)
            ?? throw new OrderRejectedException(ReasonOrderNotActive, $"Заказ {orderId} не найден.");

        // Общий с ApplyStateAsync обработчик робота: продление и смена плеча не дают
        // двух сообщений с одним orderUpdateId.
        var gate = _serialGates.GetOrAdd(order.AgvSerialNumber, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await AppendLegCoreAsync(orderId, targetNodeId, hasLoad, anchorActionType, targetActionType, ct)
                .ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<RobotOrder> AppendLegCoreAsync(
        string orderId,
        string targetNodeId,
        bool hasLoad,
        string? anchorActionType,
        string? targetActionType,
        CancellationToken ct)
    {
        var options = _options.Value;

        // Заказ перечитывается под обработчиком робота: state мог поменять его до входа.
        var order = await _orders.GetAsync(orderId, ct).ConfigureAwait(false);
        if (order is null || !ExtendStatuses.Contains(order.Status))
        {
            throw new OrderRejectedException(
                ReasonOrderNotActive,
                $"Заказ {orderId} не в работе (статус {order?.Status ?? "нет"}).");
        }

        if (order.OrderUpdateId >= options.Order.MaxOrderUpdates)
        {
            throw new OrderRejectedException(
                ReasonMaxOrderUpdates,
                $"Заказ {orderId}: достигнут предел обновлений {options.Order.MaxOrderUpdates}.");
        }

        // Поза робота нужна только для геометрии точки остановки: публикация плеча от нее
        // не зависит (П-1), поэтому ожидания подтверждения здесь нет.
        var snapshot = await _states.GetLatestAsync(order.AgvSerialNumber, ct).ConfigureAwait(false);

        var mapId = PlanMapId(order.PlanJson) ?? snapshot?.MapId;
        var graph = string.IsNullOrWhiteSpace(mapId)
            ? null
            : await _graphs.LoadAsync(mapId, ct).ConfigureAwait(false);
        if (graph is null)
        {
            throw new OrderRejectedException(RoutePlanReason.MapNotFound, $"Карта '{mapId}' не найдена.");
        }

        // Старт плеча — последний узел плана заказа (П-5), а не узел по снимку робота:
        // робот может стоять между узлами, а продолжение заказа должно начинаться там,
        // где кончилось предыдущее сообщение.
        var from = LegStartNode(graph, order) ?? RobotStartNodeResolver.Resolve(graph, snapshot);
        var to = graph.Nodes.Values.FirstOrDefault(node => node.NodeId == targetNodeId);
        if (from is null || to is null)
        {
            throw new OrderRejectedException(RoutePlanReason.NodeNotFound, "Узел старта или цели не найден на карте.");
        }

        var sendFork = options.Order.SendForkActions;
        var anchorAction = sendFork ? anchorActionType : null;
        var endAction = sendFork ? targetActionType : null;

        // Действие узла-якоря уже стоит в плане заказа (первое сообщение со стоянкой в узле
        // сразу несло действие): повторять его нельзя, иначе робот выполнит вилы дважды.
        if (anchorAction is not null && PlanHasAction(order.PlanJson, from.NodeId, anchorAction))
        {
            anchorAction = null;
        }

        var empty = from.Id == to.Id;
        if (empty && anchorAction is null && endAction is null)
        {
            // Д-7: робот уже в цели плеча, движения и действий нет — сообщение не публикуется.
            _logger.LogInformation(
                "Заказ {OrderId}: плечо к {Node} выполнено без публикации, робот уже в узле",
                orderId,
                to.NodeId);
            return order;
        }

        var plan = empty
            ? RoutePlanResult.Success(
                new PlannedRoute([], 0, 0, 0, 0, double.PositiveInfinity, false, null),
                null)
            : await _planner
                .PlanAsync(new RoutePlanRequest(mapId!, options.DefaultAgvProfile, from.Id, to.Id, hasLoad), ct)
                .ConfigureAwait(false);
        if (!plan.Found || plan.Route is null)
        {
            throw new OrderRejectedException(plan.Reason ?? RoutePlanReason.NoPath, "Маршрут не построен.");
        }

        var lastLeg = string.Equals(to.NodeId, order.TargetNodeId, StringComparison.Ordinal);
        var updateId = order.OrderUpdateId + 1;
        var now = _time.GetUtcNow();
        var request = new VdaOrderRequest(
            plan.Route,
            graph,
            mapId!,
            order.OrderId,
            options.Order.Manufacturer,
            order.AgvSerialNumber,
            _headers.Next(),
            now,
            OrderUpdateId: updateId,
            FirstNodeTheta: options.Order.FirstNodeTheta,
            RobotX: snapshot?.PositionX,
            RobotY: snapshot?.PositionY,
            RobotTheta: snapshot?.Theta,
            TerminalActionType: lastLeg ? options.Order.TerminalActionType : null,
            ApproachTarget: plan.ApproachNodeId is not null && WorkingNodeGeometry.IsWorking(to) ? to : null,
            // П-2 и П-4: сообщение начинается тройкой узла-якоря, маршрут идет за ней.
            AnchorNode: from,
            AnchorActionType: anchorAction,
            TargetActionType: endAction,
            // Д-2: номера продолжают прошлое сообщение (эталон: последний узел 12 -> поза 12).
            SequenceIdBase: LastPlanSequenceId(order) ?? 0,
            // П-3: первый узел сообщения N > 0 — узел-якорь с координатами карты и без курса.
            FirstNodeIsPose: false);

        var planJson = composer.Compose(request);
        var planNodeCount = CountNodes(planJson);
        var chunks = OrderChunkPlanner.Slice(planNodeCount, options.Order.ReleasedNodes);
        var chunk = chunks.Count > 0 ? chunks[0] : new OrderChunk(0, planNodeCount);
        var messageJson = chunk.StartIndex == 0 && chunk.NodeCount >= planNodeCount
            ? planJson
            : composer.Compose(request with
            {
                SliceStartIndex = chunk.StartIndex,
                SliceNodeCount = chunk.NodeCount,
            });

        await _publisher
            .PublishAsync(options.Order.Manufacturer, order.AgvSerialNumber, messageJson, ct)
            .ConfigureAwait(false);
        RememberUpdate(order, updateId, now);
        LogPublication(order.OrderId, updateId, messageJson, to.NodeId, "плечо");

        // Узлы прошлого плеча из заказа уходят: PlanJson и узлы хранят только текущее плечо.
        // Резерв трафика пересоберется по новому PlanJson на следующем state.
        var updated = order with
        {
            PlanJson = planJson,
            OrderUpdateId = updateId,
            ReleasedNodeCount = chunk.NodeCount,
            Nodes = BuildNodes(planJson, order.OrderId, chunk),
        };

        await _orders.SaveAsync(updated, ct).ConfigureAwait(false);
        return updated;
    }

    /// <summary>
    /// Обновляет статус заказа по сообщению <c>state</c>.
    ///
    /// Единственная точка входа автомата. Сообщения одного робота обрабатываются по очереди:
    /// иначе два <c>state</c> подряд дают два продления с одним <c>orderUpdateId</c> (АС-4).
    /// </summary>
    /// <param name="snapshot">Снимок робота.</param>
    /// <param name="ct">Токен отмены.</param>
    /// <returns>Заказ после обновления либо <c>null</c>, если заказа нет.</returns>
    public async Task<RobotOrder?> ApplyStateAsync(RobotSnapshot snapshot, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var gate = _serialGates.GetOrAdd(snapshot.SerialNumber, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await ApplyStateCoreAsync(snapshot, ct).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<RobotOrder?> ApplyStateCoreAsync(RobotSnapshot snapshot, CancellationToken ct)
    {
        var now = _time.GetUtcNow();

        if (string.IsNullOrWhiteSpace(snapshot.OrderId))
        {
            return await CloseWithoutOrderIdAsync(snapshot, now, ct).ConfigureAwait(false);
        }

        var order = await _orders.GetAsync(snapshot.OrderId!, ct).ConfigureAwait(false);
        if (order is null)
        {
            return null;
        }

        var status = order.Status;
        var failure = order.FailureReason;
        DateTimeOffset? acceptedAt = order.AcceptedAt;
        DateTimeOffset? completedAt = order.CompletedAt;

        // Строка 10 таблицы §3.1: устаревшая версия заказа игнорируется целиком.
        if (snapshot.OrderUpdateId < order.OrderUpdateId)
        {
            return order;
        }

        var blockingError = RobotErrorClassifier.HasBlockingError(snapshot);
        if (blockingError && ActiveStatuses.Contains(status))
        {
            // Строки 2 и 3: до первого сдвига с первого узла плана заказ еще не поехал — Rejected.
            status = order.Status == RobotOrderStatuses.Sent && !HasMovedFromFirstNode(order, snapshot.LastNodeId)
                ? RobotOrderStatuses.Rejected
                : RobotOrderStatuses.Failed;
            failure = ReasonRobotError;
        }
        else if (status == RobotOrderStatuses.Sent)
        {
            status = RobotOrderStatuses.Accepted;
            acceptedAt = now;
        }

        var nodes = MergeNodes(order, snapshot, out var reachedLast);

        if (status == RobotOrderStatuses.Accepted && (snapshot.Driving || nodes.Any(node => node.Reached)))
        {
            status = RobotOrderStatuses.Executing;
        }

        if (ActiveStatuses.Contains(status) && HasFinishedCancelAction(snapshot))
        {
            status = RobotOrderStatuses.Cancelled;
        }

        // Ошибка действия вил текущего плеча: заказ проваливается, cancelOrder не отправляется (Д-9).
        if (ActiveStatuses.Contains(status) && HasFailedPlanAction(order, snapshot))
        {
            status = RobotOrderStatuses.Failed;
            failure = ReasonRobotError;
        }

        // Закрытие заказа только в цели всей задачи (Д-6 spec-order-updates): пройден последний
        // узел текущего сообщения и это TargetNodeId. Конец промежуточного плеча заказ не закрывает.
        // Пустые nodeStates/edgeStates доказательством финиша не считаются (АС-1, Д-2).
        var reachedTarget = !string.IsNullOrWhiteSpace(order.TargetNodeId)
            && string.Equals(snapshot.LastNodeId, order.TargetNodeId, StringComparison.Ordinal);

        if (status == RobotOrderStatuses.Executing
            && !snapshot.Driving
            && !HasRunningAction(snapshot)
            && reachedLast
            && reachedTarget)
        {
            status = RobotOrderStatuses.Completed;
            completedAt = now;
        }

        if (status == RobotOrderStatuses.Completed)
        {
            // Заказ закрыт: пройден весь план, отметку получают и узлы без отдельного lastNodeId.
            nodes = nodes.Select(node => node.Reached ? node : node with { Reached = true }).ToList();
        }

        var changed = status != order.Status
            || !nodes.SequenceEqual(order.Nodes)
            || acceptedAt != order.AcceptedAt
            || completedAt != order.CompletedAt
            || failure != order.FailureReason;

        if (!changed && !ExtendStatuses.Contains(status))
        {
            await _traffic.OnStateAsync(snapshot, order.PlanJson, ct).ConfigureAwait(false);
            return order;
        }

        var updated = order with
        {
            Status = status,
            AcceptedAt = acceptedAt,
            CompletedAt = completedAt,
            FailureReason = failure,
            Nodes = nodes,
        };

        if (ExtendStatuses.Contains(status))
        {
            updated = await ExtendPlanAsync(updated, snapshot, ct).ConfigureAwait(false);
        }

        await _orders.SaveAsync(updated, ct).ConfigureAwait(false);
        _logger.LogInformation("Заказ {OrderId}: статус {Status}", updated.OrderId, updated.Status);

        TrackProgress(updated, snapshot, now);

        if (ActiveStatuses.Contains(updated.Status))
        {
            await _traffic.OnStateAsync(snapshot, updated.PlanJson, ct).ConfigureAwait(false);
            return updated;
        }

        _updates.TryRemove(updated.OrderId, out _);
        await _traffic.ReleaseAsync(updated.AgvSerialNumber, ct).ConfigureAwait(false);
        return updated;
    }

    /// <summary>
    /// Робот очистил <c>orderId</c>. Если у робота есть активный заказ и <c>lastNodeId</c> доказывает
    /// финиш — заказ закрывается (строка 7 таблицы §3.1, риск Р-1), иначе только освобождаются резервы.
    /// </summary>
    private async Task<RobotOrder?> CloseWithoutOrderIdAsync(
        RobotSnapshot snapshot,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var order = await _orders
            .GetActiveForRobotAsync(snapshot.SerialNumber, ct)
            .ConfigureAwait(false);

        if (order is null || !ActiveStatuses.Contains(order.Status))
        {
            await _traffic.ReleaseAsync(snapshot.SerialNumber, ct).ConfigureAwait(false);
            return null;
        }

        // Только цель всей задачи: последний узел промежуточного плеча заказ не закрывает (Д-6).
        var finished = !string.IsNullOrWhiteSpace(snapshot.LastNodeId)
            && string.Equals(snapshot.LastNodeId, order.TargetNodeId, StringComparison.Ordinal);

        if (!finished)
        {
            // Точного совпадения с целью плана нет: заказ не трогаем, чтобы не закрыть чужой.
            await _traffic.ReleaseAsync(snapshot.SerialNumber, ct).ConfigureAwait(false);
            return null;
        }

        var completed = order with
        {
            Status = RobotOrderStatuses.Completed,
            CompletedAt = order.CompletedAt ?? now,
            Nodes = MergeNodes(order, snapshot, out _)
                .Select(node => node with { Reached = true })
                .ToList(),
        };

        await _orders.SaveAsync(completed, ct).ConfigureAwait(false);
        await _traffic.ReleaseAsync(snapshot.SerialNumber, ct).ConfigureAwait(false);
        _logger.LogInformation(
            "Заказ {OrderId} закрыт по lastNodeId {Node}: робот очистил orderId",
            completed.OrderId,
            snapshot.LastNodeId);

        return completed;
    }

    /// <summary>
    /// Публикует обновление заказа, когда кусок почти исчерпан: план продолжается от последнего
    /// достигнутого узла, <c>orderUpdateId</c> растет, нумерация исходного плана сохраняется.
    /// </summary>
    private async Task<RobotOrder> ExtendPlanAsync(RobotOrder order, RobotSnapshot snapshot, CancellationToken ct)
    {
        var orderOptions = _options.Value.Order;

        // В режиме ReleasedNodes = 0 робот получил весь маршрут: чанков и обновлений нет,
        // newBaseRequest ни к чему не обязывает.
        if (orderOptions.ReleasedNodes <= 0)
        {
            if (snapshot.NewBaseRequest)
            {
                _logger.LogInformation(
                    "Заказ {OrderId}: newBaseRequest проигнорирован, режим ReleasedNodes=0",
                    order.OrderId);
            }

            return order;
        }

        if (string.IsNullOrWhiteSpace(order.PlanJson))
        {
            return order;
        }

        if (!ExtendStatuses.Contains(order.Status))
        {
            return order;
        }

        // Строка 11 таблицы §3.1: версия робота больше нашей — продление не выполняется,
        // наш OrderUpdateId не переписывается. Продление идет только вровень с нашей версией.
        if (!string.Equals(snapshot.OrderId, order.OrderId, StringComparison.Ordinal)
            || snapshot.OrderUpdateId != order.OrderUpdateId)
        {
            return order;
        }

        if (order.OrderUpdateId >= orderOptions.MaxOrderUpdates)
        {
            _logger.LogWarning(
                "Заказ {OrderId}: достигнут предел обновлений {Max}",
                order.OrderId,
                orderOptions.MaxOrderUpdates);
            return order;
        }

        var reached = order.Nodes.Where(node => node.Reached).Select(node => node.NodeId).ToHashSet(StringComparer.Ordinal);

        // AGV выставляет newBaseRequest, когда его базовый план исчерпан: как в AGVControl,
        // строим продолжение от последнего достигнутого узла, не дожидаясь счётчика узлов.
        var remaining = snapshot.NewBaseRequest ? 0 : snapshot.LastNodeSequenceId is { } lastSeq
            ? VdaOrderUpdater.CountUnreachedInChunk(
                order.PlanJson,
                lastSeq,
                orderOptions.ReleasedNodes,
                snapshot.NodeStates)
            : VdaOrderUpdater.CountUnreachedReleased(order.PlanJson, reached);

        _logger.LogInformation(
            "Заказ {OrderId}: продление, seq={Seq}, осталось={Remaining}, версия={Version}",
            order.OrderId,
            snapshot.LastNodeSequenceId,
            remaining,
            order.OrderUpdateId);

        if (remaining > 1)
        {
            return order;
        }

        var now = _time.GetUtcNow();
        if (_lastUpdate.TryGetValue(order.AgvSerialNumber, out var last) && now - last < UpdateThrottle)
        {
            return order;
        }

        var reachedSequence = snapshot.LastNodeSequenceId
            ?? order.Nodes.Where(node => node.Reached).Select(node => node.SequenceId).DefaultIfEmpty(-1).Max();
        if (_lastUpdateSequence.TryGetValue(order.AgvSerialNumber, out var lastSequence)
            && reachedSequence <= lastSequence)
        {
            return order;
        }

        var releasedNodes = orderOptions.ReleasedNodes;
        var updateJson = VdaOrderUpdater.BuildUpdate(
            order.PlanJson,
            order.OrderUpdateId + 1,
            releasedNodes,
            _headers.Next(),
            now,
            snapshot.LastNodeId,
            snapshot.LastNodeSequenceId,
            reached);

        if (updateJson is null)
        {
            return order;
        }

        await _publisher
            .PublishAsync(orderOptions.Manufacturer, order.AgvSerialNumber, updateJson, ct)
            .ConfigureAwait(false);
        _lastUpdate[order.AgvSerialNumber] = now;
        _lastUpdateSequence[order.AgvSerialNumber] = reachedSequence;
        RememberUpdate(order, order.OrderUpdateId + 1, now);
        LogPublication(order.OrderId, order.OrderUpdateId + 1, updateJson, LastPlanNodeId(order), "продление");

        var updatedNodes = BuildNodesFromPlan(updateJson, order.OrderId, reached);
        _logger.LogInformation(
            "Заказ {OrderId}: обновление {Version}, узлов в плане {Nodes}",
            order.OrderId,
            order.OrderUpdateId + 1,
            updatedNodes.Count);

        return order with
        {
            PlanJson = updateJson,
            OrderUpdateId = order.OrderUpdateId + 1,
            ReleasedNodeCount = updatedNodes.Count,
            Nodes = updatedNodes,
        };
    }

    /// <summary>
    /// Проверяет таймауты активных заказов роботов.
    /// </summary>
    /// <summary>
    /// Возвращает заказ по идентификатору.
    /// </summary>
    /// <param name="orderId">Идентификатор заказа.</param>
    /// <param name="ct">Отмена.</param>
    /// <returns>Заказ или <c>null</c>.</returns>
    public Task<RobotOrder?> GetOrderAsync(string orderId, CancellationToken ct) =>
        _orders.GetAsync(orderId, ct);

    /// <summary>
    /// Проверяет, есть ли у робота незавершенный заказ.
    /// </summary>
    /// <param name="serialNumber">Серийный номер робота.</param>
    /// <param name="ct">Отмена.</param>
    /// <returns><c>true</c>, если заказ в работе.</returns>
    public async Task<bool> HasActiveOrderAsync(string serialNumber, CancellationToken ct)
    {
        var active = await _orders.GetActiveAsync(ct).ConfigureAwait(false);
        return active.Any(order => string.Equals(order.AgvSerialNumber, serialNumber, StringComparison.Ordinal));
    }

    /// <summary>
    /// Отправляет pick/drop как instantAction, как это делает AGVControl: координаты паллеты
    /// берутся от текущей позы робота на расстоянии длины корпуса.
    /// </summary>
    /// <param name="serialNumber">Серийный номер робота.</param>
    /// <param name="actionType">Тип действия: pick или drop.</param>
    /// <param name="ct">Отмена.</param>
    /// <returns>Идентификатор действия.</returns>
    public async Task<string> TriggerForkAsync(string serialNumber, string actionType, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serialNumber);
        ArgumentException.ThrowIfNullOrWhiteSpace(actionType);

        var snapshot = await _states.GetLatestAsync(serialNumber, ct).ConfigureAwait(false)
            ?? throw new OrderRejectedException(RoutePlanReason.NodeNotFound, "Нет состояния робота для действия вил.");

        var options = _options.Value;
        var length = options.AgvProfiles.TryGetValue(options.DefaultAgvProfile, out var profile)
            ? profile.LengthMeters
            : 1.455;
        var distance = length + 0.05;

        var containerX = snapshot.PositionX + (distance * Math.Cos(snapshot.Theta));
        var containerY = snapshot.PositionY + (distance * Math.Sin(snapshot.Theta));

        var parameters = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["containerTypeId"] = "0",
            ["containerX"] = containerX,
            ["containerY"] = containerY,
            ["containerTheta"] = snapshot.Theta,
            ["onlyFork"] = true,
            ["autoReturnNode"] = false,
            ["noScan"] = false,
        };

        var actionId = Guid.NewGuid().ToString("N");
        var payload = VdaInstantActionPayload.Build(actionType, actionId, _time.GetUtcNow(), parameters);
        await _publisher.PublishInstantActionAsync(options.Order.Manufacturer, serialNumber, payload, ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Роботу {Serial} отправлено действие {Action} ({ActionId}), паллета {X}/{Y}",
            serialNumber,
            actionType,
            actionId,
            Math.Round(containerX, 3),
            Math.Round(containerY, 3));

        return actionId;
    }

    /// <summary>
    /// Возвращает статус действия робота из последнего состояния.
    /// </summary>
    /// <param name="serialNumber">Серийный номер робота.</param>
    /// <param name="actionId">Идентификатор действия.</param>
    /// <param name="ct">Отмена.</param>
    /// <returns>Статус действия или <c>null</c>.</returns>
    public async Task<string?> GetActionStatusAsync(string serialNumber, string actionId, CancellationToken ct)
    {
        var snapshot = await _states.GetLatestAsync(serialNumber, ct).ConfigureAwait(false);
        return snapshot?.ActionStates?.FirstOrDefault(item => item.ActionId == actionId)?.ActionStatus;
    }
    public async Task<int> CheckTimeoutsAsync(CancellationToken ct)
    {
        var options = _options.Value.Order;
        var now = _time.GetUtcNow();
        var failed = 0;

        foreach (var active in await _orders.GetActiveAsync(ct).ConfigureAwait(false))
        {
            var order = await _orders.GetAsync(active.OrderId, ct).ConfigureAwait(false);
            if (order is null)
            {
                continue;
            }

            var snapshot = await _states.GetLatestAsync(order.AgvSerialNumber, ct).ConfigureAwait(false);
            string? reason = null;

            if (snapshot is not null
                && order.Status == RobotOrderStatuses.Sent
                && string.Equals(snapshot.OrderId, order.OrderId, StringComparison.Ordinal))
            {
                // Робот уже принял заказ, просто событие accept не попало в обработчик: фиксируем прием.
                order = order with
                {
                    Status = RobotOrderStatuses.Accepted,
                    AcceptedAt = order.AcceptedAt ?? now,
                };
                await _orders.SaveAsync(order, ct).ConfigureAwait(false);
            }

            if (order.Status == RobotOrderStatuses.Sent
                && order.SentAt is not null
                && (now - order.SentAt.Value).TotalSeconds > options.AcceptTimeoutSeconds)
            {
                reason = ReasonNoAccept;
            }
            else if (order.Status != RobotOrderStatuses.Executing
                && order.SentAt is not null
                && (now - order.SentAt.Value).TotalSeconds <= options.AcceptTimeoutSeconds)
            {
                // Робот ещё не подтвердил заказ: молчание состояния не считается отказом,
                // пока не истекло окно приёма. Иначе заказ падает раньше, чем робот ответит.
                reason = null;
            }
            else if (snapshot is null
                || snapshot.OrderId != order.OrderId
                || (now - snapshot.ReceivedAt).TotalSeconds > options.StateSilenceSeconds)
            {
                reason = ReasonStateSilence;
            }
            else if (order.Status is RobotOrderStatuses.Accepted or RobotOrderStatuses.Executing
                && snapshot.OrderUpdateId < order.OrderUpdateId
                && _updates.TryGetValue(order.OrderId, out var publication)
                && publication.UpdateId == order.OrderUpdateId
                && (now - publication.At).TotalSeconds > options.UpdateAcceptTimeoutSeconds)
            {
                // Д-9: обновление не повторяется и cancelOrder не отправляется; робот остается
                // в последнем подтвержденном узле, решение за оператором.
                reason = ReasonUpdateNotAccepted;
            }
            else if (order.Status == RobotOrderStatuses.Executing
                && (now - LastProgressAt(order)).TotalSeconds > options.ProgressTimeoutSeconds)
            {
                // АС-9: таймаут отсчитывается от последнего прогресса, а не от AcceptedAt.
                reason = ReasonProgressTimeout;
            }

            if (reason is null)
            {
                continue;
            }

            await _orders
                .SaveAsync(order with { Status = RobotOrderStatuses.Failed, FailureReason = reason }, ct)
                .ConfigureAwait(false);
            await _traffic.ReleaseAsync(order.AgvSerialNumber, ct).ConfigureAwait(false);
            _updates.TryRemove(order.OrderId, out _);
            _logger.LogWarning("Заказ {OrderId} провален: {Reason}", order.OrderId, reason);
            failed++;
        }

        return failed;
    }

    /// <summary>
    /// Момент последнего прогресса заказа: сдвиг <c>lastNodeId</c>/<c>lastNodeSequenceId</c>,
    /// иначе подтверждение заказа.
    /// </summary>
    private DateTimeOffset LastProgressAt(RobotOrder order) =>
        _progress.TryGetValue(order.OrderId, out var tracked)
            ? tracked.At
            : order.AcceptedAt ?? order.SentAt ?? order.CreatedAt;

    /// <summary>
    /// Восстанавливает заказы после рестарта: продолжает те, что подтверждены <c>state</c>,
    /// остальные проваливает с <c>STATE_MISMATCH</c>.
    /// </summary>
    /// <param name="ct">Токен отмены.</param>
    /// <returns>Сколько заказов провалено при восстановлении.</returns>
    public async Task<int> RecoverAsync(CancellationToken ct)
    {
        var failed = 0;
        foreach (var order in await _orders.GetActiveAsync(ct).ConfigureAwait(false))
        {
            var snapshot = await _states.GetLatestAsync(order.AgvSerialNumber, ct).ConfigureAwait(false);
            if (snapshot is not null && snapshot.OrderId == order.OrderId && snapshot.Driving)
            {
                // Горизонт резерва восстанавливается тем же расчетом внутри ApplyStateAsync (§2.6).
                _logger.LogInformation("Заказ {OrderId} продолжен после рестарта", order.OrderId);
                await ApplyStateAsync(snapshot, ct).ConfigureAwait(false);
                continue;
            }

            if (snapshot is null)
            {
                _logger.LogWarning(
                    "Заказ {OrderId}: резерв не восстановлен, ждём state робота {Serial}",
                    order.OrderId,
                    order.AgvSerialNumber);
            }

            await _orders
                .SaveAsync(
                    order with { Status = RobotOrderStatuses.Failed, FailureReason = ReasonStateMismatch },
                    ct)
                .ConfigureAwait(false);
            await _traffic.ReleaseAsync(order.AgvSerialNumber, ct).ConfigureAwait(false);
            _logger.LogWarning("Заказ {OrderId} провален при восстановлении: {Reason}", order.OrderId, ReasonStateMismatch);
            failed++;
        }

        return failed;
    }

    /// <summary>
    /// Публикует мгновенное действие роботу.
    /// </summary>
    /// <param name="serialNumber">Серийный номер робота.</param>
    /// <param name="actionType">Тип действия.</param>
    /// <param name="actionId">Идентификатор действия.</param>
    /// <param name="ct">Токен отмены.</param>
    /// <returns>Идентификатор действия.</returns>
    public async Task<string> SendInstantActionAsync(
        string serialNumber,
        string actionType,
        string actionId,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serialNumber);
        ArgumentException.ThrowIfNullOrWhiteSpace(actionType);
        ArgumentException.ThrowIfNullOrWhiteSpace(actionId);

        var payload = VdaInstantActionPayload.Build(actionType, actionId, _time.GetUtcNow());
        await _publisher
            .PublishInstantActionAsync(_options.Value.Order.Manufacturer, serialNumber, payload, ct)
            .ConfigureAwait(false);

        if (actionType == "cancelOrder")
        {
            var order = await _orders.GetLatestAsync(serialNumber, ct).ConfigureAwait(false);
            if (order is not null && ActiveStatuses.Contains(order.Status))
            {
                await _orders
                    .SaveAsync(order with { Status = RobotOrderStatuses.Cancelled }, ct)
                    .ConfigureAwait(false);
                await _traffic.ReleaseAsync(serialNumber, ct).ConfigureAwait(false);
            }
        }

        return actionId;
    }

    private static IReadOnlyList<RobotOrderNode> BuildNodes(
        string planJson,
        string orderId,
        OrderChunk chunk)
    {
        using var document = JsonDocument.Parse(planJson);
        var nodes = new List<RobotOrderNode>();
        var index = 0;
        foreach (var node in document.RootElement.GetProperty("nodes").EnumerateArray())
        {
            nodes.Add(new RobotOrderNode(
                orderId,
                node.GetProperty("sequenceId").GetInt32(),
                node.GetProperty("nodeId").GetString() ?? string.Empty,
                Released: index >= chunk.StartIndex && index <= chunk.EndIndex,
                Reached: false,
                ActionCount: node.GetProperty("actions").GetArrayLength()));
            index++;
        }

        return nodes;
    }

    private static int CountNodes(string planJson)
    {
        using var document = JsonDocument.Parse(planJson);
        return document.RootElement.GetProperty("nodes").GetArrayLength();
    }

    private static IReadOnlyList<RobotOrderNode> BuildNodesFromPlan(
        string planJson,
        string orderId,
        IReadOnlyCollection<string> reached)
    {
        using var document = JsonDocument.Parse(planJson);
        var result = new List<RobotOrderNode>();
        foreach (var node in document.RootElement.GetProperty("nodes").EnumerateArray())
        {
            var nodeId = node.GetProperty("nodeId").GetString() ?? string.Empty;
            result.Add(new RobotOrderNode(
                orderId,
                node.GetProperty("sequenceId").GetInt32(),
                nodeId,
                node.GetProperty("released").GetBoolean(),
                reached.Contains(nodeId),
                0));
        }

        return result;
    }

    private static IReadOnlyList<RobotOrderNode> MergeNodes(
        RobotOrder order,
        RobotSnapshot snapshot,
        out bool reachedLast)
    {
        reachedLast = false;

        if (order.Nodes.Count == 0)
        {
            return order.Nodes;
        }

        var byNode = snapshot.NodeStates
            .GroupBy(node => node.NodeId)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);

        // АС-8: released значит «разрешено войти», а не «проехал». Reached из released не выводится.
        var merged = order.Nodes
            .Select(node => byNode.TryGetValue(node.NodeId, out var state)
                ? node with { Released = node.Released || state.Released }
                : node)
            .ToList();

        // Достижение подтверждает только lastNodeId: все узлы до него включительно пройдены.
        // Первый узел плана считается стартом, а не прогрессом (строка 2 таблицы §3.1).
        if (!string.IsNullOrWhiteSpace(snapshot.LastNodeId))
        {
            // Узел может повторяться (стоянка в цели: узел позы и дубль с действием), поэтому
            // сначала ищем по паре nodeId + lastNodeSequenceId, затем только по nodeId.
            var reachedIndex = snapshot.LastNodeSequenceId is { } lastSequence
                ? merged.FindIndex(node =>
                    node.SequenceId == lastSequence
                    && string.Equals(node.NodeId, snapshot.LastNodeId, StringComparison.Ordinal))
                : -1;
            if (reachedIndex < 0)
            {
                reachedIndex = merged.FindIndex(node =>
                    string.Equals(node.NodeId, snapshot.LastNodeId, StringComparison.Ordinal));
            }

            for (var index = 1; index <= reachedIndex && index < merged.Count; index++)
            {
                merged[index] = merged[index] with { Reached = true };
            }
        }

        reachedLast = merged[^1].Reached;
        return merged;
    }

    /// <summary>
    /// Стартовый узел плеча: последний узел плана заказа (П-5). Робот мог не доехать до него
    /// или стоять между узлами — продолжение заказа все равно начинается там, где кончилось
    /// предыдущее сообщение, иначе робот отвергает update как невалидное продолжение.
    /// </summary>
    private static WarehouseNode? LegStartNode(WarehouseGraph graph, RobotOrder order)
    {
        var nodes = PlanNodeIds(order.PlanJson);
        if (nodes.Count == 0)
        {
            return null;
        }

        var last = nodes[^1];
        return graph.Nodes.Values.FirstOrDefault(node => string.Equals(node.NodeId, last, StringComparison.Ordinal));
    }

    /// <summary>
    /// В плане заказа уже есть действие этого типа в узле: повторно его не ставим.
    /// </summary>
    private static bool PlanHasAction(string? planJson, string nodeId, string actionType)
    {
        var root = ParsePlan(planJson);
        if (root is not { } plan
            || !plan.TryGetProperty("nodes", out var nodes)
            || nodes.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var node in nodes.EnumerateArray())
        {
            if (!node.TryGetProperty("nodeId", out var id)
                || !string.Equals(id.GetString(), nodeId, StringComparison.Ordinal)
                || !node.TryGetProperty("actions", out var actions)
                || actions.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var action in actions.EnumerateArray())
            {
                if (action.TryGetProperty("actionType", out var type)
                    && string.Equals(type.GetString(), actionType, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// В <c>actionStates</c> есть <c>FAILED</c> по действию из текущего плана заказа.
    /// Чужие (прошлых заказов) действия не учитываются.
    /// </summary>
    private static bool HasFailedPlanAction(RobotOrder order, RobotSnapshot snapshot)
    {
        var failed = snapshot.ActionStates?
            .Where(action => string.Equals(action.ActionStatus, "FAILED", StringComparison.OrdinalIgnoreCase))
            .Select(action => action.ActionId)
            .ToHashSet(StringComparer.Ordinal);
        if (failed is null || failed.Count == 0)
        {
            return false;
        }

        return PlanActionIds(order.PlanJson).Any(failed.Contains);
    }

    private static IEnumerable<string> PlanActionIds(string? planJson)
    {
        var root = ParsePlan(planJson);
        if (root is not { } plan || !plan.TryGetProperty("nodes", out var nodes) || nodes.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var ids = new List<string>();
        foreach (var node in nodes.EnumerateArray())
        {
            if (!node.TryGetProperty("actions", out var actions) || actions.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            ids.AddRange(actions.EnumerateArray()
                .Select(action => action.TryGetProperty("actionId", out var id) ? id.GetString() : null)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id!));
        }

        return ids;
    }

    private static JsonElement? ParsePlan(string? planJson)
    {
        if (string.IsNullOrWhiteSpace(planJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(planJson);
            return document.RootElement.ValueKind == JsonValueKind.Object ? document.RootElement.Clone() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// <c>sequenceId</c> последнего узла плана заказа: база нумерации следующего сообщения.
    /// Берется из <c>PlanJson</c> — полного плана плеча, а не из сохраненных узлов: узлы
    /// описывают только отправленный кусок, и его последний узел может быть серединой маршрута.
    /// </summary>
    private static int? LastPlanSequenceId(RobotOrder order) =>
        PlanSequenceRange(order.PlanJson)?.Last
        ?? (order.Nodes.Count > 0 ? order.Nodes[^1].SequenceId : null);

    /// <summary>Первый и последний <c>sequenceId</c> узлов сообщения.</summary>
    private static (int First, int Last)? PlanSequenceRange(string? planJson)
    {
        var root = ParsePlan(planJson);
        if (root is not { } plan
            || !plan.TryGetProperty("nodes", out var nodes)
            || nodes.ValueKind != JsonValueKind.Array
            || nodes.GetArrayLength() == 0)
        {
            return null;
        }

        static int Sequence(JsonElement node) =>
            node.TryGetProperty("sequenceId", out var id) && id.TryGetInt32(out var value) ? value : 0;

        return (Sequence(nodes[0]), Sequence(nodes[nodes.GetArrayLength() - 1]));
    }

    /// <summary>Карта плана: <c>nodePosition.mapId</c> первого узла.</summary>
    private static string? PlanMapId(string? planJson)
    {
        var root = ParsePlan(planJson);
        if (root is not { } plan
            || !plan.TryGetProperty("nodes", out var nodes)
            || nodes.ValueKind != JsonValueKind.Array
            || nodes.GetArrayLength() == 0
            || !nodes[0].TryGetProperty("nodePosition", out var position)
            || !position.TryGetProperty("mapId", out var mapId))
        {
            return null;
        }

        return mapId.GetString();
    }

    private void RememberUpdate(RobotOrder order, int updateId, DateTimeOffset at) =>
        _updates[order.OrderId] = new UpdatePublication(order.AgvSerialNumber, order.OrderId, updateId, at);

    /// <summary>
    /// Журнал каждой публикации: история плеч в базе не хранится, разбор идет по этим записям.
    /// </summary>
    private void LogPublication(string orderId, int updateId, string messageJson, string? legTarget, string kind)
    {
        var range = PlanSequenceRange(messageJson);
        _logger.LogInformation(
            "Публикация {Kind}: заказ {OrderId}, orderUpdateId={UpdateId}, sequenceId {First}..{Last}, цель плеча {Target}",
            kind,
            orderId,
            updateId,
            range?.First,
            range?.Last,
            legTarget);
    }

    private static string? LastPlanNodeId(RobotOrder order)
    {
        if (order.Nodes.Count > 0)
        {
            return order.Nodes[^1].NodeId;
        }

        var nodes = PlanNodeIds(order.PlanJson);
        return nodes.Count > 0 ? nodes[^1] : null;
    }

    /// <summary>
    /// Робот сдвинулся с первого узла плана: нужно для выбора <c>Rejected</c> либо <c>Failed</c>
    /// при блокирующей ошибке (строки 2 и 3 таблицы §3.1).
    /// </summary>
    private static bool HasMovedFromFirstNode(RobotOrder order, string? lastNodeId)
    {
        if (string.IsNullOrWhiteSpace(lastNodeId))
        {
            return false;
        }

        var first = order.Nodes.Count > 0
            ? order.Nodes[0].NodeId
            : PlanNodeIds(order.PlanJson).FirstOrDefault();

        return first is not null && !string.Equals(first, lastNodeId, StringComparison.Ordinal);
    }

    private static IReadOnlyList<string> PlanNodeIds(string? planJson)
    {
        if (string.IsNullOrWhiteSpace(planJson))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(planJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("nodes", out var nodes)
                || nodes.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            return nodes.EnumerateArray()
                .Select(node => node.TryGetProperty("nodeId", out var id) ? id.GetString() : null)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id!)
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// Робот выполнил <c>cancelOrder</c>: строка 8 таблицы §3.1.
    /// </summary>
    private static bool HasFinishedCancelAction(RobotSnapshot snapshot) =>
        snapshot.ActionStates?.Any(action =>
            string.Equals(action.ActionType, "cancelOrder", StringComparison.OrdinalIgnoreCase)
            && string.Equals(action.ActionStatus, "FINISHED", StringComparison.OrdinalIgnoreCase)) == true;

    /// <summary>
    /// Робот выполняет действие: заказ не закрывается, пока действие в <c>RUNNING</c>/<c>WAITING</c>.
    /// </summary>
    private static bool HasRunningAction(RobotSnapshot snapshot) =>
        snapshot.ActionStates?.Any(action =>
            string.Equals(action.ActionStatus, "RUNNING", StringComparison.OrdinalIgnoreCase)
            || string.Equals(action.ActionStatus, "WAITING", StringComparison.OrdinalIgnoreCase)) == true;

    /// <summary>
    /// Запоминает прогресс заказа для <c>PROGRESS_TIMEOUT</c>: таймаут считается от последнего
    /// сдвига <c>lastNodeId</c>/<c>lastNodeSequenceId</c>, а не от <c>AcceptedAt</c> (АС-9).
    /// </summary>
    private void TrackProgress(RobotOrder order, RobotSnapshot snapshot, DateTimeOffset now)
    {
        if (!string.Equals(order.Status, RobotOrderStatuses.Executing, StringComparison.Ordinal))
        {
            return;
        }

        if (_progress.TryGetValue(order.OrderId, out var tracked)
            && string.Equals(tracked.LastNodeId, snapshot.LastNodeId, StringComparison.Ordinal)
            && tracked.LastNodeSequenceId == snapshot.LastNodeSequenceId)
        {
            return;
        }

        _progress[order.OrderId] = new OrderProgress(snapshot.LastNodeId, snapshot.LastNodeSequenceId, now);
    }

    /// <summary>
    /// Снимок для трафика при выдаче заказа: горизонт нового плана считается от его начала.
    /// </summary>
    private static RobotSnapshot TrafficSnapshot(
        string serialNumber,
        string mapId,
        RobotSnapshot? snapshot,
        DateTimeOffset now) =>
        (snapshot ?? new RobotSnapshot(
            serialNumber,
            "UNKNOWN",
            false,
            null,
            0,
            0,
            0,
            0,
            mapId,
            0,
            [],
            [],
            now,
            string.Empty))
        with
        {
            MapId = mapId,
            LastNodeSequenceId = 0,
        };

    /// <summary>Последний замеченный прогресс заказа.</summary>
    private sealed record OrderProgress(string? LastNodeId, int? LastNodeSequenceId, DateTimeOffset At);

    /// <summary>Опубликованное обновление заказа: робот, заказ, версия и момент публикации.</summary>
    private sealed record UpdatePublication(string Serial, string OrderId, int UpdateId, DateTimeOffset At);
}

/// <summary>
/// У робота уже есть незавершенный заказ.
/// </summary>
public sealed class OrderInProgressException : Exception
{
    /// <summary>Создает исключение.</summary>
    /// <param name="orderId">Активный заказ.</param>
    /// <param name="status">Его статус.</param>
    public OrderInProgressException(string orderId, string status)
        : base($"У робота активен заказ {orderId} в статусе {status}.")
    {
        OrderId = orderId;
        Status = status;
    }

    /// <summary>Активный заказ.</summary>
    public string OrderId { get; }

    /// <summary>Статус активного заказа.</summary>
    public string Status { get; }
}

/// <summary>
/// Заказ не создан из-за карты, узла или маршрута.
/// </summary>
public sealed class OrderRejectedException : Exception
{
    /// <summary>Создает исключение.</summary>
    /// <param name="reason">Код причины.</param>
    /// <param name="message">Текст.</param>
    public OrderRejectedException(string reason, string message)
        : base(message)
    {
        Reason = reason;
    }

    /// <summary>Код причины.</summary>
    public string Reason { get; }
}
