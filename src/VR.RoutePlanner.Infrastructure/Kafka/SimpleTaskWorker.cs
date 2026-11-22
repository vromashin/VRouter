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
            foreach (var action in task.Actions)
            {
                var hasLoad = action.ActionType != WmsTaskParser.PickUp;
                await WaitFreeAsync(ct).ConfigureAwait(false);
                var order = await orders
                    .CreateOrderAsync(task.AreaWmsId, await ResolveSerialAsync(ct).ConfigureAwait(false), action.Location, hasLoad, ct)
                    .ConfigureAwait(false);

                logger.LogInformation(
                    "Задача {TaskId}: шаг {Location} под заказом {OrderId}",
                    task.TaskId,
                    action.Location,
                    order.OrderId);

                var outcome = await WaitCompletionAsync(order.OrderId, ct).ConfigureAwait(false);
                if (!string.Equals(outcome, RobotOrderStatuses.Completed, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"Заказ {order.OrderId} завершился со статусом {outcome}.");
                }

                // Вилы: pick/drop отдельным instantAction, как в AGVControl.
                await RunForkAsync(order.AgvSerialNumber, action.ActionType == WmsTaskParser.PickUp, ct).ConfigureAwait(false);
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

            if (order.Status is RobotOrderStatuses.Completed or RobotOrderStatuses.Failed or RobotOrderStatuses.Cancelled)
            {
                return order.Status;
            }

            await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
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
