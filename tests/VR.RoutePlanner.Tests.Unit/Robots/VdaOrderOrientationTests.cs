using System.Text.Json.Nodes;
using VR.RoutePlanner.Domain.Planning;
using VR.RoutePlanner.Infrastructure.Mqtt;
using Xunit;

namespace VR.RoutePlanner.Tests.Unit.Robots;

/// <summary>
/// Поле <c>orientation</c> у ребер: задается только у первого ребра сообщения.
/// </summary>
public sealed class VdaOrderOrientationTests
{
    [Fact]
    public void Build_AllForward_FirstEdgeOrientationIsZero()
    {
        var (graph, edges) = OrderFixtures.LineGraph(storedBackwards: false);
        var route = OrderFixtures.Route(
        [
            (edges[0].EdgeId, RouteEdgeDirection.Forward),
            (edges[1].EdgeId, RouteEdgeDirection.Forward),
            (edges[2].EdgeId, RouteEdgeDirection.Forward),
        ]);

        var order = VdaOrderBuilder.Build(OrderFixtures.Request(route, graph));

        Assert.Equal([0.0, 0.0, 0.0], Orientations(order));
    }

    [Fact]
    public void Build_FirstEdgeBackward_FirstEdgeOrientationIsPi()
    {
        var (graph, edges) = OrderFixtures.LineGraph(storedBackwards: true);
        var route = OrderFixtures.Route(
        [
            (edges[0].EdgeId, RouteEdgeDirection.Backward),
            (edges[1].EdgeId, RouteEdgeDirection.Forward),
            (edges[2].EdgeId, RouteEdgeDirection.Backward),
        ]);

        var order = VdaOrderBuilder.Build(OrderFixtures.Request(route, graph));

        // Только у первого ребра сообщения: PI, потому что перегон идет против хранения.
        var orientations = Orientations(order);
        Assert.Equal(Math.PI, orientations[0], 9);

        // Направление следующих ребер на orientation не влияет, как в AGVControl.
        Assert.Equal([0.0, 0.0], orientations.Skip(1));
    }

    [Fact]
    public void Build_SliceFirstEdgeBackward_OrientationIsPi()
    {
        var (graph, edges) = OrderFixtures.LineGraph(storedBackwards: true);
        var route = OrderFixtures.Route(
        [
            (edges[0].EdgeId, RouteEdgeDirection.Backward),
            (edges[1].EdgeId, RouteEdgeDirection.Forward),
            (edges[2].EdgeId, RouteEdgeDirection.Backward),
        ]);

        var order = VdaOrderBuilder.Build(OrderFixtures.Request(
            route,
            graph,
            orderUpdateId: 1,
            sliceStartIndex: 2,
            sliceNodeCount: 2));

        // Кусок начинается со второго ребра плана, и оно Backward: правило считается
        // по первому ребру сообщения, а не по первому ребру плана.
        var edge = Assert.Single(order["edges"]!.AsArray());
        Assert.Equal(Math.PI, edge!["orientation"]!.GetValue<double>(), 9);
    }

    [Fact]
    public void Build_BackwardEdge_TrajectoryStartsAtTraversalStart()
    {
        var (graph, edges) = OrderFixtures.LineGraph(storedBackwards: false);
        var route = OrderFixtures.Route([(edges[0].EdgeId, RouteEdgeDirection.Backward)]);

        var order = VdaOrderBuilder.Build(OrderFixtures.Request(route, graph));

        var edge = order["edges"]![0]!;
        Assert.Equal("wa301", edge["startNodeId"]!.GetValue<string>());
        Assert.Equal("wa300", edge["endNodeId"]!.GetValue<string>());

        var points = edge["trajectory"]!["controlPoints"]!.AsArray();
        Assert.Equal(2.0, points[0]!["x"]!.GetValue<double>(), 9);
        Assert.Equal(0.0, points[1]!["x"]!.GetValue<double>(), 9);
    }

    private static IReadOnlyList<double> Orientations(JsonObject order) =>
        order["edges"]!.AsArray().Select(edge => edge!["orientation"]!.GetValue<double>()).ToList();
}
