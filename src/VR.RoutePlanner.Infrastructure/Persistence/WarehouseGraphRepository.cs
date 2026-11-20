using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VR.RoutePlanner.Application.Configuration;
using VR.RoutePlanner.Application.Graph;
using VR.RoutePlanner.Domain.Graph;
using VR.RoutePlanner.Infrastructure.Graph;

namespace VR.RoutePlanner.Infrastructure.Persistence;

/// <summary>
/// Читает <c>public."Maps"</c>, <c>public."Nodes"</c> и <c>public."Edges"</c> и кэширует граф в памяти.
/// В эти таблицы сервис не пишет.
/// </summary>
public sealed class WarehouseGraphRepository : IWarehouseGraphRepository
{
    private readonly IDbContextFactory<RoutePlannerDbContext> _contextFactory;
    private readonly IMemoryCache _cache;
    private readonly IOptions<RoutePlannerOptions> _options;
    private readonly ILogger<WarehouseGraphRepository> _logger;

    /// <summary>
    /// Создает репозиторий графа.
    /// </summary>
    /// <param name="contextFactory">Фабрика контекста базы <c>rms</c>.</param>
    /// <param name="cache">Кэш в памяти процесса.</param>
    /// <param name="options">Параметры маршрутизации.</param>
    /// <param name="logger">Логгер.</param>
    public WarehouseGraphRepository(
        IDbContextFactory<RoutePlannerDbContext> contextFactory,
        IMemoryCache cache,
        IOptions<RoutePlannerOptions> options,
        ILogger<WarehouseGraphRepository> logger)
    {
        _contextFactory = contextFactory;
        _cache = cache;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<WarehouseGraph> LoadAsync(string mapId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mapId);

        var key = CacheKey(mapId);
        if (_cache.TryGetValue(key, out WarehouseGraph? cached) && cached is not null)
        {
            return cached;
        }

        var graph = await LoadCoreAsync(mapId, ct).ConfigureAwait(false);
        _cache.Set(
            key,
            graph,
            new MemoryCacheEntryOptions
            {
                Priority = CacheItemPriority.NeverRemove,
            });

        return graph;
    }

    /// <inheritdoc />
    public async Task<string> GetGraphVersionAsync(string mapId, CancellationToken ct)
    {
        var graph = await LoadAsync(mapId, ct).ConfigureAwait(false);
        return graph.GraphVersion;
    }

    /// <inheritdoc />
    public Task InvalidateAsync(string mapId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mapId);
        _cache.Remove(CacheKey(mapId));
        return Task.CompletedTask;
    }

    private static string CacheKey(string mapId) => "vr-route-planner:graph:" + mapId;

    private async Task<WarehouseGraph> LoadCoreAsync(string mapId, CancellationToken ct)
    {
        var options = _options.Value;
        await using var context = await _contextFactory
            .CreateDbContextAsync(ct)
            .ConfigureAwait(false);

        var maps = await ActiveMapsQuery(context, mapId)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (maps.Count > 1)
        {
            _logger.LogWarning(
                "Для карты {MapId} активно несколько строк Maps: {Versions}. Взята самая свежая версия.",
                mapId,
                ActiveMapSelector.DescribeVersions(maps));
        }

        var map = ActiveMapSelector.Select(maps, mapId);

        var nodeRows = await ActiveNodesQuery(context, map.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var edgeRows = await ActiveEdgesQuery(context, map.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var nodes = new Dictionary<Guid, WarehouseNode>(nodeRows.Count);
        foreach (var row in nodeRows)
        {
            var node = new WarehouseNode(
                row.Id,
                row.NodeId,
                row.MapId,
                row.PositionX,
                row.PositionY,
                row.Theta,
                row.AllowedDeviationXY,
                row.AllowedDeviationTheta,
                row.NodeType ?? string.Empty,
                row.RackId,
                row.Z,
                row.IsActive);

            nodes[node.Id] = node;
        }

        var edgesByNode = new Dictionary<Guid, List<WarehouseEdge>>(edgeRows.Count);
        foreach (var row in edgeRows)
        {
            var parsed = TrajectoryParser.Parse(
                row.Trajectory,
                options.TrajectorySampleStep,
                _logger,
                row.EdgeId);

            var edge = new WarehouseEdge(
                row.Id,
                row.EdgeId,
                row.FromNodeId,
                row.ToNodeId,
                row.MapId,
                row.Length,
                row.MaxSpeed ?? 0,
                row.Bidirectional,
                row.Corridor,
                row.MaxRotationSpeed,
                row.Orientation,
                row.OrientationType,
                row.RotationAllowed,
                parsed.Points,
                row.Trajectory,
                row.IsActive);

            AddToIndex(edgesByNode, row.FromNodeId, edge);
            if (row.Bidirectional && row.ToNodeId != row.FromNodeId)
            {
                AddToIndex(edgesByNode, row.ToNodeId, edge);
            }
        }

        var edges = edgesByNode.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<WarehouseEdge>)pair.Value.AsReadOnly());

        var graphVersion = BuildGraphVersion(
            map.MapVersion,
            nodeRows.Select(row => row.NodeId),
            edgeRows.Select(row => row.EdgeId));

        _logger.LogInformation(
            "Граф карты {MapId} загружен: узлов {NodeCount}, ребер {EdgeCount}, версия графа {GraphVersion}, версия карты {MapVersion}.",
            mapId,
            nodes.Count,
            edgeRows.Count,
            graphVersion,
            map.MapVersion);

        return new WarehouseGraph
        {
            MapKey = map.Id,
            MapId = mapId,
            MapVersion = map.MapVersion,
            GraphVersion = graphVersion,
            Nodes = nodes,
            Edges = edges,
        };
    }

    private static void AddToIndex(
        Dictionary<Guid, List<WarehouseEdge>> index,
        Guid nodeKey,
        WarehouseEdge edge)
    {
        if (!index.TryGetValue(nodeKey, out var list))
        {
            list = new List<WarehouseEdge>();
            index[nodeKey] = list;
        }

        list.Add(edge);
    }

    /// <summary>
    /// Активные строки карты с детерминированным порядком: от свежей версии к старой.
    /// </summary>
    /// <param name="context">Контекст базы <c>rms</c>.</param>
    /// <param name="mapId">Идентификатор карты.</param>
    /// <returns>Упорядоченная выборка.</returns>
    public static IQueryable<RmsMapRecord> ActiveMapsQuery(RoutePlannerDbContext context, string mapId)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(mapId);

        return context.Maps
            .AsNoTracking()
            .Where(record => record.MapId == mapId && record.IsActive)
            .OrderBy(record => record.MapVersion)
            .ThenBy(record => record.Id);
    }

    /// <summary>
    /// Активные узлы карты с детерминированным порядком обхода.
    /// </summary>
    /// <param name="context">Контекст базы <c>rms</c>.</param>
    /// <param name="mapKey">Первичный ключ строки <c>Maps</c>.</param>
    /// <returns>Упорядоченная выборка.</returns>
    public static IQueryable<RmsNodeRecord> ActiveNodesQuery(RoutePlannerDbContext context, Guid mapKey)
    {
        ArgumentNullException.ThrowIfNull(context);

        return context.Nodes
            .AsNoTracking()
            .Where(record => record.MapId == mapKey && record.IsActive)
            .OrderBy(record => record.NodeId)
            .ThenBy(record => record.Id);
    }

    /// <summary>
    /// Активные ребра карты с детерминированным порядком обхода.
    /// </summary>
    /// <param name="context">Контекст базы <c>rms</c>.</param>
    /// <param name="mapKey">Первичный ключ строки <c>Maps</c>.</param>
    /// <returns>Упорядоченная выборка.</returns>
    public static IQueryable<RmsEdgeRecord> ActiveEdgesQuery(RoutePlannerDbContext context, Guid mapKey)
    {
        ArgumentNullException.ThrowIfNull(context);

        return context.Edges
            .AsNoTracking()
            .Where(record => record.MapId == mapKey && record.IsActive)
            .OrderBy(record => record.EdgeId)
            .ThenBy(record => record.Id);
    }

    /// <summary>
    /// Собирает версию графа: версия карты плюс короткий хеш состава узлов и ребер.
    /// </summary>
    /// <param name="mapVersion">Версия карты из справочника.</param>
    /// <param name="nodeIds">Бизнес-идентификаторы узлов (<c>Nodes.NodeId</c>).</param>
    /// <param name="edgeIds">Бизнес-идентификаторы ребер (<c>Edges.EdgeId</c>).</param>
    /// <returns>Строка версии графа.</returns>
    public static string BuildGraphVersion(
        string mapVersion,
        IEnumerable<string> nodeIds,
        IEnumerable<string> edgeIds)
    {
        var builder = new StringBuilder();
        foreach (var id in nodeIds.OrderBy(value => value, StringComparer.Ordinal))
        {
            builder.Append("N:").Append(id).Append('\n');
        }

        foreach (var id in edgeIds.OrderBy(value => value, StringComparer.Ordinal))
        {
            builder.Append("E:").Append(id).Append('\n');
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        var shortHash = Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant();

        return string.Create(CultureInfo.InvariantCulture, $"{mapVersion}-{shortHash}");
    }
}
