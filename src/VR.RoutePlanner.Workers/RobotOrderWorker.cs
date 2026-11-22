using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VR.RoutePlanner.Application.Legacy;
using VR.RoutePlanner.Application.Robots;
using VR.RoutePlanner.Infrastructure.Legacy;
using VR.RoutePlanner.Infrastructure.Mqtt;

namespace VR.RoutePlanner.Workers;

/// <summary>
/// Ведение заказов: восстановление после рестарта, обновление статуса по <c>state</c>
/// и проверка таймаутов. Включается вместе с чтением MQTT.
/// </summary>
public sealed class RobotOrderWorker : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(2);

    private readonly RobotOrderService _orders;
    private readonly RobotSessionService _sessions;
    private readonly MqttRobotStateReader _reader;
    private readonly ILegacyAgvStateWriter _legacy;
    private readonly RobotVisualizationWriter _visualizations;
    private readonly IOptions<LegacyOptions> _legacyOptions;
    private readonly ILogger<RobotOrderWorker> _logger;

    /// <summary>
    /// Создает сервис ведения заказов.
    /// </summary>
    /// <param name="orders">Сервис заказов.</param>
    /// <param name="sessions">Запись состояния роботов в базу.</param>
    /// <param name="reader">Чтение состояния роботов.</param>
    /// <param name="logger">Журнал.</param>
    public RobotOrderWorker(
        RobotOrderService orders,
        RobotSessionService sessions,
        MqttRobotStateReader reader,
        ILegacyAgvStateWriter legacy,
        RobotVisualizationWriter visualizations,
        IOptions<LegacyOptions> legacyOptions,
        ILogger<RobotOrderWorker> logger)
    {
        ArgumentNullException.ThrowIfNull(orders);
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(visualizations);
        ArgumentNullException.ThrowIfNull(logger);

        _orders = orders;
        _sessions = sessions;
        _reader = reader;
        _legacy = legacy;
        _visualizations = visualizations;
        _legacyOptions = legacyOptions;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _reader.SnapshotReceived += OnSnapshotReceived;
        _reader.VisualizationReceived += OnVisualizationReceived;

        try
        {
            await RecoverAsync(stoppingToken).ConfigureAwait(false);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await _orders.CheckTimeoutsAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception)
                {
                    _logger.LogError(exception, "Ошибка проверки таймаутов заказов");
                }

                await Task.Delay(Tick, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // штатная остановка
        }
        finally
        {
            _reader.SnapshotReceived -= OnSnapshotReceived;
            _reader.VisualizationReceived -= OnVisualizationReceived;
        }
    }

    private async Task RecoverAsync(CancellationToken ct)
    {
        try
        {
            var failed = await _orders.RecoverAsync(ct).ConfigureAwait(false);
            _logger.LogInformation("Заказы после рестарта: провалено {Failed}", failed);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Ошибка восстановления заказов");
        }
    }

    private void OnVisualizationReceived(object? sender, RobotVisualization visualization)
    {
        _ = Task.Run(
            async () =>
            {
                try
                {
                    await _visualizations.WriteAsync(visualization, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    _logger.LogError(exception, "Ошибка записи позиции робота {Serial}", visualization.SerialNumber);
                }
            });
    }

    private void OnSnapshotReceived(object? sender, Application.Robots.RobotSnapshot snapshot)
    {
        _ = Task.Run(
            async () =>
            {
                try
                {
                    var payload = _reader.GetLatestPayload(snapshot.SerialNumber);
                    await _sessions.SaveAsync(snapshot, payload, CancellationToken.None).ConfigureAwait(false);
                    var status = await _orders.ApplyStateAsync(snapshot, CancellationToken.None).ConfigureAwait(false);
                    if (_legacyOptions.Value.WriteAgvsEnabled)
                    {
                        await _legacy
                            .ApplyAsync(snapshot, status?.OrderId, CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                }
                catch (Exception exception)
                {
                    _logger.LogError(exception, "Ошибка обновления заказа по state робота {Serial}", snapshot.SerialNumber);
                }
            });
    }
}
