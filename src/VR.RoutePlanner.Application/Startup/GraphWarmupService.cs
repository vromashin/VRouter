using System.Diagnostics;
using Microsoft.Extensions.Logging;
using VR.RoutePlanner.Application.Graph;
using VR.RoutePlanner.Application.Traffic;
using VR.RoutePlanner.Domain.Traffic;

namespace VR.RoutePlanner.Application.Startup;

/// <summary>
/// Оркестратор прогрева: выполняет шаги строго по порядку, ошибку шага логирует
/// с его именем и пробрасывает наверх.
/// </summary>
public sealed class GraphWarmupService : IGraphWarmupService
{
    /// <summary>Шаг загрузки графа карты.</summary>
    public const string GraphStep = "загрузка графа";

    /// <summary>Шаг получения строки <c>vrt_MapVersion</c>.</summary>
    public const string MapVersionStep = "версия карты";

    /// <summary>Шаг чтения или расчета ширин ребер.</summary>
    public const string WidthsStep = "ширины ребер";

    /// <summary>Шаг построения ресурсов профилей.</summary>
    public const string ResourcesStep = "построение ресурсов";

    /// <summary>Шаг записи ресурсов в <c>vrt_*</c>.</summary>
    public const string SaveStep = "сохранение ресурсов";

    private readonly IWarehouseGraphRepository _graphRepository;
    private readonly IMapVersionRepository _mapVersionRepository;
    private readonly IEdgeWidthRepository _widthRepository;
    private readonly ITrafficResourceBuilder _resourceBuilder;
    private readonly IResourceRepository _resourceRepository;
    private readonly IWarmupLock _warmupLock;
    private readonly IWarmupStateRepository _stateRepository;
    private readonly ILogger<GraphWarmupService> _logger;

    /// <summary>
    /// Создает оркестратор прогрева.
    /// </summary>
    /// <param name="graphRepository">Чтение графа карты.</param>
    /// <param name="mapVersionRepository">Версия карты в <c>vrt_MapVersion</c>.</param>
    /// <param name="widthRepository">Ширины ребер.</param>
    /// <param name="resourceBuilder">Построитель ресурсов.</param>
    /// <param name="resourceRepository">Запись ресурсов.</param>
    /// <param name="warmupLock">Межпроцессная блокировка прогрева.</param>
    /// <param name="stateRepository">Хранилище последнего итога прогрева.</param>
    /// <param name="logger">Логгер.</param>
    public GraphWarmupService(
        IWarehouseGraphRepository graphRepository,
        IMapVersionRepository mapVersionRepository,
        IEdgeWidthRepository widthRepository,
        ITrafficResourceBuilder resourceBuilder,
        IResourceRepository resourceRepository,
        IWarmupLock warmupLock,
        IWarmupStateRepository stateRepository,
        ILogger<GraphWarmupService> logger)
    {
        ArgumentNullException.ThrowIfNull(graphRepository);
        ArgumentNullException.ThrowIfNull(mapVersionRepository);
        ArgumentNullException.ThrowIfNull(widthRepository);
        ArgumentNullException.ThrowIfNull(resourceBuilder);
        ArgumentNullException.ThrowIfNull(resourceRepository);
        ArgumentNullException.ThrowIfNull(warmupLock);
        ArgumentNullException.ThrowIfNull(stateRepository);
        ArgumentNullException.ThrowIfNull(logger);

        _graphRepository = graphRepository;
        _mapVersionRepository = mapVersionRepository;
        _widthRepository = widthRepository;
        _resourceBuilder = resourceBuilder;
        _resourceRepository = resourceRepository;
        _warmupLock = warmupLock;
        _stateRepository = stateRepository;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<WarmupResult?> GetLastResultAsync(string mapId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mapId);

        return await _stateRepository.GetAsync(mapId, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<WarmupResult> WarmupAsync(string mapId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mapId);

        var completedBeforeWait = await _stateRepository.GetAsync(mapId, ct).ConfigureAwait(false);

        await using var lease = await _warmupLock.AcquireAsync(mapId, ct).ConfigureAwait(false);

        var completedByOtherProcess = await _stateRepository.GetAsync(mapId, ct).ConfigureAwait(false);
        if (completedByOtherProcess is not null
            && completedByOtherProcess.CompletedAt != completedBeforeWait?.CompletedAt)
        {
            _logger.LogInformation(
                "Прогрев карты {MapId}: параллельный вызов дождался уже готового результата другого процесса.",
                mapId);
            return completedByOtherProcess;
        }

        var result = await RunAsync(mapId, ct).ConfigureAwait(false);
        await _stateRepository.SaveAsync(result, ct).ConfigureAwait(false);

        return result;
    }

    private async Task<WarmupResult> RunAsync(string mapId, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();

        var graph = await StepAsync(
            GraphStep,
            () => _graphRepository.LoadAsync(mapId, ct),
            ct).ConfigureAwait(false);

        var mapVersionId = await StepAsync(
            MapVersionStep,
            () => _mapVersionRepository.GetOrCreateAsync(mapId, graph, ct),
            ct).ConfigureAwait(false);

        var widths = await StepAsync(
            WidthsStep,
            () => _widthRepository.GetOrCreateAsync(mapVersionId, graph, mapId, ct),
            ct).ConfigureAwait(false);

        var sets = await StepAsync(
            ResourcesStep,
            () => _resourceBuilder.BuildAsync(graph, widths.Widths, mapVersionId, ct),
            ct).ConfigureAwait(false);

        await StepAsync(
            SaveStep,
            async () =>
            {
                await _resourceRepository.SaveAsync(mapVersionId, sets, ct).ConfigureAwait(false);
                return true;
            },
            ct).ConfigureAwait(false);

        var resourceCount = new Dictionary<string, int>(sets.Count, StringComparer.Ordinal);
        var impassableCount = new Dictionary<string, int>(sets.Count, StringComparer.Ordinal);
        foreach (var set in sets)
        {
            resourceCount[set.AgvProfile] = set.Resources.Count;
            impassableCount[set.AgvProfile] = set.Passability.Count(item => !item.IsPassable);
        }

        var duration = Stopwatch.GetElapsedTime(started);
        var result = new WarmupResult(
            mapId,
            mapVersionId,
            graph.GraphVersion,
            widths.Widths.Count,
            _resourceBuilder.WidthSource,
            resourceCount,
            impassableCount,
            (long)Math.Round(duration.TotalMilliseconds, MidpointRounding.AwayFromZero),
            widths.Reused,
            DateTimeOffset.UtcNow);

        _logger.LogInformation(
            "Прогрев карты {MapId} завершен за {DurationMs} мс: ребер {EdgeCount}, ресурсов {ResourceCount}, "
            + "ширины из кэша: {ReusedWidths}.",
            result.MapId,
            result.DurationMs,
            result.EdgeCount,
            string.Join(", ", result.ResourceCount.Select(pair => $"{pair.Key}={pair.Value}")),
            result.ReusedWidths);

        return result;
    }

    private async Task<T> StepAsync<T>(string step, Func<Task<T>> action, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            return await action().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Прогрев: шаг «{Step}» завершился ошибкой: {Message}.", step, exception.Message);
            throw new WarmupStepException(step, exception);
        }
    }
}
