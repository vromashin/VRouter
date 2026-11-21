using Microsoft.EntityFrameworkCore;
using VR.RoutePlanner.Application.Graph;
using VR.RoutePlanner.Domain.Graph;
using VR.RoutePlanner.Domain.Traffic;

namespace VR.RoutePlanner.Infrastructure.Persistence;

/// <summary>
/// Чтение <c>vrt_EdgePassability</c> без записи: таблицу наполняет прогрев.
/// </summary>
public sealed class EdgePassabilityRepository : IEdgePassabilityRepository
{
    private readonly IDbContextFactory<RoutePlannerDbContext> _contextFactory;

    /// <summary>
    /// Создает репозиторий проходимости.
    /// </summary>
    /// <param name="contextFactory">Фабрика контекста базы <c>rms</c>.</param>
    public EdgePassabilityRepository(IDbContextFactory<RoutePlannerDbContext> contextFactory)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        _contextFactory = contextFactory;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<EdgePassability>> GetAsync(
        Guid mapVersionId,
        string agvProfile,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agvProfile);

        await using var context = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var rows = await context.EdgePassabilities
            .AsNoTracking()
            .Where(row => row.MapVersionId == mapVersionId && row.AgvProfile == agvProfile)
            .OrderBy(row => row.EdgeId)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return rows
            .Select(row => new EdgePassability(
                row.MapVersionId,
                row.EdgeId,
                row.AgvProfile,
                row.IsPassable,
                ParseClass(row.CorridorClass),
                row.Reason))
            .ToList();
    }

    private static CorridorClass ParseClass(string value) =>
        Enum.TryParse<CorridorClass>(value, ignoreCase: true, out var parsed) ? parsed : CorridorClass.C1;
}
