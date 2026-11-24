using System.Text.Json;
using Confluent.Kafka;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VR.RoutePlanner.Application.Robots;
using VR.RoutePlanner.Domain.Robots;
using VR.RoutePlanner.Infrastructure.Legacy;

namespace VR.RoutePlanner.Infrastructure.Kafka;

/// <summary>
/// Читает задачи WMS и исполняет их роботом: подбор в первом узле, выгрузка во втором.
/// События выполнения публикуются в топик <c>tasks-events</c>.
/// </summary>
public sealed class SimpleTaskWorker(
    IOptions<KafkaOptions> options,
    RobotOrderService orders,
    ILegacyAgvRepository agvs,
    IOptions<Application.Configuration.RoutePlannerOptions> orderOptions,
    ILogger<SimpleTaskWorker> logger) : BackgroundService
{
    private readonly IOptions<Application.Configuration.RoutePlannerOptions> _orderOptions = orderOptions;

    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        if (!settings.Enabled)
        {
            logger.LogInformation("Чтение задач WMS выключено");
            return;
        }

        await Task.Yield();

        using var consumer = new ConsumerBuilder<string, string>(new ConsumerConfig
        {
            BootstrapServers = settings.BootstrapServers,
            GroupId = settings.GroupId,
            AutoOffsetReset = Enum.TryParse<AutoOffsetReset>(settings.AutoOffsetReset, ignoreCase: true, out var reset)
                ? reset
                : AutoOffsetReset.Latest,
            EnableAutoCommit = true,
        }).Build();

        using var producer = new ProducerBuilder<string, string>(new ProducerConfig
        {
            BootstrapServers = settings.BootstrapServers,
        }).Build();

        consumer.Subscribe(settings.TasksTopic);
        logger.LogInformation("Чтение задач WMS включено: {Topic} через {Brokers}", settings.TasksTopic, settings.BootstrapServers);

        while (!stoppingToken.IsCancellationRequested)
        {
            ConsumeResult<string, string>? result = null;
            try
            {
                result = consumer.Consume(TimeSpan.FromSeconds(1));
            }
            catch (ConsumeException exception)
            {
                logger.LogWarning(exception, "Ошибка чтения задач WMS");
                await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken).ConfigureAwait(false);
                continue;
            }

            if (result?.Message?.Value is null)
            {
                continue;
            }

            var task = WmsTaskParser.Parse(result.Message.Value);
            if (task is not null && task.CreatedAt != DateTimeOffset.MinValue && task.CreatedAt < _startedAt.AddMinutes(-1))
            {
                // Старые задачи из истории топика не выполняем: робот уже не там, узлы могли измениться.
                logger.LogInformation("Задача {TaskId} пропущена: создана до запуска сервиса", task.TaskId);
                continue;
            }

            if (task is null)
            {
                logger.LogWarning("Задача WMS не разобрана: {Payload}", result.Message.Value);
                continue;
            }

            await ExecuteAsync(task, producer, settings, stoppingToken).ConfigureAwait(false);
        }

        consumer.Close();
    }

    private async Task ExecuteAsync(
        WmsTask task,
        IProducer<string, string> producer,
        KafkaOptions settings,
        CancellationToken ct)
    {
        logger.LogInformation(
            "Задача {TaskId}: действий {Count}, первое {First}",
            task.TaskId,
            task.Actions.Count,
            task.Actions[0].Location);

        await PublishAsync(producer, settings, new { TaskId = task.TaskId, Status = "Running", AgvId = await ResolveAgvIdAsync(ct).ConfigureAwait(false) }, ct)
            .ConfigureAwait(false);

        try
        {
            var order = await PublishLegsAsync(task, ct).ConfigureAwait(false);

            var outcome = await WaitCompletionAsync(order.OrderId, ct).ConfigureAwait(false);
            if (!string.Equals(outcome, RobotOrderStatuses.Completed, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Заказ {order.OrderId} завершился со статусом {outcome}.");
            }

            await PublishAsync(producer, settings, new { TaskId = task.TaskId, Status = "Finished", AgvId = await ResolveAgvIdAsync(ct).ConfigureAwait(false) }, ct)
                .ConfigureAwait(false);
            logger.LogInformation("Задача {TaskId}: выполнена", task.TaskId);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Задача {TaskId}: ошибка исполнения", task.TaskId);
            await PublishAsync(producer, settings, new { TaskId = task.TaskId, Status = "Failed", AgvId = await ResolveAgvIdAsync(ct).ConfigureAwait(false) }, ct)
                .ConfigureAwait(false);
        }
    }

    /// <summary>Шаг опроса состояния заказа. В тестах уменьшается.</summary>
    internal TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Один VDA-заказ на задачу: первое действие создает заказ (сообщение 0 несет только
    /// движение), каждое следующее уходит как <see cref="RobotOrderService.AppendLegAsync"/>
    /// сразу после последнего чанка предыдущего плеча, без ожидания подтверждения по <c>state</c>.
    /// Действие вил ставится в сообщении, которое начинается на узле-якоре (П-2). Возвращает
    /// заказ после публикации последнего плеча.
    /// </summary>
    /// <param name="task">Задача WMS.</param>
    /// <param name="ct">Токен отмены.</param>
    /// <returns>Заказ задачи.</returns>
    internal async Task<RobotOrder> PublishLegsAsync(WmsTask task, CancellationToken ct)
    {
        var serial = await ResolveSerialAsync(ct).ConfigureAwait(false);
        var finalTarget = task.Actions[^1].Location;
        var sendFork = _orderOptions.Value.Order.SendForkActions;
        RobotOrder? order = null;
        string? previousTarget = null;

        for (var index = 0; index < task.Actions.Count; index++)
        {
            var action = task.Actions[index];
            var hasLoad = action.ActionType != WmsTaskParser.PickUp;
            var last = index == task.Actions.Count - 1;

            // Действие вил на узле-якоре — действие предыдущего плеча задачи: оно уходит
            // в сообщении, которое начинается на узле, где робот его выполняет.
            var anchorAction = index > 0 && sendFork
                ? ForkActionType(task.Actions[index - 1].ActionType)
                : null;

            // Действие самого плеча ставится только в последнем плече задачи: за ним нет
            // сообщения, которое началось бы на его узле.
            var targetAction = last && sendFork ? ForkActionType(action.ActionType) : null;

            if (order is null || order.Status == RobotOrderStatuses.Completed)
            {
                // Completed здесь только у заказа, закрытого сразу (робот уже в нерабочем узле):
                // следующему действию нужен новый заказ.
                await WaitFreeAsync(ct).ConfigureAwait(false);
                order = await orders
                    .CreateTaskOrderAsync(task.AreaWmsId, serial, action.Location, hasLoad, finalTarget, targetAction, ct)
                    .ConfigureAwait(false);
            }
            else
            {
                // Следующее плечо — сразу после последнего чанка предыдущего: продолжение заказа
                // должно начинаться на последнем отправленном узле, иначе робот его отвергает.
                await WaitLegReleasedAsync(order.OrderId, previousTarget!, ct).ConfigureAwait(false);
                order = await orders
                    .AppendLegAsync(order.OrderId, action.Location, hasLoad, anchorAction, targetAction, ct)
                    .ConfigureAwait(false);
            }

            previousTarget = action.Location;
            logger.LogInformation(
                "Задача {TaskId}: шаг {Location} под заказом {OrderId}, версия {Version}",
                task.TaskId,
                action.Location,
                order.OrderId,
                order.OrderUpdateId);
        }

        return order!;
    }

    /// <summary>Тип действия вил задачи WMS в терминах VDA 5050.</summary>
    private static string ForkActionType(int actionType) =>
        actionType == WmsTaskParser.PickUp ? "pick" : "drop";

    /// <summary>
    /// Ждет, пока уйдет последний чанк плеча: сообщение следующего плеча продолжает заказ
    /// с последнего отправленного узла. Ожидание по публикациям, а не по <c>state</c>:
    /// подтверждение плеча роботом следующему сообщению не нужно.
    /// </summary>
    private async Task WaitLegReleasedAsync(string orderId, string target, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow.AddMinutes(15);
        while (!ct.IsCancellationRequested)
        {
            var current = await orders.GetOrderAsync(orderId, ct).ConfigureAwait(false);
            if (current is null)
            {
                throw new InvalidOperationException($"Заказ {orderId} не найден при ожидании плеча к {target}.");
            }

            if (current.Status is RobotOrderStatuses.Failed or RobotOrderStatuses.Cancelled
                or RobotOrderStatuses.Rejected)
            {
                throw new InvalidOperationException(
                    $"Заказ {orderId} завершился со статусом {current.Status} до плеча к {target}.");
            }

            if (string.Equals(LastReleasedNodeId(current), target, StringComparison.Ordinal))
            {
                return;
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new InvalidOperationException($"Заказ {orderId}: последний чанк плеча к {target} не отправлен.");
            }

            await Task.Delay(PollInterval, ct).ConfigureAwait(false);
        }

        ct.ThrowIfCancellationRequested();
        throw new OperationCanceledException(ct);
    }

    /// <summary>Последний отправленный (<c>released</c>) узел плана заказа.</summary>
    private static string? LastReleasedNodeId(RobotOrder order)
    {
        for (var index = order.Nodes.Count - 1; index >= 0; index--)
        {
            if (order.Nodes[index].Released)
            {
                return order.Nodes[index].NodeId;
            }
        }

        return null;
    }

    private async Task<Guid> ResolveAgvIdAsync(CancellationToken ct)
    {
        var serial = await ResolveSerialAsync(ct).ConfigureAwait(false);
        var rows = await agvs.ListAsync(ct).ConfigureAwait(false);
        var robot = rows.FirstOrDefault(row => string.Equals(row.SerialNumber, serial, StringComparison.Ordinal));
        return robot?.Id ?? Guid.Empty;
    }

    private async Task WaitFreeAsync(CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow.AddMinutes(20);
        while (DateTimeOffset.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            if (!await orders.HasActiveOrderAsync(await ResolveSerialAsync(ct).ConfigureAwait(false), ct).ConfigureAwait(false))
            {
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
        }
    }

    private static async Task<string> ResolveSerialAsync(CancellationToken ct)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        return "98001";
    }

    private async Task RunForkAsync(string serialNumber, bool pick, CancellationToken ct)
    {
        if (!_orderOptions.Value.Order.SendForkActions)
        {
            logger.LogInformation("Действия вил выключены настройкой SendForkActions");
            return;
        }

        var actionType = pick ? "pick" : "drop";
        var actionId = await orders.TriggerForkAsync(serialNumber, actionType, ct).ConfigureAwait(false);

        var deadline = DateTimeOffset.UtcNow.AddSeconds(120);
        while (DateTimeOffset.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            var status = await orders.GetActionStatusAsync(serialNumber, actionId, ct).ConfigureAwait(false);
            if (string.Equals(status, "FINISHED", StringComparison.OrdinalIgnoreCase))
            {
                logger.LogInformation("Действие {Action} {ActionId} выполнено", actionType, actionId);
                return;
            }

            if (string.Equals(status, "FAILED", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Действие {actionType} завершилось ошибкой.");
            }

            await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
        }

        logger.LogWarning("Действие {Action} {ActionId} не завершилось за 120 секунд", actionType, actionId);
    }
    private async Task<string> WaitCompletionAsync(string orderId, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow.AddMinutes(15);
        while (DateTimeOffset.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            var order = await orders.GetOrderAsync(orderId, ct).ConfigureAwait(false);
            if (order is null)
            {
                return RobotOrderStatuses.Failed;
            }

            // Completed -> Finished, любой иной терминальный статус -> Failed (Д-6).
            if (order.Status is RobotOrderStatuses.Completed or RobotOrderStatuses.Failed
                or RobotOrderStatuses.Cancelled or RobotOrderStatuses.Rejected)
            {
                return order.Status;
            }

            await Task.Delay(PollInterval, ct).ConfigureAwait(false);
        }

        return RobotOrderStatuses.Failed;
    }

    private static Task PublishAsync(
        IProducer<string, string> producer,
        KafkaOptions settings,
        object payload,
        CancellationToken ct) =>
        producer.ProduceAsync(
            settings.EventsTopic,
            new Message<string, string> { Key = Guid.NewGuid().ToString("N"), Value = JsonSerializer.Serialize(payload) },
            ct);
}
