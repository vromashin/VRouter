using Microsoft.Extensions.Logging.Abstractions;
using VR.RoutePlanner.Application.Robots;
using VR.RoutePlanner.Domain.Graph;
using VR.RoutePlanner.Tests.Unit.Planning;
using Xunit;

namespace VR.RoutePlanner.Tests.Unit.Robots;

/// <summary>
/// Резолв последнего узла робота: один на контроллер и трафик, без исключений
/// на неизвестного робота и неизвестный узел.
/// </summary>
public sealed class RobotLastNodeResolverTests
{
    [Fact]
    public async Task Resolve_KnownNode_ReturnsNodeWithType()
    {
        var states = new FakeStateReader();
        states.Snapshots[OrderTestData.Serial] = OrderTestData.Snapshot("ORD_04331", lastNodeId: "wa500");
        var resolver = Create(Node("wa500", "WORKING"), states);

        var node = await resolver.ResolveAsync(OrderTestData.Serial, CancellationToken.None);

        Assert.NotNull(node);
        Assert.Equal("wa500", node!.NodeId);
        Assert.Equal("WORKING", node.NodeType);
    }

    [Fact]
    public async Task Resolve_UnknownNode_ReturnsNull()
    {
        var states = new FakeStateReader();
        states.Snapshots[OrderTestData.Serial] = OrderTestData.Snapshot("ORD_04331", lastNodeId: "wa999");
        var resolver = Create(Node("wa500", "WORKING"), states);

        Assert.Null(await resolver.ResolveAsync(OrderTestData.Serial, CancellationToken.None));
    }

    [Fact]
    public async Task Resolve_NoState_ReturnsNull()
    {
        var resolver = Create(Node("wa500", "WORKING"));

        Assert.Null(await resolver.ResolveAsync("98999", CancellationToken.None));
    }

    private static RobotLastNodeResolver Create(WarehouseNode node, FakeStateReader? states = null) =>
        new(
            states ?? new FakeStateReader(),
            new FakeGraphRepository(OrderFixtures.Graph([node], [])),
            NullLogger<RobotLastNodeResolver>.Instance);

    private static WarehouseNode Node(string code, string nodeType) =>
        new(
            Guid.Parse("cccccccc-0000-0000-0000-000000000001"),
            code,
            Guid.Empty,
            1.5,
            2.5,
            0,
            0.1,
            0.1,
            nodeType,
            null,
            null,
            true);
}
