using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VR.RoutePlanner.Application.Configuration;
using VR.RoutePlanner.Application.Graph;
using VR.RoutePlanner.Application.Traffic;
using VR.RoutePlanner.Domain.Graph;
using VR.RoutePlanner.Domain.Traffic;
using VR.RoutePlanner.Tests.Unit.Support;

namespace VR.RoutePlanner.Tests.Unit.Traffic;

/// <summary>
/// Ревизия 4: ресурс рвется на перекрестке, в тупике, на стыке версий и на непроходимом ребре.
/// </summary>
public sealed class ResourceSplitTests
{
    private static readonly Guid MapVersionId = Guid.Parse("6f1d1f60-9f4c-4a43-9c2a-0f0f3f0a1a02");

    [Fact]
    public async Task BuildAsync_ThroughDegreeTwoNodes_MergesIntoSingleResource()
    {
        var set = await BuildAsync(Chain(3));

        Assert.Single(Corridors(set));
        Assert.All(set.ResourceEdges, link => Assert.Equal(Corridors(set)[0].Id, link.TrafficResourceId));
    }

    private static readonly System.Text.RegularExpressions.Regex CorridorCode =
        new(@"-C\d+-", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static IEnumerable<string> PassabilityText(ProfileResourceSet set) =>
        set.Passability.Select(pass => $"{pass.Class}:{pass.IsPassable}");

    private static List<TrafficResource> Corridors(ProfileResourceSet set) =>
        set.Resources.Where(resource => CorridorCode.IsMatch(resource.Code)).ToList();

    [Fact]
    public async Task BuildAsync_Crossroad_SplitsResources()
    {
        var graph = Chain(3, branch: true);

        var set = await BuildAsync(graph);

        Assert.True(Corridors(set).Count > 1, $"Ресурс не разорван на перекрестке: {Corridors(set).Count}.");
    }

    [Fact]
    public async Task BuildAsync_DeadEnd_DoesNotGlueAcrossDegreeOne()
    {
        var set = await BuildAsync(Chain(2));

        Assert.Single(Corridors(set));
    }

    [Fact]
    public async Task BuildAsync_UnpassableEdge_BreaksResource()
    {
        var options = CreateOptions();
        options.AgvProfiles = TestProfiles.Create(1.102);
        options.ManualCorridorWidth = 1.4;
        options.DefaultCorridorWidth = 1.4;
        options.SideMargin = 0.2;

        var set = await BuildAsync(Chain(3), options);

        Assert.True(
            Corridors(set).Count == 0,
            $"Коридоры: {string.Join(", ", Corridors(set).Select(resource => resource.Code))}; "
            + $"ширины: {string.Join(", ", PassabilityText(set))}");
        Assert.All(set.Passability, pass => Assert.False(pass.IsPassable));
    }

    [Fact]
    public void FromString_DifferentMapVersions_ProduceDifferentIds()
    {
        var first = DeterministicId.FromString(MapVersionId, "801-test-C1-1");
        var second = DeterministicId.FromString(Guid.NewGuid(), "801-test-C1-1");
        var same = DeterministicId.FromString(MapVersionId, "801-test-C1-1");

        Assert.NotEqual(first, second);
        Assert.Equal(first, same);
    }

    private static RoutePlannerOptions CreateOptions() => new()
    {
        WidthSource = WidthSource.Manual,
        DefaultCorridorWidth = 3.0,
        SideMargin = 0.1,
        InflationRadius = 0.4,
        AgvProfiles = TestProfiles.Create(),
        DefaultAgvProfile = TestProfiles.Name,
    };

    private static WarehouseGraph Chain(int edges, bool branch = false)
    {
        var nodes = new List<WarehouseNode>();
        var all = new List<WarehouseEdge>();

        for (var i = 0; i <= edges; i++)
        {
            nodes.Add(GraphFixtures.Node($"wa{200 + i}", i * 2.0, 0));
        }

        for (var i = 0; i < edges; i++)
        {
            all.Add(GraphFixtures.Edge(nodes[i], nodes[i + 1]));
        }

        if (branch)
        {
            var extra = GraphFixtures.Node("wa290", 4.0, 3.0);
            nodes.Add(extra);
            all.Add(GraphFixtures.Edge(nodes[2], extra));
        }

        return GraphFixtures.Graph(nodes, all);
    }

    private static async Task<ProfileResourceSet> BuildAsync(WarehouseGraph graph, RoutePlannerOptions? options = null)
    {
        options ??= CreateOptions();
        var raster = FakeMapRasterProvider.WithError(new InvalidOperationException("нет растра"));
        var builder = new TrafficResourceBuilder(
            raster,
            Options.Create(options),
            NullLogger<TrafficResourceBuilder>.Instance);

        var widths = await builder.ComputeWidthsAsync(graph, MapVersionId);
        var sets = await builder.BuildAsync(graph, widths, MapVersionId);

        return sets.Single(set => set.AgvProfile == options.DefaultAgvProfile);
    }
}
