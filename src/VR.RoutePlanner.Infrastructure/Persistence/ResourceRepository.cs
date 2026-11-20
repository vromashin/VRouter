using Microsoft.EntityFrameworkCore;
using VR.RoutePlanner.Application.Traffic;
using VR.RoutePlanner.Domain.Traffic;

namespace VR.RoutePlanner.Infrastructure.Persistence;

/// <summary>
/// Запись ресурсов в таблицы <c>vrt_*</c>.
/// </summary>
public sealed class ResourceRepository : IResourceRepository
{
    private readonly IDbContextFactory<RoutePlannerDbContext> _contextFactory;

    /// <summary>
    /// Создает репозиторий ресурсов.
    /// </summary>
    /// <param name="contextFactory">Фабрика контекста базы <c>rms</c>.</param>
    public ResourceRepository(IDbContextFactory<RoutePlannerDbContext> contextFactory)
    {
        _contextFactory = contextFactory;
    }

    /// <summary>
    /// Строит строки связей ресурса с ребрами.
    /// Связи без ресурса (<see cref="Guid.Empty"/>) отбрасываются: иначе нарушится FK
    /// <c>vrt_TrafficResourceEdge.TrafficResourceId</c>. Такие связи остаются только в памяти
    /// как отметка «ребро непроходимо для профиля».
    /// </summary>
    /// <param name="sets">Наборы ресурсов по профилям.</param>
    /// <returns>Строки для таблицы <c>vrt_TrafficResourceEdge</c>.</returns>
    internal static List<TrafficResourceEdgeRecord> MapEdges(IEnumerable<ProfileResourceSet> sets)
    {
        ArgumentNullException.ThrowIfNull(sets);

        var rows = new List<TrafficResourceEdgeRecord>();
        foreach (var set in sets)
        {
            foreach (var link in set.ResourceEdges)
            {
                if (link.TrafficResourceId == Guid.Empty)
                {
                    continue;
                }

                rows.Add(new TrafficResourceEdgeRecord
                {
                    TrafficResourceId = link.TrafficResourceId,
                    AgvProfile = link.AgvProfile,
                    EdgeId = link.EdgeId,
                    Direction = link.Direction,
                    Sequence = link.Sequence,
                    Class = (int)link.Class,
                    IsPassable = link.IsPassable,
                });
            }
        }

        return rows;
    }

    /// <inheritdoc />
    public async Task SaveAsync(Guid mapVersionId, IReadOnlyList<ProfileResourceSet> sets, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sets);

        if (mapVersionId == Guid.Empty)
        {
            throw new InvalidOperationException("Для сохранения ресурсов нужен идентификатор версии карты.");
        }

        var edges = MapEdges(sets);

        var ids = sets
            .SelectMany(set => set.Resources)
            .Select(resource => resource.Id)
            .ToList();

        await using var context = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using var transaction = await context.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

        if (ids.Count > 0)
        {
            await context.ResourceConflicts
                .Where(row => ids.Contains(row.ResourceId) || ids.Contains(row.ConflictingResourceId))
                .ExecuteDeleteAsync(ct)
                .ConfigureAwait(false);
            await context.TrafficResourceEdges
                .Where(row => ids.Contains(row.TrafficResourceId))
                .ExecuteDeleteAsync(ct)
                .ConfigureAwait(false);
            await context.TrafficResourceNodes
                .Where(row => ids.Contains(row.TrafficResourceId))
                .ExecuteDeleteAsync(ct)
                .ConfigureAwait(false);
            await context.TrafficResources
                .Where(row => row.MapVersionId == mapVersionId)
                .ExecuteDeleteAsync(ct)
                .ConfigureAwait(false);
        }

        foreach (var set in sets)
        {
            foreach (var resource in set.Resources)
            {
                context.TrafficResources.Add(new TrafficResourceRecord
                {
                    Id = resource.Id,
                    MapVersionId = mapVersionId,
                    AgvProfile = set.AgvProfile,
                    Code = resource.Code,
                    ResourceType = resource.ResourceType,
                    Capacity = resource.Capacity,
                    MinEntryInterval = resource.MinEntryInterval,
                    MaxOccupancyTime = resource.MaxOccupancyTime,
                    IsActive = resource.IsActive,
                });
            }

            foreach (var node in set.ResourceNodes)
            {
                context.TrafficResourceNodes.Add(new TrafficResourceNodeRecord
                {
                    TrafficResourceId = node.TrafficResourceId,
                    NodeId = node.NodeId,
                    AgvProfile = set.AgvProfile,
                });
            }

            foreach (var conflict in set.Conflicts)
            {
                context.ResourceConflicts.Add(new ResourceConflictRecord
                {
                    ResourceId = conflict.ResourceId,
                    ConflictingResourceId = conflict.ConflictingResourceId,
                    ConflictType = conflict.ConflictType,
                });
            }
        }

        context.TrafficResourceEdges.AddRange(edges);

        await context.SaveChangesAsync(ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }
}
