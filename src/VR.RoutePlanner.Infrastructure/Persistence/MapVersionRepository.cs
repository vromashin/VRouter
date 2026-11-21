using Microsoft.EntityFrameworkCore;
using Npgsql;
using VR.RoutePlanner.Application.Startup;
using VR.RoutePlanner.Domain.Graph;

namespace VR.RoutePlanner.Infrastructure.Persistence;

/// <summary>
/// Строки <c>vrt_MapVersion</c>: одна запись на пару карта + версия графа.
/// </summary>
public sealed class MapVersionRepository : IMapVersionRepository
{
    private const string UniqueViolation = "23505";

    private readonly IDbContextFactory<RoutePlannerDbContext> _contextFactory;

    /// <summary>
    /// Создает репозиторий версий карт.
    /// </summary>
    /// <param name="contextFactory">Фабрика контекста базы <c>rms</c>.</param>
    public MapVersionRepository(IDbContextFactory<RoutePlannerDbContext> contextFactory)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        _contextFactory = contextFactory;
    }

    /// <inheritdoc />
    public async Task<Guid> GetOrCreateAsync(string mapId, WarehouseGraph graph, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mapId);
        ArgumentNullException.ThrowIfNull(graph);

        await using var context = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var existing = await FindAsync(context, mapId, graph.GraphVersion, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            return existing.Value;
        }

        var record = new MapVersionRecord
        {
            Id = Guid.NewGuid(),
            MapId = mapId,
            MapVersion = graph.MapVersion,
            GraphVersion = graph.GraphVersion,
            CreatedAt = DateTime.UtcNow,
        };

        context.MapVersions.Add(record);
        try
        {
            await context.SaveChangesAsync(ct).ConfigureAwait(false);
            return record.Id;
        }
        catch (DbUpdateException exception)
            when (exception.InnerException is PostgresException { SqlState: UniqueViolation })
        {
            // Параллельный прогрев успел вставить ту же версию: берем готовую строку.
            context.Entry(record).State = EntityState.Detached;
            var inserted = await FindAsync(context, mapId, graph.GraphVersion, ct).ConfigureAwait(false);
            if (inserted is null)
            {
                throw;
            }

            return inserted.Value;
        }
    }

    /// <inheritdoc />
    public async Task<Guid?> TryGetAsync(string mapId, string graphVersion, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mapId);
        ArgumentException.ThrowIfNullOrWhiteSpace(graphVersion);

        await using var context = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await FindAsync(context, mapId, graphVersion, ct).ConfigureAwait(false);
    }

    private static Task<Guid?> FindAsync(
        RoutePlannerDbContext context,
        string mapId,
        string graphVersion,
        CancellationToken ct) => context.MapVersions
        .AsNoTracking()
        .Where(record => record.MapId == mapId && record.GraphVersion == graphVersion)
        .Select(record => (Guid?)record.Id)
        .FirstOrDefaultAsync(ct);
}
