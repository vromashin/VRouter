using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using VR.RoutePlanner.Application.Traffic;

namespace VR.RoutePlanner.Workers;

/// <summary>
/// Периодический свип резервов трафика: снимает резервы роботов, от которых дольше
/// <c>Routing:ReservationHorizonSeconds</c> нет <c>state</c>, и проеханное сверх
/// <c>Routing:ReleaseHorizonSeconds</c> (перенос <c>TrafficCleanupService</c> из AGVControl).
/// </summary>
public sealed class TrafficReservationCleanupWorker : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    private readonly ITrafficCoordinator _traffic;
    private readonly TimeProvider _time;
    private readonly ILogger<TrafficReservationCleanupWorker> _logger;

    /// <summary>
    /// Создает свип резервов.
    /// </summary>
    /// <param name="traffic">Диспетчер трафика.</param>
    /// <param name="time">Источник времени.</param>
    /// <param name="logger">Журнал.</param>
    public TrafficReservationCleanupWorker(
        ITrafficCoordinator traffic,
        TimeProvider time,
        ILogger<TrafficReservationCleanupWorker> logger)
    {
        ArgumentNullException.ThrowIfNull(traffic);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);

        _traffic = traffic;
        _time = time;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _traffic
                    .SweepAsync(_time.GetUtcNow(), stoppingToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Свип резервов трафика завершился ошибкой");
            }

            if (!await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                return;
            }
        }
    }
}
