using Confluent.Kafka;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VR.RoutePlanner.Application.Robots;

namespace VR.RoutePlanner.Infrastructure.Kafka;

/// <summary>
/// Публикация события <c>agv-charging-completed</c> в Kafka (Д-7). Ключ сообщения — серийный
/// номер робота, <c>EventId</c> — собственный <c>Guid</c> на каждую публикацию, чтобы потребитель
/// мог дедуплицировать сообщения.
/// </summary>
public sealed class KafkaRobotEventPublisher : IRobotEventPublisher, IDisposable
{
    /// <summary>
    /// Предел ожидания доставки: тик фонового сервиса не должен стоять на недоступном брокере
    /// дольше нескольких секунд.
    /// </summary>
    private const int DeliveryTimeoutMs = 5000;

    private readonly IProducer<string, string> _producer;
    private readonly ILogger<KafkaRobotEventPublisher> _logger;

    /// <summary>
    /// Создает публикатор событий.
    /// </summary>
    /// <param name="options">Параметры <c>Routing:Kafka</c>: адрес брокера.</param>
    /// <param name="logger">Журнал.</param>
    public KafkaRobotEventPublisher(
        IOptions<KafkaOptions> options,
        ILogger<KafkaRobotEventPublisher> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _logger = logger;
        _producer = new ProducerBuilder<string, string>(new ProducerConfig
        {
            BootstrapServers = options.Value.BootstrapServers,
            MessageTimeoutMs = DeliveryTimeoutMs,
            RequestTimeoutMs = DeliveryTimeoutMs,
        }).Build();
    }

    /// <inheritdoc />
    public async Task PublishChargingCompletedAsync(
        RobotChargingCompletedEvent payload,
        string topic,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentException.ThrowIfNullOrWhiteSpace(topic);

        ct.ThrowIfCancellationRequested();

        var body = System.Text.Json.JsonSerializer.Serialize(new
        {
            payload.EventId,
            payload.AgvSerialNumber,
            payload.NewBatteryLevel,
            payload.Threshold,
            payload.Status,
            CompletedAt = payload.CompletedAt.UtcDateTime.ToString(
                "yyyy-MM-ddTHH:mm:ss.fffZ",
                System.Globalization.CultureInfo.InvariantCulture),
        });

        var result = await _producer
            .ProduceAsync(topic, new Message<string, string>
            {
                Key = payload.AgvSerialNumber,
                Value = body,
            })
            .ConfigureAwait(false);

        _logger.LogInformation(
            "Событие {EventId} робота {Serial} доставлено в {Topic}, партиция {Partition}, смещение {Offset}",
            payload.EventId,
            payload.AgvSerialNumber,
            result.Topic,
            result.Partition.Value,
            result.Offset.Value);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _producer.Flush(TimeSpan.FromSeconds(DeliveryTimeoutMs / 1000.0));
        _producer.Dispose();
    }
}
