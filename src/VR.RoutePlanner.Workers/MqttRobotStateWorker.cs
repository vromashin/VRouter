using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace VR.RoutePlanner.Workers;

/// <summary>
/// Хостед-сервис чтения состояния роботов по MQTT. Включается флагом <c>Routing:Mqtt:Enabled</c>.
/// </summary>
public sealed class MqttRobotStateWorker : BackgroundService
{
    private readonly Infrastructure.Mqtt.MqttRobotStateReader _reader;
    private readonly ILogger<MqttRobotStateWorker> _logger;

    /// <summary>
    /// Создает хостед-сервис MQTT.
    /// </summary>
    /// <param name="reader">Клиент чтения состояний.</param>
    /// <param name="logger">Журнал.</param>
    public MqttRobotStateWorker(
        Infrastructure.Mqtt.MqttRobotStateReader reader,
        ILogger<MqttRobotStateWorker> logger)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(logger);

        _reader = reader;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Чтение состояния роботов по MQTT включено, подписка {Topic}",
            Infrastructure.Mqtt.MqttRobotStateReader.StateTopicFilter);

        await _reader.RunAsync(stoppingToken).ConfigureAwait(false);
    }
}
