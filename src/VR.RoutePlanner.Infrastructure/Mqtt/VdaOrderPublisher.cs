using System.Globalization;
using Microsoft.Extensions.Logging;
using VR.RoutePlanner.Application.Robots;

namespace VR.RoutePlanner.Infrastructure.Mqtt;

/// <summary>
/// Публикация заказа: собирает тело, адресует топик <c>APR/v2/{manufacturer}/{serial}/order</c>
/// и передает его транспорту. <c>headerId</c> берется из таймера и строго возрастает.
/// </summary>
public sealed class VdaOrderPublisher : IRobotOrderPublisher
{
    /// <summary>Шаблон топика заказа.</summary>
    public const string TopicTemplate = "APR/v2/{0}/{1}/order";

    /// <summary>Шаблон топика мгновенных действий.</summary>
    public const string InstantActionTopicTemplate = "APR/v2/{0}/{1}/instantActions";

    private static long _lastHeaderId;

    private readonly IRobotOrderTransport _transport;
    private readonly ILogger<VdaOrderPublisher> _logger;

    /// <summary>
    /// Создает публикатор заказов.
    /// </summary>
    /// <param name="transport">Транспорт публикации.</param>
    /// <param name="logger">Журнал.</param>
    public VdaOrderPublisher(IRobotOrderTransport transport, ILogger<VdaOrderPublisher> logger)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(logger);

        _transport = transport;
        _logger = logger;
    }

    /// <summary>
    /// Следующий <c>headerId</c> по таймеру: тики текущего момента, но не меньше предыдущего.
    /// </summary>
    /// <returns>Идентификатор сообщения.</returns>
    public static long NextHeaderId()
    {
        var candidate = DateTime.UtcNow.Ticks;
        while (true)
        {
            var previous = Interlocked.Read(ref _lastHeaderId);
            var next = candidate > previous ? candidate : previous + 1;
            if (Interlocked.CompareExchange(ref _lastHeaderId, next, previous) == previous)
            {
                return next;
            }
        }
    }

    /// <summary>
    /// Топик заказа для робота.
    /// </summary>
    /// <param name="manufacturer">Производитель.</param>
    /// <param name="serialNumber">Серийный номер.</param>
    /// <returns>Топик.</returns>
    public static string TopicFor(string manufacturer, string serialNumber) =>
        string.Format(CultureInfo.InvariantCulture, TopicTemplate, manufacturer, serialNumber);

    /// <inheritdoc />
    public async Task PublishAsync(
        string manufacturer,
        string serialNumber,
        string orderJson,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manufacturer);
        ArgumentException.ThrowIfNullOrWhiteSpace(serialNumber);
        ArgumentException.ThrowIfNullOrWhiteSpace(orderJson);

        var topic = TopicFor(manufacturer, serialNumber);
        await _transport.PublishAsync(topic, orderJson, ct).ConfigureAwait(false);
        _logger.LogInformation("Заказ отправлен в {Topic}, байт: {Length}", topic, orderJson.Length);
    }

    /// <inheritdoc />
    public async Task PublishInstantActionAsync(
        string manufacturer,
        string serialNumber,
        string payload,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manufacturer);
        ArgumentException.ThrowIfNullOrWhiteSpace(serialNumber);
        ArgumentException.ThrowIfNullOrWhiteSpace(payload);

        var topic = string.Format(CultureInfo.InvariantCulture, InstantActionTopicTemplate, manufacturer, serialNumber);
        await _transport.PublishAsync(topic, payload, ct).ConfigureAwait(false);
        _logger.LogInformation("Мгновенное действие отправлено в {Topic}", topic);
    }
}
