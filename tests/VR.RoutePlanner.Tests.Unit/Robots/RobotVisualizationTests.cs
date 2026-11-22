using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VR.RoutePlanner.Application.Configuration;
using VR.RoutePlanner.Application.Legacy;
using VR.RoutePlanner.Application.Robots;
using VR.RoutePlanner.Domain.Robots;
using VR.RoutePlanner.Infrastructure.Mqtt;
using VR.RoutePlanner.Infrastructure.Persistence;
using VR.RoutePlanner.Tests.Unit.Planning;
using Xunit;

namespace VR.RoutePlanner.Tests.Unit.Robots;

/// <summary>
/// Живая позиция из <c>visualization</c>: разбор образца, троттлинг записи в <c>Agvs</c>,
/// приоритет визуализации над <c>state</c> и выключенный флаг записи.
/// </summary>
public sealed class RobotVisualizationTests
{
    private const string Sample = """
        {"agvPosition":{"deviationRange":0,"localizationScore":0,"mapDescription":"","mapId":"801","positionInitialized":false,"theta":2.5557217597961426,"x":12.640478134155273,"y":6.689114093780518},"batteryState":{"batteryCharge":98.83719635009766,"batteryHealth":100,"batteryVoltage":56855.8046875,"charging":false,"reach":0},"driving":true,"fork":{"forkPos":0,"liftPos":-0.05144999921321869},"headerId":1720,"loads":[],"manufacturer":"Tuskrobots","operatingMode":"AUTOMATIC","paused":false,"safetyState":{"eStop":"NONE","fieldViolation":false},"serialNumber":"98001","timestamp":"2026-11-22T11:44:47.195Z","velocity":{"omega":-0.5635315179824829,"vx":3.898255727108335e-07,"vy":-2.586850769148441e-07},"version":"2.1.0"}
        """;

    [Fact]
    public void TryParse_SimulatorSample_ReadsPositionAndTelemetry()
    {
        Assert.True(RobotVisualizationParser.TryParse(Sample, out var visualization, out var error), error);
        Assert.NotNull(visualization);
        Assert.Equal("98001", visualization!.SerialNumber);
        Assert.Equal("Tuskrobots", visualization.Manufacturer);
        Assert.Equal("2.1.0", visualization.Version);
        Assert.Equal(12.640478134155273, visualization.PositionX, 9);
        Assert.Equal(6.689114093780518, visualization.PositionY, 9);
        Assert.Equal(2.5557217597961426, visualization.Theta, 9);
        Assert.Equal("801", visualization.MapId);
        Assert.Equal(98.83719635009766, visualization.BatteryCharge, 6);
        Assert.True(visualization.Driving);
        Assert.False(visualization.Paused);
        Assert.Equal("NONE", visualization.EStop);
        Assert.False(visualization.FieldViolation);
        Assert.Equal(-0.05144999921321869, visualization.LiftHeight!.Value, 9);
        Assert.True(visualization.Velocity >= 0);
    }

    [Fact]
    public void TryParse_BrokenPayload_ReturnsFalse()
    {
        Assert.False(RobotVisualizationParser.TryParse("{ \"agvPosition\": ", out var visualization, out var error));
        Assert.Null(visualization);
        Assert.NotNull(error);

        Assert.False(RobotVisualizationParser.TryParse("{\"version\":\"2.1.0\"}", out _, out var noSerial));
        Assert.Contains("serialNumber", noSerial!);
    }

    [Fact]
    public async Task Writer_TwoPerSecond_DoesNotWriteMoreOften()
    {
        var target = new RecordingVisualizationWriter();
        var time = new TestTimeProvider(DateTimeOffset.UnixEpoch);
        var writer = new RobotVisualizationWriter(
            target,
            new FixedOptions<LegacyOptions>(new LegacyOptions { WriteAgvsEnabled = true }),
            time,
            NullLogger<RobotVisualizationWriter>.Instance);
        var visualization = Visualization(time.Now);

        Assert.True(await writer.WriteAsync(visualization, CancellationToken.None));
        time.Now = time.Now.AddMilliseconds(200);
        Assert.False(await writer.WriteAsync(visualization with { ReceivedAt = time.Now }, CancellationToken.None));
        time.Now = time.Now.AddMilliseconds(400);
        Assert.True(await writer.WriteAsync(visualization with { ReceivedAt = time.Now }, CancellationToken.None));
        time.Now = time.Now.AddMilliseconds(500);
        Assert.True(await writer.WriteAsync(visualization with { ReceivedAt = time.Now }, CancellationToken.None));

        Assert.Equal(3, target.Writes.Count);
    }

    [Fact]
    public async Task Writer_FlagOff_DoesNotWrite()
    {
        var target = new RecordingVisualizationWriter();
        var writer = new RobotVisualizationWriter(
            target,
            new FixedOptions<LegacyOptions>(new LegacyOptions { WriteAgvsEnabled = false }),
            new TestTimeProvider(DateTimeOffset.UnixEpoch),
            NullLogger<RobotVisualizationWriter>.Instance);

        Assert.False(await writer.WriteAsync(Visualization(DateTimeOffset.UnixEpoch), CancellationToken.None));
        Assert.Empty(target.Writes);
    }

    [Fact]
    public async Task Reader_VisualizationIsFresher_OverridesStatePosition()
    {
        var memory = new MqttRobotStateReader(
            Options.Create(new MqttOptions { Enabled = false }),
            NullLogger<MqttRobotStateReader>.Instance);
        var time = new TestTimeProvider(DateTimeOffset.UnixEpoch.AddHours(3));

        Assert.True(memory.ProcessPayload(
            "APR/v2/Tuskrobots/98001/state",
            """{"version":"2.0.0","serialNumber":"98001","orderId":"ORD_04331","driving":false,"lastNodeId":"wa129","agvPosition":{"x":1,"y":2,"theta":0,"mapId":"801"},"batteryState":{"batteryCharge":50},"errors":[],"nodeStates":[]}"""));
        Assert.True(memory.ProcessVisualizationPayload("APR/v2/Tuskrobots/98001/visualization", Sample));

        var reader = new DatabaseRobotStateReader(
            memory,
            memory,
            new FakeSessionRepository(),
            new FixedOptions<AgvMonitoringOptions>(new AgvMonitoringOptions()),
            time,
            NullLogger<DatabaseRobotStateReader>.Instance);

        var snapshot = await reader.GetLatestAsync("98001", CancellationToken.None);

        Assert.NotNull(snapshot);
        Assert.Equal(12.640478134155273, snapshot!.PositionX, 9);
        Assert.Equal(6.689114093780518, snapshot.PositionY, 9);
        Assert.Equal(2.5557217597961426, snapshot.Theta, 9);
        Assert.Equal(98.83719635009766, snapshot.BatteryCharge, 6);
        Assert.Equal("ORD_04331", snapshot.OrderId);
        Assert.Equal("wa129", snapshot.LastNodeId);
    }

    private static RobotVisualization Visualization(DateTimeOffset receivedAt) => new(
        "98001",
        "Tuskrobots",
        "2.1.0",
        12.5,
        6.5,
        0.5,
        "801",
        99,
        true,
        false,
        "NONE",
        false,
        0,
        1,
        receivedAt);

    private sealed class RecordingVisualizationWriter : ILegacyAgvVisualizationWriter
    {
        public List<RobotVisualization> Writes { get; } = new();

        public Task ApplyVisualizationAsync(RobotVisualization visualization, CancellationToken ct)
        {
            Writes.Add(visualization);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeSessionRepository : IRobotSessionRepository
    {
        public Task SaveAsync(RobotSession session, CancellationToken ct) => Task.CompletedTask;

        public Task<RobotSession?> GetAsync(string agvSerialNumber, CancellationToken ct) =>
            Task.FromResult<RobotSession?>(null);
    }
}
