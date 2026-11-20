using Microsoft.Extensions.Options;
using VR.RoutePlanner.Application.Configuration;
using VR.RoutePlanner.Application.Startup;

namespace VR.RoutePlanner.Workers;

/// <summary>
/// Хостед-сервис прогрева ресурсов: прогон при старте и далее по интервалу
/// из секции <c>Routing:Warmup</c>.
/// </summary>
public sealed class GraphWarmupWorker : BackgroundService
{
    private readonly IGraphWarmupService _warmup;
    private readonly IOptions<WarmupOptions> _options;
    private readonly ILogger<GraphWarmupWorker> _logger;

    /// <summary>
    /// Создает сервис прогрева.
    /// </summary>
    /// <param name="warmup">Оркестратор прогрева.</param>
    /// <param name="options">Параметры прогрева.</param>
    /// <param name="logger">Логгер.</param>
    public GraphWarmupWorker(
        IGraphWarmupService warmup,
        IOptions<WarmupOptions> options,
        ILogger<GraphWarmupWorker> logger)
    {
        ArgumentNullException.ThrowIfNull(warmup);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _warmup = warmup;
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// Выполняет один прогрев с учетом политики <c>FailFast</c>.
    /// </summary>
    /// <param name="ct">Токен остановки сервиса.</param>
    /// <returns><c>true</c>, если прогрев прошел без ошибок.</returns>
    /// <exception cref="OperationCanceledException">Сервис останавливается.</exception>
    public async Task<bool> RunOnceAsync(CancellationToken ct)
    {
        var options = _options.Value;
        try
        {
            var result = await _warmup.WarmupAsync(options.MapId, ct).ConfigureAwait(false);
            _logger.LogInformation(
                "Прогрев карты {MapId} завершен: ребер {EdgeCount}, ресурсов {ResourceCount}, "
                + "непроходимых ребер {ImpassableCount}, ширина {WidthSource}, из кэша {ReusedWidths}, {DurationMs} мс.",
                result.MapId,
                result.EdgeCount,
                string.Join(", ", result.ResourceCount.Select(pair => $"{pair.Key}={pair.Value}")),
                string.Join(", ", result.ImpassableCount.Select(pair => $"{pair.Key}={pair.Value}")),
                result.WidthSource,
                result.ReusedWidths,
                result.DurationMs);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            var step = exception is WarmupStepException stepException ? stepException.Step : "неизвестен";
            _logger.LogError(
                exception,
                "Прогрев карты {MapId} завершился ошибкой на шаге «{Step}».",
                options.MapId,
                step);

            if (options.FailFast)
            {
                throw;
            }

            return false;
        }
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = _options.Value;
        if (!options.Enabled)
        {
            _logger.LogInformation("Прогрев ресурсов отключен (Routing:Warmup:Enabled = false).");
            return;
        }

        if (options.RunOnStartup)
        {
            await RunOnceAsync(stoppingToken).ConfigureAwait(false);
        }

        if (options.RefreshIntervalMinutes <= 0)
        {
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(options.RefreshIntervalMinutes));
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            await RunOnceAsync(stoppingToken).ConfigureAwait(false);
        }
    }
}
