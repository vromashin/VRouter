using Microsoft.Extensions.Options;
using VR.RoutePlanner.Application.Configuration;
using VR.RoutePlanner.Application.Graph;
using VR.RoutePlanner.Application.Planning;
using VR.RoutePlanner.Application.Startup;
using VR.RoutePlanner.Domain.Graph;

namespace VR.RoutePlanner.Tests.Unit.Planning;

/// <summary>
/// Подставные зависимости планировщика: без базы и сети.
/// </summary>
internal sealed class FakeGraphRepository(WarehouseGraph graph) : IWarehouseGraphRepository
{
    public Task<WarehouseGraph> LoadAsync(string mapId, CancellationToken ct) => Task.FromResult(graph);

    public Task<string> GetGraphVersionAsync(string mapId, CancellationToken ct) =>
        Task.FromResult(graph.GraphVersion);

    public Task InvalidateAsync(string mapId, CancellationToken ct) => Task.CompletedTask;
}

internal sealed class FakeMapVersionRepository(Guid? versionId) : IMapVersionRepository
{
    public Task<Guid> GetOrCreateAsync(string mapId, WarehouseGraph graph, CancellationToken ct) =>
        versionId is null ? throw new InvalidOperationException("Версии нет.") : Task.FromResult(versionId.Value);

    public Task<Guid?> TryGetAsync(string mapId, string graphVersion, CancellationToken ct) =>
        Task.FromResult(versionId);
}

internal sealed class FakeEdgeWidthRepository(IReadOnlyList<EdgeWidth> widths) : IEdgeWidthRepository
{
    public Task<EdgeWidthLoad> GetOrCreateAsync(
        Guid mapVersionId,
        WarehouseGraph graph,
        string mapId,
        CancellationToken ct) =>
        Task.FromResult(new EdgeWidthLoad(widths, widths.Count > 0));

    public Task<EdgeWidthLoad> ReadAsync(Guid mapVersionId, CancellationToken ct) =>
        Task.FromResult(new EdgeWidthLoad(widths, widths.Count > 0));
}

internal sealed class FakeEdgePassabilityRepository(IReadOnlyList<EdgePassability> rows)
    : IEdgePassabilityRepository
{
    public Task<IReadOnlyList<EdgePassability>> GetAsync(
        Guid mapVersionId,
        string agvProfile,
        CancellationToken ct) =>
        Task.FromResult(rows);
}

internal sealed class FakeResourceEdgeRepository(IReadOnlyList<Domain.Traffic.TrafficResourceEdge> rows)
    : IResourceEdgeRepository
{
    public Task<IReadOnlyList<Domain.Traffic.TrafficResourceEdge>> GetAsync(
        Guid mapVersionId,
        string agvProfile,
        CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<Domain.Traffic.TrafficResourceEdge>>(
            rows.Where(row => string.Equals(row.AgvProfile, agvProfile, StringComparison.Ordinal)).ToList());
}

internal sealed class ZeroWaitEstimator : IWaitEstimator
{
    public ValueTask<double> EstimateAsync(RoutePlanRequest request, Guid edgeId, CancellationToken ct) =>
        ValueTask.FromResult(0.0);
}

internal sealed class FixedOptions<T>(T value) : IOptions<T>
    where T : class
{
    public T Value { get; } = value;
}
