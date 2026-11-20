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
/// Читает растр карты с локального диска, при его отсутствии пробует HTTP.
/// Метаданные (размер, разрешение, смещение) берутся из <c>public."Maps"</c>.
/// </summary>
public sealed class MapRasterProvider : IMapRasterProvider
{
    private static readonly Lazy<HttpClient> SharedHttpClient = new(
        () => new HttpClient { Timeout = TimeSpan.FromSeconds(30) },
        isThreadSafe: true);

    private readonly IDbContextFactory<RoutePlannerDbContext> _contextFactory;
    private readonly IMemoryCache _cache;
    private readonly IOptions<RoutePlannerOptions> _options;
    private readonly ILogger<MapRasterProvider> _logger;

    /// <summary>
    /// Создает провайдер растра.
    /// </summary>
    /// <param name="contextFactory">Фабрика контекста базы <c>rms</c>.</param>
    /// <param name="cache">Кэш в памяти процесса.</param>
    /// <param name="options">Параметры маршрутизации.</param>
    /// <param name="logger">Логгер.</param>
    public MapRasterProvider(
        IDbContextFactory<RoutePlannerDbContext> contextFactory,
        IMemoryCache cache,
        IOptions<RoutePlannerOptions> options,
        ILogger<MapRasterProvider> logger)
    {
        _contextFactory = contextFactory;
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

        var (fileBytes, stamp) = await ReadFileBytesAsync(fileName, cancellationToken).ConfigureAwait(false);
        var cacheKey = string.Create(
            CultureInfo.InvariantCulture,
            $"routeplanner:raster:{mapId}:{map.Id}:{fileName}:{stamp}");

        if (_cache.TryGetValue(cacheKey, out MapRaster? cached) && cached is not null)
        {
            return cached;
        }

        var raster = MapRasterLoader.Load(
            fileBytes,
            fileName,
            map.Resolution,
            map.OriginX,
            map.OriginY,
            map.Width,
            map.Height,
            options.ObstacleThreshold);

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

    private async Task<(byte[] Bytes, string Stamp)> ReadFileBytesAsync(
        string fileName,
        CancellationToken cancellationToken)
    {
        var options = _options.Value;
        var directory = options.MapRasterPath;

        if (!string.IsNullOrWhiteSpace(directory))
        {
            var fullPath = Path.Combine(directory, fileName);
            var info = new FileInfo(fullPath);
            if (info.Exists)
            {
                var bytes = await File.ReadAllBytesAsync(fullPath, cancellationToken).ConfigureAwait(false);
                var stamp = string.Create(
                    CultureInfo.InvariantCulture,
                    $"file:{info.LastWriteTimeUtc.Ticks}:{bytes.Length}");
                return (bytes, stamp);
            }

            _logger.LogWarning(
                "Файл растра {Path} не найден, пробуем скачать по адресу {Url}.",
                fullPath,
                options.MapRasterUrl);
        }

        if (string.IsNullOrWhiteSpace(options.MapRasterUrl))
        {
            throw new FileNotFoundException(
                $"Файл растра '{fileName}' не найден в '{directory}', а Routing:MapRasterUrl не задан.");
        }

        var url = options.MapRasterUrl.TrimEnd('/') + "/" + Uri.EscapeDataString(fileName);
        var downloaded = await SharedHttpClient.Value
            .GetByteArrayAsync(url, cancellationToken)
            .ConfigureAwait(false);

        _logger.LogInformation(
            "Растр {FileName} скачан по адресу {Url}, {Size} байт.",
            fileName,
            url,
            downloaded.Length);

        return (downloaded, string.Create(CultureInfo.InvariantCulture, $"http:{downloaded.Length}"));
    }
}
