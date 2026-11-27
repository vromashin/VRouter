using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VR.RoutePlanner.Application.Robots;
using VR.RoutePlanner.Infrastructure.Mqtt;
using VR.RoutePlanner.Tests.Unit.Support;
using Xunit;

namespace VR.RoutePlanner.Tests.Unit.Robots;

/// <summary>
/// Разбор сообщений VDA 5050 <c>state</c>: образцы с робота, версия 2.1.0 и битый JSON.
/// </summary>
public sealed class RobotStateParserTests
{
    [Fact]
    public void TryParse_RobotSample_ReadsPositionAndOrder()
    {
        var payload = ReadAsset("mqtt-state-robot.json");

        Assert.True(RobotStateParser.TryParse(payload, out var snapshot, out var error), error);
        Assert.NotNull(snapshot);
        Assert.Equal("98001", snapshot!.SerialNumber);
        Assert.Equal("2.0.0", snapshot.Version);
        Assert.Equal("ONLINE", snapshot.ConnectionState);
        Assert.Equal("ORD_04329", snapshot.OrderId);
        Assert.Equal(0, snapshot.OrderUpdateId);
        Assert.False(snapshot.Driving);
        Assert.Equal(14.232115745544434, snapshot.PositionX, 9);
        Assert.Equal(35.35910415649414, snapshot.PositionY, 9);
        Assert.Equal(0.009327671490609646, snapshot.Theta, 12);
        Assert.Equal("801", snapshot.MapId);
        Assert.Equal(53.21471405029297, snapshot.BatteryCharge, 6);
        Assert.Empty(snapshot.Errors);
        Assert.Empty(snapshot.NodeStates);
        Assert.True(snapshot.ReceivedAt > DateTimeOffset.UtcNow.AddMinutes(-5));
    }

    [Fact]
    public void TryParse_SecondSample_ReadsDifferentPose()
    {
        var payload = ReadAsset("vda-state-after.json");

        Assert.True(RobotStateParser.TryParse(payload, out var snapshot, out var error), error);
        Assert.NotNull(snapshot);
        Assert.Equal("98001", snapshot!.SerialNumber);
        Assert.Equal("ORD_04330", snapshot.OrderId);
        Assert.Equal(5.639951705932617, snapshot.PositionX, 9);
        Assert.Equal(24.485902786254883, snapshot.PositionY, 9);
        Assert.Equal(-1.570400357246399, snapshot.Theta, 12);
        Assert.Equal(51.55823516845703, snapshot.BatteryCharge, 6);
    }

    [Fact]
    public void TryParse_Version210_SameFields()
    {
        const string payload = """
            {
              "headerId": 7,
              "timestamp": "2026-11-21T21:20:00.000Z",
              "version": "2.1.0",
              "manufacturer": "Tuskrobots",
              "serialNumber": "98002",
              "orderId": "ORD_05000",
              "orderUpdateId": 3,
              "driving": true,
              "agvPosition": { "x": 1.5, "y": 2.5, "theta": 0.5, "mapId": "801" },
              "batteryState": { "batteryCharge": 42.5 },
              "errors": [ { "errorType": "obstacle", "errorDescription": "препятствие впереди" } ],
              "nodeStates": [ { "nodeId": "wa1", "sequenceId": 1, "released": true } ]
            }
            """;

        Assert.True(RobotStateParser.TryParse(payload, out var snapshot, out var error), error);
        Assert.NotNull(snapshot);
        Assert.Equal("2.1.0", snapshot!.Version);
        Assert.Equal("98002", snapshot.SerialNumber);
        Assert.True(snapshot.Driving);
        Assert.Equal(3, snapshot.OrderUpdateId);
        Assert.Equal(1.5, snapshot.PositionX, 9);
        Assert.Equal(42.5, snapshot.BatteryCharge, 9);
        Assert.Equal("препятствие впереди", Assert.Single(snapshot.Errors));
        var node = Assert.Single(snapshot.NodeStates);
        Assert.Equal("wa1", node.NodeId);
        Assert.True(node.Released);
    }

    [Fact]
    public void TryParse_EdgeStates_ReadsReleasedEdges()
    {
        const string payload = """
            {
              "headerId": 7,
              "timestamp": "2026-11-21T21:20:00.000Z",
              "version": "2.1.0",
              "manufacturer": "Tuskrobots",
              "serialNumber": "98002",
              "orderId": "ORD_05000",
              "orderUpdateId": 3,
              "driving": true,
              "agvPosition": { "x": 1.5, "y": 2.5, "theta": 0.5, "mapId": "801" },
              "batteryState": { "batteryCharge": 42.5 },
              "errors": [],
              "nodeStates": [ { "nodeId": "wa1", "sequenceId": 1, "released": true } ],
              "edgeStates": [
                { "edgeId": "801-wa0-wa1", "sequenceId": 0, "released": true },
                { "edgeId": "801-wa1-wa2", "sequenceId": 2, "released": false }
              ]
            }
            """;

        Assert.True(RobotStateParser.TryParse(payload, out var snapshot, out var error), error);
        Assert.NotNull(snapshot);

        var edges = snapshot!.EdgeStates;
        Assert.NotNull(edges);
        Assert.Equal(2, edges!.Count);
        Assert.Equal("801-wa0-wa1", edges[0].EdgeId);
        Assert.Equal(0, edges[0].SequenceId);
        Assert.True(edges[0].Released);
        Assert.False(edges[1].Released);
    }

    [Fact]
    public void TryParse_NoEdgeStates_EmptyList()
    {
        var payload = ReadAsset("vda-state-after.json");

        Assert.True(RobotStateParser.TryParse(payload, out var snapshot, out var error), error);
        Assert.NotNull(snapshot!.EdgeStates);
        Assert.Empty(snapshot.EdgeStates!);
    }

    [Fact]
    public void TryParse_ChargingTrue_SetsChargingFlag()
    {
        const string payload = """
            {
              "headerId": 9,
              "timestamp": "2026-11-27T10:00:00.000Z",
              "version": "2.1.0",
              "manufacturer": "Tuskrobots",
              "serialNumber": "98001",
              "driving": false,
              "agvPosition": { "x": 14.61, "y": 5.19, "theta": 3.14, "mapId": "801" },
              "batteryState": { "batteryCharge": 42.5, "charging": true },
              "errors": []
            }
            """;

        Assert.True(RobotStateParser.TryParse(payload, out var snapshot, out var error), error);
        Assert.NotNull(snapshot);
        Assert.True(snapshot!.Charging);
    }

    [Fact]
    public void TryParse_ChargingAbsent_ChargingFalse()
    {
        var payload = ReadAsset("vda-state-after.json");

        Assert.True(RobotStateParser.TryParse(payload, out var snapshot, out var error), error);
        Assert.NotNull(snapshot);
        Assert.False(snapshot!.Charging);
    }

    [Theory]
    [InlineData("{ \"version\": \"2.0.0\", ", "битый JSON")]
    [InlineData("не json вовсе", "битый JSON")]
    [InlineData("", "пустое")]
    [InlineData("{ \"version\": \"1.0.0\", \"serialNumber\": \"98001\" }", "версия")]
    [InlineData("{ \"version\": \"2.0.0\" }", "serialNumber")]
    public void TryParse_BrokenPayload_ReturnsNullWithReason(string payload, string expected)
    {
        Assert.False(RobotStateParser.TryParse(payload, out var snapshot, out var error));
        Assert.Null(snapshot);
        Assert.NotNull(error);
        Assert.Contains(expected, error, StringComparison.OrdinalIgnoreCase);
    }

    private static string ReadAsset(string fileName) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Robots", "Assets", fileName));
}

/// <summary>
/// Клиент MQTT без брокера: подписка и отказ разбора не бросают исключения.
/// </summary>
public sealed class MqttRobotStateReaderTests
{
    [Fact]
    public async Task GetLatest_BeforeMessages_IsNull()
    {
        var reader = new MqttRobotStateReader(
            Options.Create(new MqttOptions { Enabled = false }),
            NullLogger<MqttRobotStateReader>.Instance);

        Assert.Null(await reader.GetLatestAsync("98001", CancellationToken.None));
        Assert.Empty(await reader.GetAllAsync(CancellationToken.None));
        Assert.Equal("APR/v2/+/+/state", MqttRobotStateReader.StateTopicFilter);
    }

    [Fact]
    public async Task ProcessPayload_BrokenJson_DoesNotThrowAndLogsWarning()
    {
        var logger = new RecordingLogger<MqttRobotStateReader>();
        var reader = new MqttRobotStateReader(Options.Create(new MqttOptions { Enabled = false }), logger);

        var stored = reader.ProcessPayload("APR/v2/Tuskrobots/98001/state", "{ \"version\": \"2.0.0\", ");

        Assert.False(stored);
        Assert.Empty(await reader.GetAllAsync(CancellationToken.None));
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Warning && entry.Message.Contains("не разобрано"));
    }

    [Fact]
    public async Task ProcessPayload_ValidSample_StoresSnapshot()
    {
        var reader = new MqttRobotStateReader(
            Options.Create(new MqttOptions { Enabled = false }),
            NullLogger<MqttRobotStateReader>.Instance);
        var payload = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Robots", "Assets", "vda-state-after.json"));

        var stored = reader.ProcessPayload("APR/v2/Tuskrobots/98001/state", payload);

        Assert.True(stored);
        var snapshot = await reader.GetLatestAsync("98001", CancellationToken.None);
        Assert.NotNull(snapshot);
        Assert.Equal("ORD_04330", snapshot!.OrderId);
        Assert.Single(await reader.GetAllAsync(CancellationToken.None));
    }

    [Fact]
    public void Options_DisabledWithoutHost_PassesValidation()
    {
        var options = new MqttOptions { Enabled = false, Host = string.Empty };

        Assert.Null(options.Validate());
        Assert.IsType<ValidateOptionsResult>(new MqttOptionsValidator().Validate(null, options));
    }
}
