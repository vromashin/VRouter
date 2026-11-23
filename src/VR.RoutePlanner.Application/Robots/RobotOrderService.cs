using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VR.RoutePlanner.Application.Configuration;
using VR.RoutePlanner.Application.Graph;
using VR.RoutePlanner.Application.Planning;
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

    private readonly IPathPlanner _planner;
    private readonly IVdaOrderComposer composer;
    private readonly IHeaderIdSource _headers;
    private readonly IWarehouseGraphRepository _graphs;
    private readonly IRobotOrderNumberSource _numbers;
    private readonly IRobotOrderPublisher _publisher;
    private readonly IRobotOrderRepository _orders;
    private readonly IRobotStateReader _states;
    private readonly IOptions<RoutePlannerOptions> _options;
    private readonly TimeProvider _time;
    private readonly ILogger<RobotOrderService> _logger;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastUpdate = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _lastUpdateSequence = new(StringComparer.Ordinal);

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

        _planner = planner;
        this.composer = composer;
        _headers = headers;
        _graphs = graphs;
        _numbers = numbers;
        _publisher = publisher;
        _orders = orders;
        _states = states;
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

        // Слот рабочий узел не проезжаем: маршрут строим до соседнего узла, действие выполняем там.
        var slotNode = to;
        var stopBeforeSlot = options.Order.StopBeforeSlot
            && slotNode.NodeId.StartsWith(SlotApproachResolver.SlotPrefix, StringComparison.OrdinalIgnoreCase);
        if (stopBeforeSlot)
        {
            var robotX = snapshot?.PositionX ?? from.X;
            var robotY = snapshot?.PositionY ?? from.Y;
            // Предпочитаем соседа, ребро которого направлено В слот: тогда робот едет по ребру
            // вперед и приезжает лицом к стеллажу, а не кормой.
            var candidates = graph.Edges.Values
                .SelectMany(list => list)
                .Where(edge => edge.FromNodeId == slotNode.Id || edge.ToNodeId == slotNode.Id)
                .Select(edge => new
                {
                    NodeId = edge.FromNodeId == slotNode.Id ? edge.ToNodeId : edge.FromNodeId,
                    IntoSlot = edge.ToNodeId == slotNode.Id,
                })
                .Distinct()
                .Select(item => new
                {
                    Node = graph.Nodes.GetValueOrDefault(item.NodeId),
                    item.IntoSlot,
                })
                .Where(item => item.Node is not null
                    && !item.Node.NodeId.StartsWith(SlotApproachResolver.SlotPrefix, StringComparison.OrdinalIgnoreCase))
                .OrderBy(item => Math.Sqrt(Math.Pow(item.Node!.X - slotNode.X, 2) + Math.Pow(item.Node!.Y - slotNode.Y, 2)))
                .ThenBy(item => item.IntoSlot ? 0 : 1)
                .ToList();

            var neighbor = candidates.FirstOrDefault()?.Node;

            if (neighbor is not null)
            {
                to = neighbor;
            }
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

        // Тип действия берем из типа узла, как старый AGVControl: LOADING это подбор, UNLOADING выгрузка.
        var actionType = slotNode.NodeType?.ToUpperInvariant() switch
        {
            "LOADING" => "pick",
            "UNLOADING" => "drop",
            _ => hasLoad ? "drop" : "pick",
        };
        var orderJson = composer.Compose(
            plan.Route,
            graph,
            mapId,
            orderId,
            options.Order.Manufacturer,
            serialNumber,
            number,
            _headers.Next(),
            options.Order.ReleasedNodes,
            now,
            snapshot?.PositionX,
            snapshot?.PositionY,
            snapshot?.Theta,
            actionType,
            orderId,
            slotNode.X,
            slotNode.Y,
            Math.Atan2(slotNode.Y - to.Y, slotNode.X - to.X));

        await _publisher
            .PublishAsync(options.Order.Manufacturer, serialNumber, orderJson, ct)
            .ConfigureAwait(false);

        var nodes = BuildNodes(orderJson, orderId, options.Order.ReleasedNodes);
        var order = new RobotOrder(
            orderId,
            serialNumber,
            0,
            RobotOrderStatuses.Sent,
            orderJson,
            Math.Min(options.Order.ReleasedNodes, nodes.Count),
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
            "Заказ {OrderId} роботу {Serial}: узлов {Nodes}, длина {Length:F2} м",
            orderId,
            serialNumber,
            nodes.Count,
            plan.Route.TotalLengthMeters);

        return order;
    }

    /// <summary>
    /// Обновляет статус заказа по сообщению <c>state</c>.
    /// </summary>
    /// <param name="snapshot">Снимок робота.</param>
    /// <param name="ct">Токен отмены.</param>
    /// <returns>Заказ после обновления либо <c>null</c>, если заказ не найден.</returns>
    public async Task<RobotOrder?> ApplyStateAsync(RobotSnapshot snapshot, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (string.IsNullOrWhiteSpace(snapshot.OrderId))
        {
            return null;
        }

        var order = await _orders.GetAsync(snapshot.OrderId, ct).ConfigureAwait(false);
        if (order is null)
        {
            return null;
        }

        var now = _time.GetUtcNow();
        var status = order.Status;
        var failure = order.FailureReason;
        DateTimeOffset? acceptedAt = order.AcceptedAt;
        DateTimeOffset? completedAt = order.CompletedAt;

        if (snapshot.OrderUpdateId < order.OrderUpdateId)
        {
            return order;
        }

        if (RobotErrorClassifier.HasBlockingError(snapshot) && ActiveStatuses.Contains(status))
        {
            status = RobotOrderStatuses.Failed;
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

        if (status == RobotOrderStatuses.Executing && reachedLast && !snapshot.Driving)
        {
            status = RobotOrderStatuses.Completed;
            completedAt = now;
        }

        if (status == order.Status
            && nodes.SequenceEqual(order.Nodes)
            && acceptedAt == order.AcceptedAt
            && completedAt == order.CompletedAt
            && failure == order.FailureReason
            && status != RobotOrderStatuses.Executing)
        {
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

        if (status == RobotOrderStatuses.Executing)
        {
            if (_options.Value.Order.ReleasedNodes > 0)
            {
                updated = await ExtendPlanAsync(updated, snapshot, ct).ConfigureAwait(false);
            }
            nodes = updated.Nodes;
        }

        await _orders.SaveAsync(updated, ct).ConfigureAwait(false);
        _logger.LogInformation("Заказ {OrderId}: статус {Status}", updated.OrderId, updated.Status);
        return updated;
    }

    /// <summary>
    /// Публикует обновление заказа, когда базовый план почти исчерпан: горизонт сдвигается
    /// на следующие узлы, <c>orderUpdateId</c> растет.
    /// </summary>
    private async Task<RobotOrder> ExtendPlanAsync(RobotOrder order, RobotSnapshot snapshot, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(order.PlanJson))
        {
            return order;
        }

        var reached = order.Nodes.Where(node => node.Reached).Select(node => node.NodeId).ToHashSet(StringComparer.Ordinal);
        // AGV выставляет newBaseRequest, когда его базовый план исчерпан: как в AGVControl,
        // строим продолжение от последнего достигнутого узла, не дожидаясь счётчика узлов.
        var remaining = snapshot.NewBaseRequest ? 0 : snapshot.LastNodeSequenceId is { } lastSeq
            ? VdaOrderUpdater.CountUnreachedReleasedAfterSequence(order.PlanJson, lastSeq)
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

        var releasedNodes = _options.Value.Order.ReleasedNodes;
        var updateJson = VdaOrderUpdater.BuildUpdate(
            order.PlanJson,
            order.OrderUpdateId + 1,
            releasedNodes,
            _headers.Next(),
            now,
            snapshot.LastNodeId,
            snapshot.LastNodeSequenceId);

        if (updateJson is null)
        {
            return order;
        }

        await _publisher
            .PublishAsync(_options.Value.Order.Manufacturer, order.AgvSerialNumber, updateJson, ct)
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
            ReleasedNodeCount = Math.Min(releasedNodes, updatedNodes.Count),
            Nodes = updatedNodes,
        };
    }

    /// <summary>
    /// Проверяет таймауты активных заказов роботов.
    /// </summary>
    /// <param name="ct">Токен отмены.</param>
    /// <returns>Сколько заказов провалено по таймауту.</returns>
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

        _logger.LogInformation(
            "Робот {Serial} выровнен по узлу {Node}: расхождение {Distance:F2} м",
            serialNumber,
            node.NodeId,
            distance);

        await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
        return await _states.GetLatestAsync(serialNumber, ct).ConfigureAwait(false) ?? snapshot;
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
                && (now - (order.AcceptedAt ?? order.SentAt ?? order.CreatedAt)).TotalSeconds
                    > options.ProgressTimeoutSeconds)
            {
                reason = ReasonProgressTimeout;
            }

            if (reason is null)
            {
                continue;
            }

            await _orders
                .SaveAsync(order with { Status = RobotOrderStatuses.Failed, FailureReason = reason }, ct)
                .ConfigureAwait(false);
            _logger.LogWarning("Заказ {OrderId} провален: {Reason}", order.OrderId, reason);
            failed++;
        }

        return failed;
    }

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
                _logger.LogInformation("Заказ {OrderId} продолжен после рестарта", order.OrderId);
                await ApplyStateAsync(snapshot, ct).ConfigureAwait(false);
                continue;
            }

            await _orders
                .SaveAsync(
                    order with { Status = RobotOrderStatuses.Failed, FailureReason = ReasonStateMismatch },
                    ct)
                .ConfigureAwait(false);
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
            }
        }

        return actionId;
    }

    private static IReadOnlyList<RobotOrderNode> BuildNodes(
        string orderJson,
        string orderId,
        int releasedNodes)
    {
        using var document = JsonDocument.Parse(orderJson);
        var nodes = new List<RobotOrderNode>();
        var index = 0;
        foreach (var node in document.RootElement.GetProperty("nodes").EnumerateArray())
        {
            nodes.Add(new RobotOrderNode(
                orderId,
                node.GetProperty("sequenceId").GetInt32(),
                node.GetProperty("nodeId").GetString() ?? string.Empty,
                Released: index < releasedNodes,
                Reached: false,
                ActionCount: node.GetProperty("actions").GetArrayLength()));
            index++;
        }

        return nodes;
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
        if (snapshot.NodeStates.Count == 0)
        {
            // Узлы в состоянии закончились: последний узел заказа считается достигнутым,
            // если робот сообщает его как lastNodeId (так робот отмечает финиш).
            var lastOrderNode = order.Nodes.Count > 0 ? order.Nodes[^1] : null;
            reachedLast = (lastOrderNode is not null && lastOrderNode.Reached)
                || (lastOrderNode is not null
                    && !string.IsNullOrWhiteSpace(snapshot.LastNodeId)
                    && string.Equals(lastOrderNode.NodeId, snapshot.LastNodeId, StringComparison.Ordinal));

            return order.Nodes;
        }

        var byNode = snapshot.NodeStates
            .GroupBy(node => node.NodeId)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);

        var merged = order.Nodes
            .Select(node => byNode.TryGetValue(node.NodeId, out var state)
                ? string.IsNullOrWhiteSpace(snapshot.LastNodeId)
                    ? node with { Released = node.Released || state.Released, Reached = node.Reached || state.Released }
                    : node with { Released = node.Released || state.Released }
                : node)
            .ToList();

        // Достижение подтверждает lastNodeId из состояния: все узлы до него включительно пройдены,
        // а released значит лишь готовность узла и не является признаком проезда.
        if (!string.IsNullOrWhiteSpace(snapshot.LastNodeId))
        {
            var reachedIndex = merged.FindIndex(node =>
                string.Equals(node.NodeId, snapshot.LastNodeId, StringComparison.Ordinal));

            for (var i = 0; i <= reachedIndex && i < merged.Count; i++)
            {
                merged[i] = merged[i] with { Reached = true };
            }
        }

        reachedLast = merged.Count > 0 && merged[^1].Reached;
        return merged;
    }
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
