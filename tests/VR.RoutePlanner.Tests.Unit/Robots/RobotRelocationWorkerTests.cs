using Microsoft.Extensions.Logging.Abstractions;
using VR.RoutePlanner.Application.Configuration;
using VR.RoutePlanner.Application.Robots;
using VR.RoutePlanner.Domain.Robots;
using VR.RoutePlanner.Tests.Unit.Planning;
using VR.RoutePlanner.Workers;

namespace VR.RoutePlanner.Tests.Unit.Robots;

/// <summary>
/// Тик фонового сервиса автопереездов: пакет зарядки (приоритет по заряду), пакет парковки
/// после простоя, пауза между попытками, событие завершения заряда на робота.
/// </summary>
public sealed class RobotRelocationWorkerTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    [Fact]
    public async Task TickAsync_ChargePriority_LowestBatteryGoesFirst()
    {
        var harness = RelocationFixtures.Create(RelocationFixtures.Graph(chargingCount: 1));
        harness.States.Snapshots[RelocationFixtures.Serial] =
            RelocationFixtures.Snapshot(RelocationFixtures.Serial, battery: 18);
        harness.States.Snapshots[RelocationFixtures.OtherSerial] = RelocationFixtures.Snapshot(
            RelocationFixtures.OtherSerial,
            battery: 10,
            lastNodeId: "wa101",
            y: 20);

        await CreateWorker(harness).TickAsync(Ct);

        var order = Assert.Single(harness.Repository.Orders).Value;
        Assert.Equal(RelocationFixtures.OtherSerial, order.AgvSerialNumber);
        Assert.Equal("ch1", order.TargetNodeId);
    }

    [Fact]
    public async Task TickAsync_TwoTicksInARow_ProduceSingleOrder()
    {
        var harness = RelocationFixtures.Create(RelocationFixtures.Graph(chargingCount: 1));
        harness.States.Snapshots[RelocationFixtures.Serial] =
            RelocationFixtures.Snapshot(RelocationFixtures.Serial, battery: 15);
        var worker = CreateWorker(harness);

        await worker.TickAsync(Ct);
        await worker.TickAsync(Ct);

        Assert.Single(harness.Repository.Orders);
        Assert.Single(harness.Composer.Requests);
    }

    [Fact]
    public async Task TickAsync_BusyStation_WaitsRetryDelayBeforeNextAttempt()
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
        var worker = CreateWorker(harness);

        await worker.TickAsync(Ct);
        Assert.Empty(harness.Repository.Orders);

        // Станция освободилась, но пауза повтора 60 с еще не истекла: заказ не создается.
        harness.States.Snapshots.Remove(RelocationFixtures.OtherSerial);
        harness.Time.Now = harness.Time.Now.AddSeconds(5);
        await worker.TickAsync(Ct);
        Assert.Empty(harness.Repository.Orders);

        harness.Time.Now = harness.Time.Now.AddSeconds(61);
        await worker.TickAsync(Ct);
        Assert.Equal("ch1", Assert.Single(harness.Repository.Orders).Value.TargetNodeId);
    }

    [Fact]
    public async Task TickAsync_MaxAttempts_StopsRetryLoop()
    {
        var options = new ChargingOptions { MaxAttempts = 2, RetryDelaySeconds = 0 };
        var harness = RelocationFixtures.Create(RelocationFixtures.Graph(chargingCount: 1), options);
        harness.States.Snapshots[RelocationFixtures.Serial] =
            RelocationFixtures.Snapshot(RelocationFixtures.Serial, battery: 15);
        harness.States.Snapshots[RelocationFixtures.OtherSerial] = RelocationFixtures.Snapshot(
            RelocationFixtures.OtherSerial,
            battery: 90,
            lastNodeId: "ch1",
            x: 5,
            y: 0);
        var worker = CreateWorker(harness, options);

        for (var tick = 0; tick < 4; tick++)
        {
            await worker.TickAsync(Ct);
        }

        // После MaxAttempts попыток заказы не создаются и сборщик не вызывается.
        Assert.Empty(harness.Repository.Orders);
        Assert.Empty(harness.Composer.Requests);
    }

    [Fact]
    public async Task TickAsync_BusyRobot_DoesNotBlockOtherRobot()
    {
        var harness = RelocationFixtures.Create(RelocationFixtures.Graph(chargingCount: 1));
        harness.States.Snapshots[RelocationFixtures.Serial] =
            RelocationFixtures.Snapshot(RelocationFixtures.Serial, battery: 15);
        harness.States.Snapshots[RelocationFixtures.OtherSerial] = RelocationFixtures.Snapshot(
            RelocationFixtures.OtherSerial,
            battery: 5,
            lastNodeId: "wa101",
            y: 20);
        harness.Repository.Orders["ORD_05000"] = RelocationFixtures.Order(
            RelocationFixtures.OtherSerial,
            "ORD_05000",
            RobotOrderStatuses.Executing,
            "wa101");

        await CreateWorker(harness).TickAsync(Ct);

        var order = Assert.Single(
            harness.Repository.Orders.Values,
            item => item.OrderId != "ORD_05000");
        Assert.Equal(RelocationFixtures.Serial, order.AgvSerialNumber);
    }

    [Fact]
    public async Task TickAsync_ChargingCompleted_IndependentEventsPerRobot()
    {
        var harness = RelocationFixtures.Create();
        harness.States.Snapshots[RelocationFixtures.Serial] = RelocationFixtures.Snapshot(
            RelocationFixtures.Serial,
            battery: 85,
            charging: true,
            lastNodeId: "ch1",
            x: 5,
            y: 0);
        harness.States.Snapshots[RelocationFixtures.OtherSerial] = RelocationFixtures.Snapshot(
            RelocationFixtures.OtherSerial,
            battery: 88,
            charging: true,
            lastNodeId: "ch2",
            x: 5,
            y: 20);
        var worker = CreateWorker(harness);

        await worker.TickAsync(Ct);

        Assert.Equal(2, harness.Events.Published.Count);
        Assert.Equal(2, harness.Events.Published.Select(item => item.Payload.EventId).Distinct().Count());
        Assert.Equal(
            [RelocationFixtures.Serial, RelocationFixtures.OtherSerial],
            harness.Events.Published.Select(item => item.Payload.AgvSerialNumber).OrderBy(item => item, StringComparer.Ordinal));
        Assert.All(harness.Events.Published, item =>
        {
            Assert.Equal("agv-charging-completed", item.Topic);
            Assert.Equal("OK", item.Payload.Status);
            Assert.Equal(80, item.Payload.Threshold);
        });

        // Защёлка на робота: повторный тик новых событий не дает.
        await worker.TickAsync(Ct);
        Assert.Equal(2, harness.Events.Published.Count);

        // Автоматический stopCharging по умолчанию выключен.
        Assert.Empty(harness.Publisher.Actions);
    }

    [Fact]
    public async Task TickAsync_ChargeDropsBelowLowerLimit_LatchResetsAndEventRepeats()
    {
        var harness = RelocationFixtures.Create();
        var charging = RelocationFixtures.Snapshot(
            RelocationFixtures.Serial,
            battery: 85,
            charging: true,
            lastNodeId: "ch1",
            x: 5,
            y: 0);
        harness.States.Snapshots[RelocationFixtures.Serial] = charging;
        var worker = CreateWorker(harness);

        await worker.TickAsync(Ct);
        Assert.Single(harness.Events.Published);

        harness.States.Snapshots[RelocationFixtures.Serial] = charging with { BatteryCharge = 10 };
        await worker.TickAsync(Ct);
        Assert.Single(harness.Events.Published);

        harness.States.Snapshots[RelocationFixtures.Serial] = charging with { BatteryCharge = 82 };
        await worker.TickAsync(Ct);
        Assert.Equal(2, harness.Events.Published.Count);
        Assert.NotEqual(
            harness.Events.Published[0].Payload.EventId,
            harness.Events.Published[1].Payload.EventId);
    }

    [Fact]
    public async Task TickAsync_AutoStopChargingEnabled_SendsStopCharging()
    {
        var options = new ChargingOptions { AutoStopCharging = true };
        var harness = RelocationFixtures.Create(charging: options);
        harness.States.Snapshots[RelocationFixtures.Serial] = RelocationFixtures.Snapshot(
            RelocationFixtures.Serial,
            battery: 85,
            charging: true,
            lastNodeId: "ch1",
            x: 5,
            y: 0);

        await CreateWorker(harness, options).TickAsync(Ct);

        var action = Assert.Single(harness.Publisher.Actions);
        Assert.Contains("\"actionType\":\"stopCharging\"", action.Payload);
        Assert.Single(harness.Events.Published);
    }

    [Fact]
    public async Task TickAsync_Parking_AfterWaitingMinutes()
    {
        var harness = RelocationFixtures.Create();
        harness.States.Snapshots[RelocationFixtures.Serial] =
            RelocationFixtures.Snapshot(RelocationFixtures.Serial, battery: 90);
        var worker = CreateWorker(harness);

        await worker.TickAsync(Ct);
        Assert.Empty(harness.Repository.Orders);

        harness.Time.Now = harness.Time.Now.AddMinutes(6);
        await worker.TickAsync(Ct);

        Assert.Equal("pk1", Assert.Single(harness.Repository.Orders).Value.TargetNodeId);
    }

    [Fact]
    public async Task TickAsync_ParkedRobotWithLowBattery_GoesToCharge()
    {
        // Робот уже стоит в парковке и разрядился: зарядка важнее, он должен поехать на станцию.
        var harness = RelocationFixtures.Create();
        harness.States.Snapshots[RelocationFixtures.Serial] = RelocationFixtures.Snapshot(
            RelocationFixtures.Serial, battery: 10, lastNodeId: "pk1", x: 10, y: 0);
        var worker = CreateWorker(harness);

        await worker.TickAsync(Ct);

        Assert.Equal("ch1", Assert.Single(harness.Repository.Orders).Value.TargetNodeId);
    }

    [Fact]
    public async Task TickAsync_ChargedOnStation_GoesToParkingAndFreesCharger()
    {
        // Зарядился на единственной станции: должен уехать в парковку и освободить зарядку.
        var harness = RelocationFixtures.Create(RelocationFixtures.Graph(chargingCount: 1));
        harness.States.Snapshots[RelocationFixtures.Serial] = RelocationFixtures.Snapshot(
            RelocationFixtures.Serial, battery: 95, lastNodeId: "ch1", x: 5, y: 0);
        var worker = CreateWorker(harness);

        await worker.TickAsync(Ct);
        Assert.Empty(harness.Repository.Orders);

        harness.Time.Now = harness.Time.Now.AddMinutes(6);
        await worker.TickAsync(Ct);

        Assert.Equal("pk1", Assert.Single(harness.Repository.Orders).Value.TargetNodeId);
    }

    [Fact]
    public async Task ExecuteAsync_WhenDisabled_DoesNotRelocate()
    {
        var harness = RelocationFixtures.Create(RelocationFixtures.Graph(chargingCount: 1));
        harness.States.Snapshots[RelocationFixtures.Serial] =
            RelocationFixtures.Snapshot(RelocationFixtures.Serial, battery: 10);
        var worker = CreateWorker(harness, new ChargingOptions { Enabled = false });

        await worker.StartAsync(Ct);
        await worker.StopAsync(Ct);

        Assert.Empty(harness.Repository.Orders);
    }

    private static RobotRelocationWorker CreateWorker(
        RelocationFixtures.Harness harness,
        ChargingOptions? options = null) =>
        new(
            harness.States,
            harness.Repository,
            harness.Service,
            new FixedOptions<ChargingOptions>(options ?? harness.Charging),
            harness.Time,
            NullLogger<RobotRelocationWorker>.Instance);
}
