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

    private static readonly TimeSpan UpdateThrottle = TimeSpan.FromSeconds(2);

    private readonly ISimulatorResetService? _simulatorReset;

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
    /// <param name="simulatorReset">Сброс симулятора робота, если он включен в конфигурации.</param>
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
        _simulatorReset = simulatorReset;
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
    /// Создает и публикует заказ роботу.
    /// </summary>
    /// <param name="mapId">Карта.</param>
    /// <param name="serialNumber">Серийный номер робота.</param>
    /// <param name="targetNodeId">Целевой узел.</param>
    /// <param name="hasLoad">Робот везет груз.</param>
    /// <param name="ct">Токен отмены.</param>
    /// <returns>Созданный заказ.</returns>
    /// <exception cref="OrderInProgressException">У робота уже есть незавершенный заказ.</exception>
    /// <exception cref="OrderRejectedException">Заказ не создан: карта, узел или маршрут недоступны.</exception>
    public async Task<RobotOrder> CreateOrderAsync(
        string mapId,
        string serialNumber,
        string targetNodeId,
        bool hasLoad,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mapId);
        ArgumentException.ThrowIfNullOrWhiteSpace(serialNumber);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetNodeId);

        var options = _options.Value;
        var current = await _orders.GetLatestAsync(serialNumber, ct).ConfigureAwait(false);
        if (current is not null && ActiveStatuses.Contains(current.Status))
        {
            throw new OrderInProgressException(current.OrderId, current.Status);
        }

        var snapshot = await _states.GetLatestAsync(serialNumber, ct).ConfigureAwait(false);

        // Симулятор и робот держат orderId прошлого заказа: пока он не очищен, новый заказ
        // получает NO_ACCEPT. Как в AGVControl, сначала снимаем прежний заказ и ждём очистки.
        // Симулятор держит orderId и узлы прошлого заказа; пока они не очищены, новый заказ
        // принимается молча и не выполняется. Как AGVControl, снимаем заказ и ждём очистки.
        var hasLeftovers = !string.IsNullOrWhiteSpace(snapshot?.OrderId)
            || (snapshot?.NodeStates.Count ?? 0) > 0;

        if (hasLeftovers)
        {
            var cancelPayload = VdaInstantActionPayload.Build(
                "cancelOrder",
                Guid.NewGuid().ToString("N"),
                _time.GetUtcNow());
            await _publisher
                .PublishInstantActionAsync(options.Order.Manufacturer, serialNumber, cancelPayload, ct)
                .ConfigureAwait(false);

            var cleanupDeadline = _time.GetUtcNow().AddSeconds(15);
            while (_time.GetUtcNow() < cleanupDeadline && !ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
                snapshot = await _states.GetLatestAsync(serialNumber, ct).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(snapshot?.OrderId) && (snapshot?.NodeStates.Count ?? 0) == 0)
                {
                    break;
                }
            }

            if (!string.IsNullOrWhiteSpace(snapshot?.OrderId) || (snapshot?.NodeStates.Count ?? 0) > 0)
            {
                // Симулятор не очищает состояние сам: перезапускаем его, если это разрешено настройкой.
                if (_simulatorReset is { Enabled: true })
                {
                    await _simulatorReset.ResetAsync(ct).ConfigureAwait(false);
                    snapshot = await _states.GetLatestAsync(serialNumber, ct).ConfigureAwait(false);
                }
            }

            if (!string.IsNullOrWhiteSpace(snapshot?.OrderId) || (snapshot?.NodeStates.Count ?? 0) > 0)
            {
                // Симулятор оставляет orderId последнего заказа навсегда: не блокируем новый заказ,
                // робот принимает его и при незавершённом прошлом заказе (проверено на стенде).
                _logger.LogWarning(
                    "Робот {Serial} не очистил состояние (orderId={OrderId}, узлов {Nodes}), продолжаем",
                    serialNumber,
                    snapshot?.OrderId,
                    snapshot?.NodeStates.Count ?? 0);
            }
        }
        var graph = await _graphs.LoadAsync(mapId, ct).ConfigureAwait(false);
        if (graph is null)
        {
            throw new OrderRejectedException(RoutePlanReason.MapNotFound, $"Карта '{mapId}' не найдена.");
        }

        // Симулятор накапливает расхождение позы с узлом: VDA-действие initPosition возвращает
        // робота в координаты узла lastNodeId, иначе он «едет», но не доходит до узла.
        snapshot = await CorrectPoseAsync(serialNumber, snapshot, graph, ct).ConfigureAwait(false);

        var from = RobotStartNodeResolver.Resolve(graph, snapshot);
        var to = graph.Nodes.Values.FirstOrDefault(node => node.NodeId == targetNodeId);
        if (from is null || to is null)
        {
            throw new OrderRejectedException(RoutePlanReason.NodeNotFound, "Узел старта или цели не найден на карте.");
        }

        if (from.Id == to.Id)
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

        var plan = await _planner
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
            SendForkActions: options.Order.SendForkActions,
            HasLoad: hasLoad,
            FirstNodeTheta: options.Order.FirstNodeTheta,
            RobotX: snapshot?.PositionX,
            RobotY: snapshot?.PositionY,
            RobotTheta: snapshot?.Theta,
            TerminalActionType: options.Order.TerminalActionType,
            ApproachTarget: approachTarget);

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
            to.NodeId,
            plan.ApproachNodeId,
            now,
            now,
            null,
            null,
            null,
            nodes);

        await _orders.SaveAsync(order, ct).ConfigureAwait(false);
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

        // Закрытие заказа: робот стоит на последнем узле плана или на цели, не едет и не выполняет
        // действие. Пустые nodeStates/edgeStates доказательством финиша не считаются (АС-1, Д-2).
        var reachedTarget = !string.IsNullOrWhiteSpace(order.TargetNodeId)
            && string.Equals(snapshot.LastNodeId, order.TargetNodeId, StringComparison.Ordinal);

        if (status == RobotOrderStatuses.Executing
            && !snapshot.Driving
            && !HasRunningAction(snapshot)
            && (reachedLast || reachedTarget || IsLastPlanNode(order, snapshot.LastNodeId)))
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

        var finished = !string.IsNullOrWhiteSpace(snapshot.LastNodeId)
            && (string.Equals(snapshot.LastNodeId, order.TargetNodeId, StringComparison.Ordinal)
                || IsLastPlanNode(order, snapshot.LastNodeId));

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
    /// Выравнивает позу робота по узлу lastNodeId, если симулятор накопил расхождение.
    /// </summary>
    /// <param name="serialNumber">Серийный номер робота.</param>
    /// <param name="snapshot">Текущее состояние робота.</param>
    /// <param name="graph">Граф карты.</param>
    /// <param name="ct">Отмена.</param>
    /// <returns>Обновленное состояние.</returns>
    private async Task<RobotSnapshot?> CorrectPoseAsync(
        string serialNumber,
        RobotSnapshot? snapshot,
        WarehouseGraph graph,
        CancellationToken ct)
    {
        if (snapshot is null || string.IsNullOrWhiteSpace(snapshot.LastNodeId))
        {
            return snapshot;
        }

        var node = graph.Nodes.Values
            .FirstOrDefault(item => string.Equals(item.NodeId, snapshot.LastNodeId, StringComparison.Ordinal));
        if (node is null)
        {
            return snapshot;
        }

        var distance = Math.Sqrt(
            Math.Pow(node.X - snapshot.PositionX, 2) + Math.Pow(node.Y - snapshot.PositionY, 2));
        if (distance <= 0.3)
        {
            return snapshot;
        }

        // Симулятор игнорирует initPosition, если поза уже разошлась: надежный путь —
        // перезапуск с выровненным файлом состояния (настройка Legacy:SimulatorUnit).
        if (_simulatorReset is { Enabled: true })
        {
            _logger.LogWarning(
                "Поза робота {Serial} расходится с узлом {Node} на {Distance:F2} м: перезапуск симулятора с выровненным состоянием",
                serialNumber,
                node.NodeId,
                distance);
            await _simulatorReset.ResetAsync(ct).ConfigureAwait(false);
            return await _states.GetLatestAsync(serialNumber, ct).ConfigureAwait(false) ?? snapshot;
        }

        // initPosition применяется только на свободном роботе: ждем, пока он не едет и без действий.
        var idleDeadline = _time.GetUtcNow().AddSeconds(15);
        while (_time.GetUtcNow() < idleDeadline && !ct.IsCancellationRequested)
        {
            if (!snapshot.Driving && (snapshot.ActionStates?.Count ?? 0) == 0)
            {
                break;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
            snapshot = await _states.GetLatestAsync(serialNumber, ct).ConfigureAwait(false) ?? snapshot;
        }

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var parameters = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["x"] = node.X,
                ["y"] = node.Y,
                ["theta"] = snapshot.Theta,
                ["mapId"] = snapshot.MapId ?? string.Empty,
            };

            var payload = VdaInstantActionPayload.Build(
                "initPosition",
                Guid.NewGuid().ToString("N"),
                _time.GetUtcNow(),
                parameters);

            await _publisher
                .PublishInstantActionAsync(_options.Value.Order.Manufacturer, serialNumber, payload, ct)
                .ConfigureAwait(false);

            await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
            var afterAttempt = await _states.GetLatestAsync(serialNumber, ct).ConfigureAwait(false) ?? snapshot;
            var moved = Math.Sqrt(
                Math.Pow(node.X - afterAttempt.PositionX, 2) + Math.Pow(node.Y - afterAttempt.PositionY, 2)) <= 0.3;

            _logger.LogInformation(
                "initPosition попытка {Attempt}: робот {Serial}, узел {Node}, расхождение {Distance:F2} м, применено={Applied}",
                attempt,
                serialNumber,
                node.NodeId,
                distance,
                moved);

            if (moved)
            {
                return afterAttempt;
            }

            snapshot = afterAttempt;
        }

        if (_simulatorReset is { Enabled: true })
        {
            // Симулятор игнорирует initPosition: перезапускаем его с выровненным файлом состояния.
            _logger.LogWarning(
                "initPosition не применился (робот {Serial} на {Node}), перезапускаем симулятор",
                serialNumber,
                node.NodeId);
            await _simulatorReset.ResetAsync(ct).ConfigureAwait(false);
            return await _states.GetLatestAsync(serialNumber, ct).ConfigureAwait(false) ?? snapshot;
        }

        return snapshot;
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
            var reachedIndex = merged.FindIndex(node =>
                string.Equals(node.NodeId, snapshot.LastNodeId, StringComparison.Ordinal));

            for (var index = 1; index <= reachedIndex && index < merged.Count; index++)
            {
                merged[index] = merged[index] with { Reached = true };
            }
        }

        reachedLast = merged[^1].Reached;
        return merged;
    }

    /// <summary>
    /// Последний узел плана заказа: по сохраненным узлам, иначе из <c>PlanJson</c>.
    /// </summary>
    private static bool IsLastPlanNode(RobotOrder order, string? nodeId)
    {
        if (string.IsNullOrWhiteSpace(nodeId))
        {
            return false;
        }

        var last = LastPlanNodeId(order);
        return last is not null && string.Equals(last, nodeId, StringComparison.Ordinal);
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
