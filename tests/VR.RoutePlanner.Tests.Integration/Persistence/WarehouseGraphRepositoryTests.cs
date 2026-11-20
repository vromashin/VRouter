using VR.RoutePlanner.Domain.Traffic;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VR.RoutePlanner.Application.Configuration;
using VR.RoutePlanner.Infrastructure.Persistence;
using VR.RoutePlanner.Tests.Integration.Support;

namespace VR.RoutePlanner.Tests.Integration.Persistence;

/// <summary>
/// Интеграционные проверки загрузки графа карты 801 из базы <c>rms</c>.
/// Без переменной окружения <c>VRP_RMS_CONNECTION</c> тесты пропускаются.
/// </summary>
public sealed class WarehouseGraphRepositoryTests
{
    [SkippableFact]
    public async Task LoadAsync_Map801_ReturnsActiveGraph()
    {
        var connectionString = TestDatabase.RequireConnection();
        var repository = CreateRepository(connectionString);

        var graph = await repository.LoadAsync(TestDatabase.MapId, CancellationToken.None);

        Assert.Equal(TestDatabase.MapId, graph.MapId);
        Assert.NotEmpty(graph.Nodes);
        Assert.NotEmpty(graph.Edges);
        Assert.StartsWith(graph.MapVersion, graph.GraphVersion, StringComparison.Ordinal);
        Assert.All(graph.Nodes.Values, node => Assert.Equal(graph.MapKey, node.MapKey));
    }

    [SkippableFact]
    public async Task LoadAsync_Twice_ReturnsSameCachedInstance()
    {
        var connectionString = TestDatabase.RequireConnection();
        var repository = CreateRepository(connectionString);

        var first = await repository.LoadAsync(TestDatabase.MapId, CancellationToken.None);
        var second = await repository.LoadAsync(TestDatabase.MapId, CancellationToken.None);

        Assert.Same(first, second);
    }

    [SkippableFact]
    public async Task GetGraphVersionAsync_MatchesLoadedGraph()
    {
        var connectionString = TestDatabase.RequireConnection();
        var repository = CreateRepository(connectionString);

        var graph = await repository.LoadAsync(TestDatabase.MapId, CancellationToken.None);
        var version = await repository.GetGraphVersionAsync(TestDatabase.MapId, CancellationToken.None);

        Assert.Equal(graph.GraphVersion, version);
    }

    [SkippableFact]
    public async Task LoadAsync_UnknownMap_Throws()
    {
        var connectionString = TestDatabase.RequireConnection();
        var repository = CreateRepository(connectionString);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => repository.LoadAsync("000-not-a-map", CancellationToken.None));
    }

    private static WarehouseGraphRepository CreateRepository(string connectionString)
    {
        var factory = new TestDbContextFactory(TestDatabase.BuildOptions(connectionString));
        var cache = new MemoryCache(new MemoryCacheOptions());
        var options = Options.Create(new RoutePlannerOptions { WidthSource = WidthSource.Default });

        return new WarehouseGraphRepository(
            factory,
            cache,
            options,
            NullLogger<WarehouseGraphRepository>.Instance);
    }
}
