using VR.RoutePlanner.Application.Robots;
using VR.RoutePlanner.Domain.Robots;

namespace VR.RoutePlanner.Tests.Unit.Robots;

/// <summary>
/// Автопереезд на зарядку и парковку: два робота на одну станцию, две станции на двух роботов,
/// все узлы заняты, занятый узел из пути запроса, действие <c>startCharging</c> в целевом узле.
/// </summary>
public sealed class RobotRelocationServiceTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    [Fact]
    public async Task MoveToCharge_TwoRobotsOneStation_SecondGetsChargeNodeBusy()
    {
        var harness = RelocationFixtures.Create(RelocationFixtures.Graph(chargingCount: 1));
        harness.States.Snapshots[RelocationFixtures.Serial] =
            RelocationFixtures.Snapshot(RelocationFixtures.Serial, battery: 15);

        var first = await harness.Service.MoveToChargeAsync(RelocationFixtures.Serial, null, Ct);

        Assert.True(first.Success);
        Assert.Equal("ch1", first.TargetNodeId);
        Assert.NotNull(first.OrderId);
        Assert.Equal(RelocationFixtures.Serial, harness.Service.Reservations["ch1"]);

        harness.States.Snapshots[RelocationFixtures.OtherSerial] = RelocationFixtures.Snapshot(
            RelocationFixtures.OtherSerial,
            battery: 18,
            lastNodeId: "wa101",
            y: 20);

        var second = await harness.Service.MoveToChargeAsync(RelocationFixtures.OtherSerial, null, Ct);

        Assert.False(second.Success);
        Assert.Equal(RobotRelocationService.CodeChargeNodeBusy, second.Message);
        Assert.Null(second.OrderId);
        Assert.Single(harness.Repository.Orders);
    }

    [Fact]
    public async Task MoveToCharge_TwoRobotsTwoStations_EachGoesToNearest()
    {
        var harness = RelocationFixtures.Create(RelocationFixtures.Graph());
        harness.States.Snapshots[RelocationFixtures.Serial] =
            RelocationFixtures.Snapshot(RelocationFixtures.Serial, battery: 15);
        harness.States.Snapshots[RelocationFixtures.OtherSerial] = RelocationFixtures.Snapshot(
            RelocationFixtures.OtherSerial,
            battery: 18,
            lastNodeId: "wa101",
            y: 20);

        var first = await harness.Service.MoveToChargeAsync(RelocationFixtures.Serial, null, Ct);
        var second = await harness.Service.MoveToChargeAsync(RelocationFixtures.OtherSerial, null, Ct);

        Assert.True(first.Success);
        Assert.True(second.Success);
        Assert.Equal("ch1", first.TargetNodeId);
        Assert.Equal("ch2", second.TargetNodeId);
        Assert.Equal(2, harness.Repository.Orders.Count);
    }

    [Fact]
    public async Task MoveToCharge_AllStationsOccupied_ReturnsBusyAndCreatesNoOrder()
    {
        var harness = RelocationFixtures.Create(RelocationFixtures.Graph(chargingCount: 1));
        harness.States.Snapshots[RelocationFixtures.Serial] =
            RelocationFixtures.Snapshot(RelocationFixtures.Serial, battery: 15);

        // Другой робот физически стоит на единственной станции: робот ждет, заказ не создается.
        harness.States.Snapshots[RelocationFixtures.OtherSerial] = RelocationFixtures.Snapshot(
            RelocationFixtures.OtherSerial,
            battery: 90,
            lastNodeId: "ch1",
            x: 5,
            y: 0);

        var result = await harness.Service.MoveToChargeAsync(RelocationFixtures.Serial, null, Ct);

        Assert.False(result.Success);
        Assert.Equal(RobotRelocationService.CodeChargeNodeBusy, result.Message);
        Assert.Empty(harness.Repository.Orders);
    }

    [Fact]
    public async Task MoveToCharge_NoChargingNodesOnMap_ReturnsChargeNodeNotFound()
    {
        var harness = RelocationFixtures.Create(RelocationFixtures.Graph(chargingCount: 0));
        harness.States.Snapshots[RelocationFixtures.Serial] =
            RelocationFixtures.Snapshot(RelocationFixtures.Serial, battery: 15);

        var result = await harness.Service.MoveToChargeAsync(RelocationFixtures.Serial, null, Ct);

        Assert.False(result.Success);
        Assert.Equal(RobotRelocationService.CodeChargeNodeNotFound, result.Message);
    }

    [Fact]
    public async Task MoveToCharge_ExplicitParkingNode_ReturnsNodeNotCharging()
    {
        var harness = RelocationFixtures.Create();
        harness.States.Snapshots[RelocationFixtures.Serial] =
            RelocationFixtures.Snapshot(RelocationFixtures.Serial, battery: 15);

        var result = await harness.Service.MoveToChargeAsync(RelocationFixtures.Serial, "pk1", Ct);

        Assert.False(result.Success);
        Assert.Equal(RobotRelocationService.CodeNodeNotCharging, result.Message);
        Assert.Empty(harness.Repository.Orders);
    }

    [Fact]
    public async Task MoveToParking_OccupiedParkingNode_ReturnsParkingNodeBusy()
    {
        var harness = RelocationFixtures.Create(RelocationFixtures.Graph(parkingCount: 1));
        harness.States.Snapshots[RelocationFixtures.Serial] =
            RelocationFixtures.Snapshot(RelocationFixtures.Serial, battery: 90);
        harness.States.Snapshots[RelocationFixtures.OtherSerial] = RelocationFixtures.Snapshot(
            RelocationFixtures.OtherSerial,
            battery: 95,
            lastNodeId: "pk1",
            x: 10,
            y: 0);

        var result = await harness.Service.MoveToParkingAsync(RelocationFixtures.Serial, "pk1", Ct);

        Assert.False(result.Success);
        Assert.Equal(RobotRelocationService.CodeParkingNodeBusy, result.Message);
        Assert.Empty(harness.Repository.Orders);
    }

    [Fact]
    public async Task MoveToCharge_RobotAlreadyOnChargingNode_SendsStartChargingWithoutOrder()
    {
        var harness = RelocationFixtures.Create();
        harness.States.Snapshots[RelocationFixtures.Serial] = RelocationFixtures.Snapshot(
            RelocationFixtures.Serial,
            battery: 15,
            lastNodeId: "ch1",
            x: 5,
            y: 0);

        var result = await harness.Service.MoveToChargeAsync(RelocationFixtures.Serial, null, Ct);

        Assert.True(result.Success);
        Assert.NotNull(result.ActionId);
        Assert.Null(result.OrderId);
        Assert.Equal("ch1", result.TargetNodeId);
        Assert.Single(harness.Publisher.Actions);
        Assert.Contains("\"actionType\":\"startCharging\"", harness.Publisher.Actions[0].Payload);
        Assert.Empty(harness.Repository.Orders);
    }

    [Fact]
    public async Task MoveToCharge_CreatesOrderWithStartChargingActionInTargetNode()
    {
        var harness = RelocationFixtures.Create();
        harness.States.Snapshots[RelocationFixtures.Serial] =
            RelocationFixtures.Snapshot(RelocationFixtures.Serial, battery: 15);

        var result = await harness.Service.MoveToChargeAsync(RelocationFixtures.Serial, null, Ct);

        Assert.True(result.Success);
        Assert.Equal("ch1", result.TargetNodeId);

        var request = Assert.Single(harness.Composer.Requests);
        Assert.Equal(ChargingNodeAction.ActionType, request.TargetNodeActionType);
        Assert.Equal(ChargingNodeAction.BlockingType, request.TargetNodeActionBlockingType);
        Assert.NotNull(request.TargetNodeActionParameters);
        Assert.Equal("20373", request.TargetNodeActionParameters!["chg_id"]);
        Assert.True(request.TargetNodeActionParameters!.ContainsKey("chargerX"));
        Assert.True(request.TargetNodeActionParameters!.ContainsKey("thetaWhileCharging"));

        var order = Assert.Single(harness.Repository.Orders).Value;
        Assert.Equal("ch1", order.TargetNodeId);
        Assert.Equal(RobotOrderStatuses.Sent, order.Status);
    }

    [Fact]
    public async Task MoveToParking_CreatesOrderWithoutTargetNodeAction()
    {
        var harness = RelocationFixtures.Create();
        harness.States.Snapshots[RelocationFixtures.Serial] =
            RelocationFixtures.Snapshot(RelocationFixtures.Serial, battery: 90);

        var result = await harness.Service.MoveToParkingAsync(RelocationFixtures.Serial, null, Ct);

        Assert.True(result.Success);
        Assert.Equal("pk1", result.TargetNodeId);

        var request = Assert.Single(harness.Composer.Requests);
        Assert.Null(request.TargetNodeActionType);
        Assert.Null(request.TargetNodeActionParameters);
        Assert.Equal("pk1", Assert.Single(harness.Repository.Orders).Value.TargetNodeId);
    }

    [Fact]
    public async Task MoveToParking_RobotAlreadyOnParkingNode_SucceedsWithoutOrderOrAction()
    {
        var harness = RelocationFixtures.Create();
        harness.States.Snapshots[RelocationFixtures.Serial] = RelocationFixtures.Snapshot(
            RelocationFixtures.Serial,
            battery: 90,
            lastNodeId: "pk1",
            x: 10,
            y: 0);

        var result = await harness.Service.MoveToParkingAsync(RelocationFixtures.Serial, "pk1", Ct);

        Assert.True(result.Success);
        Assert.Null(result.OrderId);
        Assert.Null(result.ActionId);
        Assert.Empty(harness.Repository.Orders);
        Assert.Empty(harness.Publisher.Actions);
    }

    [Fact]
    public async Task MoveToCharge_ActiveOrder_ReturnsFailureWithoutSecondOrder()
    {
        var harness = RelocationFixtures.Create();
        harness.States.Snapshots[RelocationFixtures.Serial] =
            RelocationFixtures.Snapshot(RelocationFixtures.Serial, battery: 15);
        harness.Repository.Orders["ORD_05000"] = RelocationFixtures.Order(
            RelocationFixtures.Serial,
            "ORD_05000",
            RobotOrderStatuses.Executing,
            "wa101");

        var result = await harness.Service.MoveToChargeAsync(RelocationFixtures.Serial, null, Ct);

        Assert.False(result.Success);
        Assert.Contains("ORD_05000", result.Message);
        Assert.Single(harness.Repository.Orders);
    }

    [Fact]
    public async Task MoveToCharge_UnknownRobotState_ReturnsStateNotFound()
    {
        var harness = RelocationFixtures.Create();

        var result = await harness.Service.MoveToChargeAsync(RelocationFixtures.Serial, null, Ct);

        Assert.False(result.Success);
        Assert.Equal(RobotRelocationService.CodeStateNotFound, result.Message);
    }
}
