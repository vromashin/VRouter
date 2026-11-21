using Microsoft.EntityFrameworkCore;
using VR.RoutePlanner.Application.Planning;
using VR.RoutePlanner.Domain.Traffic;
using VR.RoutePlanner.Infrastructure.Persistence;

namespace VR.RoutePlanner.Infrastructure.Persistence;

/// <summary>
/// Чтение реберных ресурсов профиля: строки <c>vrt_TrafficResourceEdge</c> выбранной версии карты.
/// </summary>
public sealed class ResourceEdgeRepository : IResourceEdgeRepository
{
    private readonly IDbContextFactory<RoutePlannerDbContext> _contextFactory;

    /// <summary>
    /// Создает репозиторий ресурсов ребер.
    /// </summary>
    /// <param name="contextFactory">Фабрика контекста базы <c>rms</c>.</param>
    public ResourceEdgeRepository(IDbContextFactory<RoutePlannerDbContext> contextFactory)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);

        _contextFactory = contextFactory;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TrafficResourceEdge>> GetAsync(
        Guid mapVersionId,
        string profile,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profile);

        if (mapVersionId == Guid.Empty)
        {
            throw new InvalidOperationException("Не задан идентификатор версии карты.");
        }

        await using var context = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var rows = await context.TrafficResourceEdges
            .AsNoTracking()
            .Where(edge => edge.AgvProfile == profile)
            .Join(
                context.TrafficResources
                    .AsNoTracking()
                    .Where(resource => resource.MapVersionId == mapVersionId && resource.AgvProfile == profile),
                edge => edge.TrafficResourceId,
                resource => resource.Id,
                (edge, _) => edge)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return rows
            .Select(row => new TrafficResourceEdge(
                row.TrafficResourceId,
                row.EdgeId,
                row.Direction,
                row.Sequence,
                row.AgvProfile))
            .ToList();
    }
}
