using System.Text.Json;
using Confluent.Kafka;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VR.RoutePlanner.Application.Robots;
using VR.RoutePlanner.Domain.Robots;

namespace VR.RoutePlanner.Infrastructure.Kafka;

/// <summary>
/// Читает задачи WMS и исполняет их роботом: подбор в первом узле, выгрузка во втором.
/// События выполнения публикуются в топик <c>tasks-events</c>.
/// </summary>
public sealed class SimpleTaskWorker(
    IOptions<KafkaOptions> options,
    RobotOrderService orders,
    ILogger<SimpleTaskWorker> logger) : BackgroundService
{
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
            AutoOffsetReset = AutoOffsetReset.Latest,
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

        await PublishAsync(producer, settings, new { taskId = task.TaskId, status = "Started", agvId = Guid.Empty }, ct)
            .ConfigureAwait(false);

        try
        {
            foreach (var action in task.Actions)
            {
                var hasLoad = action.ActionType != WmsTaskParser.PickUp;
                var order = await orders
                    .CreateOrderAsync(task.AreaWmsId, await ResolveSerialAsync(ct).ConfigureAwait(false), action.Location, hasLoad, ct)
                    .ConfigureAwait(false);

                logger.LogInformation(
                    "Задача {TaskId}: шаг {Location} под заказом {OrderId}",
                    task.TaskId,
                    action.Location,
                    order.OrderId);

                await WaitCompletionAsync(order.OrderId, ct).ConfigureAwait(false);
            }

            await PublishAsync(producer, settings, new { taskId = task.TaskId, status = "Finished", agvId = Guid.Empty }, ct)
                .ConfigureAwait(false);
            logger.LogInformation("Задача {TaskId}: выполнена", task.TaskId);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Задача {TaskId}: ошибка исполнения", task.TaskId);
            await PublishAsync(producer, settings, new { taskId = task.TaskId, status = "Failed", agvId = Guid.Empty }, ct)
                .ConfigureAwait(false);
        }
    }

    private static async Task<string> ResolveSerialAsync(CancellationToken ct)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        return "98001";
    }

    private async Task WaitCompletionAsync(string orderId, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow.AddMinutes(15);
        while (DateTimeOffset.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            var order = await orders.GetOrderAsync(orderId, ct).ConfigureAwait(false);
            if (order is null)
            {
                return;
            }

            if (order.Status is RobotOrderStatuses.Completed or RobotOrderStatuses.Failed or RobotOrderStatuses.Cancelled)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
        }
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
