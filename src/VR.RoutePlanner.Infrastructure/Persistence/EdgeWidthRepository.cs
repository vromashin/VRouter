using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using VR.RoutePlanner.Application.Graph;
using VR.RoutePlanner.Application.Traffic;
using VR.RoutePlanner.Domain.Graph;
using VR.RoutePlanner.Domain.Traffic;

namespace VR.RoutePlanner.Infrastructure.Persistence;

/// <summary>
/// Ширины ребер: читает <c>vrt_EdgeWidth</c>, при отсутствии строк считает их построителем ресурсов
/// и вставляет одной транзакцией. Повторная вставка тех же (<c>MapVersionId</c>,<c>EdgeId</c>)
/// не падает: используется <c>ON CONFLICT DO NOTHING</c>.
/// </summary>
public sealed class EdgeWidthRepository : IEdgeWidthRepository
{
    private readonly IDbContextFactory<RoutePlannerDbContext> _contextFactory;
    private readonly TrafficResourceBuilder _builder;
    private readonly ILogger<EdgeWidthRepository> _logger;

    /// <summary>Создает репозиторий ширин.</summary>
    /// <param name="contextFactory">Фабрика контекста <c>rms</c>.</param>
    /// <param name="builder">Построитель, который считает ширины по растру.</param>
    /// <param name="logger">Логгер.</param>
    public EdgeWidthRepository(
        IDbContextFactory<RoutePlannerDbContext> contextFactory,
        TrafficResourceBuilder builder,
        ILogger<EdgeWidthRepository> logger)
    {
        _contextFactory = contextFactory;
        _builder = builder;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<EdgeWidth>> GetOrCreateAsync(
        Guid mapVersionId,
        WarehouseGraph graph,
        string mapId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentException.ThrowIfNullOrWhiteSpace(mapId);

        if (mapVersionId == Guid.Empty)
        {
            throw new InvalidOperationException(
                "Для чтения ширин нужен идентификатор версии карты vrt_MapVersion.Id.");
        }

        var stored = await ReadAsync(mapVersionId, ct).ConfigureAwait(false);
        if (stored.Count > 0)
        {
            return stored;
        }

        var computed = await _builder.ComputeWidthsAsync(graph, mapVersionId, ct).ConfigureAwait(false);
        if (computed.Count == 0)
        {
            return computed;
        }

        await InsertAsync(computed, ct).ConfigureAwait(false);

        var saved = await ReadAsync(mapVersionId, ct).ConfigureAwait(false);
        if (saved.Count == 0)
        {
            _logger.LogWarning(
                "Ширины карты {MapId} не сохранились: строки vrt_EdgeWidth не найдены после вставки.",
                mapId);
            return computed;
        }

        return saved;
    }

    private async Task<IReadOnlyList<EdgeWidth>> ReadAsync(Guid mapVersionId, CancellationToken ct)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var rows = await context.EdgeWidths
            .AsNoTracking()
            .Where(record => record.MapVersionId == mapVersionId)
            .OrderBy(record => record.EdgeId)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return rows
            .Select(row => new EdgeWidth(
                row.MapVersionId,
                row.EdgeId,
                row.Width,
                // В vrt_EdgeWidth по спеке хранится только ширина: длина и число точек
                // не персистятся и в прочитанных строках остаются нулевыми.
                LengthMeters: 0.0,
                SampledPoints: 0,
                ParseSource(row.WidthSource),
                string.IsNullOrEmpty(row.WidthSourceDetail) ? null : row.WidthSourceDetail))
            .ToList();
    }

    private async Task InsertAsync(IReadOnlyList<EdgeWidth> widths, CancellationToken ct)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        foreach (var width in widths)
        {
            context.EdgeWidths.Add(new EdgeWidthRecord
            {
                MapVersionId = width.MapVersionId,
                EdgeId = width.EdgeId,
                Width = width.WidthMeters,
                WidthSource = width.WidthSource.ToString(),
                WidthSourceDetail = width.WidthSourceDetail ?? string.Empty,
            });
        }

        try
        {
            await context.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException exception)
        {
            // Гонка двух экземпляров сервиса: строки успел вставить другой процесс.
            // Первичный ключ (MapVersionId, EdgeId) не дал дубля, повторное чтение вернет данные из базы.
            _logger.LogDebug(
                exception,
                "Вставка ширин упала на конфликте ключа: строки уже есть, читаю из базы.");
            context.ChangeTracker.Clear();
        }
    }

    private static WidthSource ParseSource(string value) => value switch
    {
        nameof(WidthSource.Raster) => WidthSource.Raster,
        nameof(WidthSource.Manual) => WidthSource.Manual,
        _ => WidthSource.Default,
    };
}
