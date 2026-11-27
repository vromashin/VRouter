using System.Text.Json.Nodes;
using VR.RoutePlanner.Application.Robots;
using VR.RoutePlanner.Domain.Graph;
using VR.RoutePlanner.Domain.Planning;
using VR.RoutePlanner.Infrastructure.Mqtt;
using Xunit;

namespace VR.RoutePlanner.Tests.Unit.Robots;

/// <summary>
/// Действие <c>startCharging</c> в целевом узле <c>CHARGING</c> (Д-4): блокирующий тип SOFT,
/// параметры устройства, одно действие в узле; у узла <c>PARKING</c> действий нет.
/// </summary>
public sealed class VdaOrderChargingActionTests
{
    private const double RobotLengthMeters = 1.455;

    [Fact]
    public void Build_ChargingTarget_HasSingleStartChargingAction()
    {
        var (graph, edge, _, slot) = OrderFixtures.SlotGraph(RelocationNodeTypes.Charging);
        var route = OrderFixtures.Route([(edge.EdgeId, RouteEdgeDirection.Forward)]);

        var order = VdaOrderBuilder.Build(OrderFixtures.Request(
            route,
            graph,
            targetNodeActionType: ChargingNodeAction.ActionType,
            targetNodeActionBlockingType: ChargingNodeAction.BlockingType,
            targetNodeActionParameters: ChargingNodeAction.Parameters(slot, RobotLengthMeters, "20373")));

        var node = order["nodes"]![1]!;
        var actions = node["actions"]!.AsArray();
        var action = Assert.Single(actions)!;

        Assert.Equal("startCharging", action["actionType"]!.GetValue<string>());
        Assert.Equal("SOFT", action["blockingType"]!.GetValue<string>());
        Assert.False(string.IsNullOrWhiteSpace(action["actionId"]!.GetValue<string>()));

        var parameters = action["actionParameters"]!.AsArray();
        Assert.Equal(
            ["chg_id", "chargerX", "chargerY", "thetaWhileCharging"],
            parameters.Select(parameter => parameter!["key"]!.GetValue<string>()));

        Assert.Equal("20373", parameters[0]!["value"]!.GetValue<string>());

        // Точка стыковки смещена от узла по оси вил на половину корпуса плюс 0.25 м;
        // thetaWhileCharging — разворот на 180 градусов от курса узла.
        var offset = (RobotLengthMeters / 2) + 0.25;
        Assert.Equal(slot.X + (offset * Math.Cos(-slot.Theta)), parameters[1]!["value"]!.GetValue<double>(), 9);
        Assert.Equal(slot.Y + (offset * Math.Sin(-slot.Theta)), parameters[2]!["value"]!.GetValue<double>(), 9);
        Assert.Equal(-slot.Theta, parameters[3]!["value"]!.GetValue<double>(), 9);
    }

    [Fact]
    public void Build_ChargingTarget_DoesNotMixForkParameters()
    {
        var (graph, edge, _, slot) = OrderFixtures.SlotGraph(RelocationNodeTypes.Charging);
        var route = OrderFixtures.Route([(edge.EdgeId, RouteEdgeDirection.Forward)]);

        var order = VdaOrderBuilder.Build(OrderFixtures.Request(
            route,
            graph,
            targetNodeActionType: ChargingNodeAction.ActionType,
            targetNodeActionParameters: ChargingNodeAction.Parameters(slot, RobotLengthMeters, "20373")));

        var keys = order["nodes"]![1]!["actions"]![0]!["actionParameters"]!
            .AsArray()
            .Select(parameter => parameter!["key"]!.GetValue<string>())
            .ToList();

        Assert.DoesNotContain("containerTypeId", keys);
        Assert.DoesNotContain("onlyFork", keys);
        Assert.DoesNotContain("autoReturnNode", keys);
    }

    [Fact]
    public void Build_ParkingTarget_HasNoActions()
    {
        var (graph, edge, _, _) = OrderFixtures.SlotGraph(RelocationNodeTypes.Parking);
        var route = OrderFixtures.Route([(edge.EdgeId, RouteEdgeDirection.Forward)]);

        var order = VdaOrderBuilder.Build(OrderFixtures.Request(route, graph));

        Assert.All(order["nodes"]!.AsArray(), node => Assert.Empty(node!["actions"]!.AsArray()));
    }

    [Fact]
    public void Parameters_ZeroTheta_PutsChargerAheadOfNode()
    {
        var node = OrderFixtures.Node("ch1", 10, 4, RelocationNodeTypes.Charging);

        var parameters = ChargingNodeAction.Parameters(node, RobotLengthMeters, "20373");

        Assert.Equal(10 + (RobotLengthMeters / 2) + 0.25, (double)parameters["chargerX"]!, 9);
        Assert.Equal(4, (double)parameters["chargerY"]!, 9);
        Assert.Equal(0, (double)parameters["thetaWhileCharging"]!, 9);
    }
}
