using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VR.RoutePlanner.Application.Configuration;
using VR.RoutePlanner.Application.Graph;
using VR.RoutePlanner.Infrastructure.Persistence;

namespace VR.RoutePlanner.Infrastructure.Graph;

/// <summary>
/// Читает растр карты из каталога <c>Routing:MapRasterPath</c>; HTTP-загрузка допускается только при пустом каталоге.
/// Метаданные (размер, разрешение, смещение) берутся из <c>public."Maps"</c>, кэш ключуется по версии графа.
/// </summary>
public sealed class MapRasterProvider : IMapRasterProvider
{
    private readonly IDbContextFactory<RoutePlannerDbContext> _contextFactory;
    private readonly IWarehouseGraphRepository _graphRepository;
    private readonly IMemoryCache _cache;
    private readonly IOptions<RoutePlannerOptions> _options;
    private readonly ILogger<MapRasterProvider> _logger;

    /// <summary>
    /// Создает провайдер растра.
    /// </summary>
    /// <param name="contextFactory">Фабрика контекста базы <c>rms</c>.</param>
    /// <param name="graphRepository">Репозиторий графа: дает версию графа для ключа кэша.</param>
    /// <param name="cache">Кэш в памяти процесса.</param>
    /// <param name="options">Параметры маршрутизации.</param>
    /// <param name="logger">Логгер.</param>
    public MapRasterProvider(
        IDbContextFactory<RoutePlannerDbContext> contextFactory,
        IWarehouseGraphRepository graphRepository,
        IMemoryCache cache,
        IOptions<RoutePlannerOptions> options,
        ILogger<MapRasterProvider> logger)
    {
        _contextFactory = contextFactory;
        _graphRepository = graphRepository;
        _cache = cache;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<MapRaster> GetAsync(string mapId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mapId);

        var options = _options.Value;
        var map = await LoadMapRecordAsync(mapId, cancellationToken).ConfigureAwait(false);
        var fileName = Path.GetFileName(map.ImageUrl ?? string.Empty);

        if (string.IsNullOrWhiteSpace(fileName))
        {
            throw new InvalidOperationException(
                $"У активной карты '{mapId}' не заполнено Maps.ImageUrl, имя файла растра неизвестно.");
        }

        var (fileBytes, _) = await MapRasterFileReader
            .ReadAsync(options.MapRasterPath, options.MapRasterUrl, fileName, _logger, cancellationToken)
            .ConfigureAwait(false);

        // Ключ кэша включает GraphVersion: смена версии графа инвалидирует растр и поле расстояний.
        var graphVersion = (await _graphRepository.LoadAsync(mapId, cancellationToken).ConfigureAwait(false)).GraphVersion;
        var cacheKey = string.Create(
            CultureInfo.InvariantCulture,
            $"routeplanner:raster:{mapId}:{graphVersion}");

        if (_cache.TryGetValue(cacheKey, out MapRaster? cached) && cached is not null)
        {
            return cached;
        }

        var raster = LoadRaster(fileBytes, fileName, map, options.ObstacleThreshold);

        _cache.Set(
            cacheKey,
            raster,
            new MemoryCacheEntryOptions
            {
                SlidingExpiration = TimeSpan.FromHours(1),
                Priority = CacheItemPriority.Normal,
            });

        _logger.LogInformation(
            "Растр карты {MapId} загружен из {Source}: {Width} x {Height} пикселей, разрешение {Resolution} м/пиксель.",
            mapId,
            fileName,
            raster.Width,
            raster.Height,
            raster.Resolution);

        return raster;
    }

    private MapRaster LoadRaster(byte[] fileBytes, string fileName, RmsMapRecord map, int obstacleThreshold)
    {
        try
        {
            return MapRasterLoader.Load(
                fileBytes,
                fileName,
                map.Resolution,
                map.OriginX,
                map.OriginY,
                map.Width,
                map.Height,
                obstacleThreshold);
        }
        catch (InvalidDataException exception)
        {
            throw new InvalidOperationException(
                $"Растр '{fileName}' не является 8-битным grayscale PNG: {exception.Message}",
                exception);
        }
    }

    private async Task<RmsMapRecord> LoadMapRecordAsync(string mapId, CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);

        var maps = await context.Maps
            .AsNoTracking()
            .Where(record => record.MapId == mapId && record.IsActive)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (maps.Count > 1)
        {
            _logger.LogWarning(
                "Для карты {MapId} активно несколько строк Maps: {Versions}. Взят растр самой свежей версии.",
                mapId,
                ActiveMapSelector.DescribeVersions(maps));
        }

        return ActiveMapSelector.Select(maps, mapId);
    }
}
