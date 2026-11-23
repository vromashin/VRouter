using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VR.RoutePlanner.Application.Configuration;
using VR.RoutePlanner.Application.Robots;
using VR.RoutePlanner.Domain.Robots;
using VR.RoutePlanner.Infrastructure.Mqtt;
using VR.RoutePlanner.Infrastructure.Persistence;
using VR.RoutePlanner.Tests.Unit.Planning;
using Xunit;

namespace VR.RoutePlanner.Tests.Unit.Robots;

/// <summary>
/// Состояние робота через базу: запись из снимка, чтение при пустой памяти, свежесть
/// и создание заказа по узлу из <c>vrt_RobotSession</c>.
/// </summary>
public sealed class RobotSessionStateTests
{
    private const string StateJson = """
        {"version":"2.0.0","manufacturer":"Tuskrobots","serialNumber":"98001","orderId":"","driving":false,
         "lastNodeId":"wa2","agvPosition":{"x":12.5,"y":34.25,"theta":0.5,"mapId":"801"},
         "batteryState":{"batteryCharge":77.5},"errors":[],"nodeStates":[]}
        """;

    [Fact]
    public async Task SaveAsync_Snapshot_WritesSessionRow()
    {
        var sessions = new FakeSessionRepository();
        var time = new TestTimeProvider(DateTimeOffset.UnixEpoch.AddHours(1));
        var service = new RobotSessionService(sessions, time, NullLogger<RobotSessionService>.Instance);
        var snapshot = OrderTestData.Snapshot(null, receivedAt: time.Now);

        var saved = await service.SaveAsync(snapshot, StateJson, CancellationToken.None);

        Assert.True(saved);
        var row = Assert.Single(sessions.Sessions.Values);
        Assert.Equal(OrderTestData.Serial, row.AgvSerialNumber);
        Assert.Equal("Tuskrobots", row.Manufacturer);
        Assert.Equal("ONLINE", row.ConnectionState);
        Assert.Equal(StateJson, row.StateJson);
        Assert.Equal(time.Now, row.LastSeenAt);
    }

    [Fact]
    public async Task SaveAsync_TwiceWithinSecond_IsThrottled()
    {
        var sessions = new FakeSessionRepository();
        var time = new TestTimeProvider(DateTimeOffset.UnixEpoch);
        var service = new RobotSessionService(sessions, time, NullLogger<RobotSessionService>.Instance);
        var snapshot = OrderTestData.Snapshot(null, receivedAt: time.Now);

        Assert.True(await service.SaveAsync(snapshot, StateJson, CancellationToken.None));
        Assert.False(await service.SaveAsync(snapshot, StateJson, CancellationToken.None));

        time.Now = time.Now.AddSeconds(2);
        Assert.True(await service.SaveAsync(snapshot with { ReceivedAt = time.Now }, StateJson + " ", CancellationToken.None));
    }

    [Fact]
    public async Task GetLatest_EmptyMemory_FallsBackToDatabase()
    {
        var sessions = new FakeSessionRepository();
        var time = new TestTimeProvider(DateTimeOffset.UnixEpoch.AddHours(2));
        sessions.Sessions[OrderTestData.Serial] = Session(time.Now.AddSeconds(-1));
        var reader = CreateReader(sessions, time);

        var snapshot = await reader.GetLatestAsync(OrderTestData.Serial, CancellationToken.None);

        Assert.NotNull(snapshot);
        Assert.Equal("wa2", snapshot!.LastNodeId);
        Assert.Equal(12.5, snapshot.PositionX, 6);
        Assert.Equal(34.25, snapshot.PositionY, 6);
        Assert.Equal(77.5, snapshot.BatteryCharge, 6);
        Assert.Equal("801", snapshot.MapId);
    }

    [Fact]
    public async Task GetLatest_StaleDatabaseRow_IsNotReturned()
    {
        var sessions = new FakeSessionRepository();
        var time = new TestTimeProvider(DateTimeOffset.UnixEpoch.AddHours(2));
        sessions.Sessions[OrderTestData.Serial] = Session(time.Now.AddSeconds(-30));
        var reader = CreateReader(sessions, time);

        Assert.Null(await reader.GetLatestAsync(OrderTestData.Serial, CancellationToken.None));
    }

    [Fact]
    public async Task GetLatest_MemoryWins_OverDatabase()
    {
        var sessions = new FakeSessionRepository();
        var time = new TestTimeProvider(DateTimeOffset.UnixEpoch.AddHours(2));
        sessions.Sessions[OrderTestData.Serial] = Session(time.Now.AddSeconds(-1));
        var memory = new MqttRobotStateReader(
            Options.Create(new MqttOptions { Enabled = false }),
            NullLogger<MqttRobotStateReader>.Instance);
        memory.ProcessPayload(
            "APR/v2/Tuskrobots/98001/state",
            StateJson.Replace("wa2", "wa4", StringComparison.Ordinal));
        var reader = CreateReader(sessions, time, memory);

        var snapshot = await reader.GetLatestAsync(OrderTestData.Serial, CancellationToken.None);

        Assert.Equal("wa4", snapshot!.LastNodeId);
    }

    [Fact]
    public async Task CreateOrder_StartNodeFromDatabase_BuildsOrder()
    {
        var sessions = new FakeSessionRepository();
        var time = new TestTimeProvider(DateTimeOffset.UnixEpoch.AddHours(2));
        sessions.Sessions[OrderTestData.Serial] = Session(time.Now.AddSeconds(-1));
        var reader = CreateReader(sessions, time);

        var nodes = Enumerable.Range(0, 5)
            .Select(index => PlanningFixtures.Node(Guid.NewGuid(), $"wa{index}", index, index))
            .ToList();
        var orders = new FakeOrderRepository();
        var publisher = new FakePublisher();
        var service = new RobotOrderService(
            new FakePlanner(3),
            new FakeComposer(),
            new HeaderIds(),
            new FakeGraphRepository(PlanningFixtures.Graph(nodes, [])),
            new FakeNumberSource(),
            publisher,
            orders,
            reader,
            new FixedOptions<RoutePlannerOptions>(PlanningFixtures.Options()),
            time,
            NullLogger<RobotOrderService>.Instance,
            new FakeTrafficCoordinator());

        var order = await service.CreateOrderAsync("801", OrderTestData.Serial, "wa4", false, CancellationToken.None);

        // Если робот уже на узле подъезда, заказ сразу завершается без движения.
        if (order.Status == RobotOrderStatuses.Completed)
        {
            Assert.Empty(publisher.Orders);
            Assert.Empty(order.Nodes);
        }
        else
        {
            Assert.Equal(RobotOrderStatuses.Sent, order.Status);
            Assert.Single(publisher.Orders);
            Assert.Equal(3, order.Nodes.Count);
        }
    }

    private static DatabaseRobotStateReader CreateReader(
        FakeSessionRepository sessions,
        TestTimeProvider time,
        MqttRobotStateReader? memory = null)
    {
        memory ??= new MqttRobotStateReader(
            Options.Create(new MqttOptions { Enabled = false }),
            NullLogger<MqttRobotStateReader>.Instance);

        return new DatabaseRobotStateReader(
            memory,
            memory,
            sessions,
            new FixedOptions<AgvMonitoringOptions>(new AgvMonitoringOptions()),
            time,
            NullLogger<DatabaseRobotStateReader>.Instance);
    }

    private static RobotSession Session(DateTimeOffset seenAt) => new(
        OrderTestData.Serial,
        "Tuskrobots",
        "2.0.0",
        "ONLINE",
        seenAt,
        StateJson,
        seenAt);

    private sealed class FakeSessionRepository : IRobotSessionRepository
    {
        public Dictionary<string, RobotSession> Sessions { get; } = new(StringComparer.Ordinal);

        public Task SaveAsync(RobotSession session, CancellationToken ct)
        {
            Sessions[session.AgvSerialNumber] = session;
            return Task.CompletedTask;
        }

        public Task<RobotSession?> GetAsync(string agvSerialNumber, CancellationToken ct) =>
            Task.FromResult(Sessions.GetValueOrDefault(agvSerialNumber));
    }
}
