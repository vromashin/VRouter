using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using VR.RoutePlanner.Infrastructure.Mqtt;

namespace VR.RoutePlanner.Api.Hosted;

/// <summary>
/// Держит MQTT-подписку в процессе API: карта опрашивает
/// <c>/api/Visualization/rawVisualizationAll</c> и ждет данные из памяти процесса.
/// </summary>
public sealed class ApiMqttReaderService(
    MqttRobotStateReader reader,
    ILogger<ApiMqttReaderService> logger) : BackgroundService
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("API: чтение состояния и визуализации роботов по MQTT включено");
        await reader.RunAsync(stoppingToken).ConfigureAwait(false);
    }
}
