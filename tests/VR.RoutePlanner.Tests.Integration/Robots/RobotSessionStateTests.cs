using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VR.RoutePlanner.Application.Configuration;
using VR.RoutePlanner.Application.Robots;
using VR.RoutePlanner.Domain.Robots;
using VR.RoutePlanner.Infrastructure.Mqtt;
using VR.RoutePlanner.Infrastructure.Persistence;
using VR.RoutePlanner.Tests.Integration.Support;
using Xunit;

namespace VR.RoutePlanner.Tests.Integration.Robots;

/// <summary>
/// Состояние робота через <c>vrt_RobotSession</c> на живой базе: процесс записи и процесс
/// чтения разные, память читателя пуста.
/// </summary>
[Collection(VrtWriteCollection.Name)]
public sealed class RobotSessionStateTests
{
    private const string StateJson = """
        {"version":"2.0.0","manufacturer":"Tuskrobots","serialNumber":"{0}","orderId":"","driving":false,
         "lastNodeId":"wa129","agvPosition":{"x":5.639951705932617,"y":24.485902786254883,"theta":-1.57,"mapId":"801"},
         "batteryState":{"batteryCharge":51.5},"errors":[],"nodeStates":[]}
        """;

    [SkippableFact]
    public async Task Session_WrittenByOneProcess_IsReadByAnother()
    {
        var connectionString = TestDatabase.RequireConnection();
        var factory = new TestDbContextFactory(TestDatabase.BuildOptions(connectionString));
        var serial = $"test-{Guid.NewGuid():N}"[..24];
        var repository = new RobotSessionRepository(factory);
        var time = TimeProvider.System;
        var writer = new RobotSessionService(repository, time, NullLogger<RobotSessionService>.Instance);
        var payload = StateJson.Replace("{0}", serial, StringComparison.Ordinal);

        try
        {
            var snapshot = new RobotSnapshot(
                serial,
                "ONLINE",
                false,
                null,
                0,
                5.64,
                24.49,
                -1.57,
                "801",
                51.5,
                Array.Empty<string>(),
                Array.Empty<RobotNodeState>(),
                time.GetUtcNow(),
                "2.0.0",
                "wa129");

            Assert.True(await writer.SaveAsync(snapshot, payload, CancellationToken.None));

            var memory = new MqttRobotStateReader(
                Options.Create(new MqttOptions { Enabled = false }),
                NullLogger<MqttRobotStateReader>.Instance);
            var reader = new DatabaseRobotStateReader(
                memory,
                memory,
                repository,
                Options.Create(new AgvMonitoringOptions { StaleStateSeconds = 5 }),
                time,
                NullLogger<DatabaseRobotStateReader>.Instance);

            var stored = await reader.GetLatestAsync(serial, CancellationToken.None);

            Assert.NotNull(stored);
            Assert.Equal("wa129", stored!.LastNodeId);
            Assert.Equal("801", stored.MapId);
            Assert.Equal(51.5, stored.BatteryCharge, 3);
            Assert.Equal(-1.57, stored.Theta, 3);
        }
        finally
        {
            await using var context = factory.CreateDbContext();
            await context.RobotSessions
                .Where(row => row.AgvSerialNumber == serial)
                .ExecuteDeleteAsync();
        }
    }
}
