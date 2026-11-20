using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VR.RoutePlanner.Application.Configuration;
using VR.RoutePlanner.Application.Graph;
using VR.RoutePlanner.Infrastructure.Persistence;

namespace VR.RoutePlanner.Infrastructure.Startup;

/// <summary>
/// Параметры работы с базой из секции конфигурации <c>Database</c>.
/// </summary>
public sealed class DatabaseOptions
{
    /// <summary>Имя секции конфигурации.</summary>
    public const string SectionName = "Database";

    /// <summary>
    /// Применять миграции при старте. В Development включено, в Production выключено:
    /// там миграции применяются вручную, а сервис только проверяет отсутствие ожидающих.
    /// </summary>
    public bool AutoMigrate { get; set; }
}

/// <summary>
/// Применяет миграции своей истории (или проверяет их отсутствие) и прогревает кэш графа
/// и растра для активных карт. Любая ошибка прерывает старт сервиса.
/// </summary>
public sealed class DatabaseStartupService : IHostedService
{
    private readonly IDbContextFactory<RoutePlannerDbContext> _contextFactory;
    private readonly IWarehouseGraphRepository _graphRepository;
    private readonly IMapRasterProvider _rasterProvider;
    private readonly IOptions<DatabaseOptions> _databaseOptions;
    private readonly IOptions<RoutePlannerOptions> _routingOptions;
    private readonly ILogger<DatabaseStartupService> _logger;

    /// <summary>Создает стартовый сервис.</summary>
    public DatabaseStartupService(
        IDbContextFactory<RoutePlannerDbContext> contextFactory,
        IWarehouseGraphRepository graphRepository,
        IMapRasterProvider rasterProvider,
        IOptions<DatabaseOptions> databaseOptions,
        IOptions<RoutePlannerOptions> routingOptions,
        ILogger<DatabaseStartupService> logger)
    {
        _contextFactory = contextFactory;
        _graphRepository = graphRepository;
        _rasterProvider = rasterProvider;
        _databaseOptions = databaseOptions;
        _routingOptions = routingOptions;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);

        if (_databaseOptions.Value.AutoMigrate)
        {
            await MigrationRunner
                .MigrateAsync(context, MigrationRunner.HistoryTable, MigrationRunner.LockKey, _logger, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            await MigrationRunner
                .EnsureNoPendingMigrationsAsync(context, MigrationRunner.HistoryTable, MigrationRunner.LockKey, _logger, cancellationToken)
                .ConfigureAwait(false);
        }

        await WarmUpAsync(context, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task WarmUpAsync(RoutePlannerDbContext context, CancellationToken cancellationToken)
    {
        var mapIds = await context.Maps
            .AsNoTracking()
            .Where(map => map.IsActive)
            .Select(map => map.MapId)
            .Distinct()
            .OrderBy(mapId => mapId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (mapIds.Count == 0)
        {
            _logger.LogWarning("В public.\"Maps\" нет активных карт, прогрев графа пропущен.");
            return;
        }

        var warmRaster = _routingOptions.Value.WidthSource == Domain.Traffic.WidthSource.Raster;

        foreach (var mapId in mapIds)
        {
            try
            {
                var graph = await _graphRepository.LoadAsync(mapId, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation(
                    "Граф карты {MapId} загружен: версия {GraphVersion}, узлов {NodeCount}, ребер {EdgeCount}.",
                    mapId,
                    graph.GraphVersion,
                    graph.Nodes.Count,
                    graph.Edges.Count);

                if (warmRaster)
                {
                    var raster = await _rasterProvider.GetAsync(mapId, cancellationToken).ConfigureAwait(false);
                    _logger.LogInformation(
                        "Растр карты {MapId} загружен: {Width}x{Height}, разрешение {Resolution} м/пиксель.",
                        mapId,
                        raster.Width,
                        raster.Height,
                        raster.Resolution);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogError(
                    exception,
                    "Карта {MapId} не подготовлена к работе: {Message}. Старт сервиса прерван.",
                    mapId,
                    exception.Message);
                throw;
            }
        }
    }
}
