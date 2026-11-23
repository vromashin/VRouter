using Microsoft.AspNetCore.Mvc;
using VR.RoutePlanner.Api.Contracts;
using VR.RoutePlanner.Api.Controllers;
using VR.RoutePlanner.Domain.Graph;
using Xunit;

namespace VR.RoutePlanner.Tests.Unit.Robots;

/// <summary>
/// Шим <c>GET /api/AgvTask/lastnode/{serial}</c>: ответ всегда 200.
/// </summary>
public sealed class LegacyAgvTaskControllerTests
{
    [Fact]
    public async Task LastNode_UnknownRobot_Returns200WithNull()
    {
        var controller = Create(Node("wa500"));

        var result = await controller.LastNodeAsync("98999", CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal(200, ok.StatusCode);
        Assert.Null(ok.Value);
    }

    [Fact]
    public async Task LastNode_RobotWithoutState_Returns200WithNull()
    {
        var controller = Create(Node("wa500"));

        var result = await controller.LastNodeAsync(OrderTestData.Serial, CancellationToken.None);

        Assert.Null(Assert.IsType<OkObjectResult>(result).Value);
    }

    [Fact]
    public async Task LastNode_UnknownNode_Returns200WithNull()
    {
        var (controller, states) = CreateWithStates(Node("wa500"));
        states.Snapshots[OrderTestData.Serial] = OrderTestData.Snapshot("ORD_04331", lastNodeId: "wa999");

        var result = await controller.LastNodeAsync(OrderTestData.Serial, CancellationToken.None);

        Assert.Null(Assert.IsType<OkObjectResult>(result).Value);
    }

    [Fact]
    public async Task LastNode_KnownNode_ReturnsNode()
    {
        var (controller, states) = CreateWithStates(Node("wa500"));
        states.Snapshots[OrderTestData.Serial] = OrderTestData.Snapshot("ORD_04331", lastNodeId: "wa500");

        var result = await controller.LastNodeAsync(OrderTestData.Serial, CancellationToken.None);

        var node = Assert.IsType<AgvNodeResponse>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal("wa500", node.NodeId);
        Assert.Equal(Guid.Empty, node.MapId);
        Assert.Equal(1.5, node.PositionX, 9);
        Assert.Equal("WORKING", node.NodeType);
        Assert.Equal("{}", node.NodeDescription);
    }

    private static LegacyAgvTaskController Create(WarehouseNode node) => CreateWithStates(node).Controller;

    private static (LegacyAgvTaskController Controller, FakeStateReader States) CreateWithStates(WarehouseNode node)
    {
        var graph = OrderFixtures.Graph([node], []);
        var states = new FakeStateReader();

        return (
            new LegacyAgvTaskController(states, new Planning.FakeGraphRepository(graph)),
            states);
    }

    private static WarehouseNode Node(string code) =>
        new(
            Guid.Parse("cccccccc-0000-0000-0000-000000000001"),
            code,
            Guid.Empty,
            1.5,
            2.5,
            0,
            0.1,
            0.1,
            "WORKING",
            null,
            null,
            true);
}
