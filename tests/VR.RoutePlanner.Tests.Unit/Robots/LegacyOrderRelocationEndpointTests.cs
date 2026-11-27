using Microsoft.AspNetCore.Mvc;
using VR.RoutePlanner.Api.Controllers;
using VR.RoutePlanner.Application.Robots;

namespace VR.RoutePlanner.Tests.Unit.Robots;

/// <summary>
/// Эндпоинты <c>move-to-charge</c> и <c>move-to-parking</c>: успех возвращает форму заказа,
/// отказ — <c>success:false</c> с кодом Д-8, третье поле ответа всегда <c>null</c>.
/// </summary>
public sealed class LegacyOrderRelocationEndpointTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    [Fact]
    public async Task MoveToCharge_ReturnsCreatedOrderWithoutActionId()
    {
        var harness = RelocationFixtures.Create();
        harness.States.Snapshots[RelocationFixtures.Serial] =
            RelocationFixtures.Snapshot(RelocationFixtures.Serial, battery: 15);
        var controller = new LegacyOrderController(harness.Orders, harness.Service);

        var result = await controller.MoveToChargeAsync(RelocationFixtures.Serial, null, Ct);

        var body = Assert.IsType<OkObjectResult>(result).Value!;
        Assert.True((bool)Property(body, "success")!);
        Assert.NotNull(Property(body, "orderId"));
        Assert.Null(Property(body, "actionId"));
        Assert.Equal(RelocationFixtures.Serial, (string?)Property(body, "agvSerialNumber"));
        Assert.Equal("ch1", Assert.Single(harness.Repository.Orders).Value.TargetNodeId);
    }

    [Fact]
    public async Task MoveToCharge_AllStationsBusy_ReturnsChargeNodeBusy()
    {
        var harness = RelocationFixtures.Create(RelocationFixtures.Graph(chargingCount: 1));
        harness.States.Snapshots[RelocationFixtures.Serial] =
            RelocationFixtures.Snapshot(RelocationFixtures.Serial, battery: 15);
        harness.States.Snapshots[RelocationFixtures.OtherSerial] = RelocationFixtures.Snapshot(
            RelocationFixtures.OtherSerial,
            battery: 90,
            lastNodeId: "ch1",
            x: 5,
            y: 0);
        var controller = new LegacyOrderController(harness.Orders, harness.Service);

        var result = await controller.MoveToChargeAsync(RelocationFixtures.Serial, null, Ct);

        var body = Assert.IsType<OkObjectResult>(result).Value!;
        Assert.False((bool)Property(body, "success")!);
        Assert.Equal(RobotRelocationService.CodeChargeNodeBusy, (string?)Property(body, "message"));
        Assert.Null(Property(body, "orderId"));
        Assert.Empty(harness.Repository.Orders);
    }

    [Fact]
    public async Task MoveToCharge_UnknownRobot_ReturnsStateNotFound()
    {
        var harness = RelocationFixtures.Create();
        var controller = new LegacyOrderController(harness.Orders, harness.Service);

        var result = await controller.MoveToChargeAsync(RelocationFixtures.Serial, null, Ct);

        var body = Assert.IsType<OkObjectResult>(result).Value!;
        Assert.False((bool)Property(body, "success")!);
        Assert.Equal(RobotRelocationService.CodeStateNotFound, (string?)Property(body, "message"));
    }

    [Fact]
    public async Task MoveToParking_ReturnsCreatedOrder()
    {
        var harness = RelocationFixtures.Create();
        harness.States.Snapshots[RelocationFixtures.Serial] =
            RelocationFixtures.Snapshot(RelocationFixtures.Serial, battery: 90);
        var controller = new LegacyOrderController(harness.Orders, harness.Service);

        var result = await controller.MoveToParkingAsync(RelocationFixtures.Serial, "pk2", Ct);

        var body = Assert.IsType<OkObjectResult>(result).Value!;
        Assert.True((bool)Property(body, "success")!);
        Assert.NotNull(Property(body, "orderId"));
        Assert.Null(Property(body, "actionId"));
        Assert.Equal("pk2", Assert.Single(harness.Repository.Orders).Value.TargetNodeId);
    }

    [Fact]
    public async Task MoveToParking_NoParkingNodesOnMap_ReturnsParkingNodeNotFound()
    {
        var harness = RelocationFixtures.Create(RelocationFixtures.Graph(parkingCount: 0));
        harness.States.Snapshots[RelocationFixtures.Serial] =
            RelocationFixtures.Snapshot(RelocationFixtures.Serial, battery: 90);
        var controller = new LegacyOrderController(harness.Orders, harness.Service);

        var result = await controller.MoveToParkingAsync(RelocationFixtures.Serial, null, Ct);

        var body = Assert.IsType<OkObjectResult>(result).Value!;
        Assert.False((bool)Property(body, "success")!);
        Assert.Equal(RobotRelocationService.CodeParkingNodeNotFound, (string?)Property(body, "message"));
    }

    private static object? Property(object body, string name) =>
        body.GetType().GetProperty(name)!.GetValue(body);
}
