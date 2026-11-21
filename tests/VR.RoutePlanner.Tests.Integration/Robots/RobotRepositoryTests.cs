using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using VR.RoutePlanner.Domain.Robots;
using VR.RoutePlanner.Infrastructure.Persistence;
using VR.RoutePlanner.Tests.Integration.Support;
using Xunit;

namespace VR.RoutePlanner.Tests.Integration.Robots;

/// <summary>
/// Таблицы сессии и заказа на живой базе: миграция применена, строки сохраняются и читаются.
/// </summary>
[Collection(VrtWriteCollection.Name)]
public sealed class RobotRepositoryTests
{
    [SkippableFact]
    public async Task SaveAsync_RobotSession_ReadsBack()
    {
        var factory = await CreateFactoryAsync();
        var repository = new RobotSessionRepository(factory);
        var serial = $"test-{Guid.NewGuid():N}"[..24];
        var now = DateTimeOffset.UtcNow;

        try
        {
            await repository.SaveAsync(
                new RobotSession(serial, "Tuskrobots", "2.0.0", "ONLINE", now, "{\"a\":1}", now),
                CancellationToken.None);

            var stored = await repository.GetAsync(serial, CancellationToken.None);
            Assert.NotNull(stored);
            Assert.Equal("Tuskrobots", stored!.Manufacturer);
            Assert.Equal("2.0.0", stored.ProtocolVersion);
            Assert.Equal("ONLINE", stored.ConnectionState);
            Assert.Equal("{\"a\":1}", stored.StateJson);
            Assert.Equal(now.ToUnixTimeMilliseconds(), stored.LastSeenAt.ToUnixTimeMilliseconds());

            await repository.SaveAsync(
                new RobotSession(serial, "Tuskrobots", "2.1.0", "OFFLINE", now, "{\"b\":2}", now),
                CancellationToken.None);

            var updated = await repository.GetAsync(serial, CancellationToken.None);
            Assert.Equal("2.1.0", updated!.ProtocolVersion);
            Assert.Equal("{\"b\":2}", updated.StateJson);
        }
        finally
        {
            await DeleteSessionAsync(factory, serial);
        }
    }

    [SkippableFact]
    public async Task SaveAsync_OrderWithNodes_ReadsBackByOrderAndByRobot()
    {
        var factory = await CreateFactoryAsync();
        var repository = new RobotOrderRepository(factory);
        var serial = $"test-{Guid.NewGuid():N}"[..24];
        var orderId = $"ORD_TG{Guid.NewGuid():N}"[..12];
        var created = DateTimeOffset.UtcNow;

        try
        {
            var order = new RobotOrder(
                orderId,
                serial,
                0,
                RobotOrderStatuses.Sent,
                "{\"nodes\":[]}",
                3,
                "wa129",
                "pa1",
                created,
                created,
                null,
                null,
                null,
                [
                    new RobotOrderNode(orderId, 0, "wa100", true, false, 0),
                    new RobotOrderNode(orderId, 2, "wa101", true, true, 1),
                ]);

            await repository.SaveAsync(order, CancellationToken.None);

            var byId = await repository.GetAsync(orderId, CancellationToken.None);
            Assert.NotNull(byId);
            Assert.Equal(serial, byId!.AgvSerialNumber);
            Assert.Equal(RobotOrderStatuses.Sent, byId.Status);
            Assert.Equal(3, byId.ReleasedNodeCount);
            Assert.Equal("wa129", byId.TargetNodeId);
            Assert.Equal("pa1", byId.ApproachNodeId);
            Assert.Equal(2, byId.Nodes.Count);
            Assert.Equal(["wa100", "wa101"], byId.Nodes.Select(node => node.NodeId));
            Assert.True(byId.Nodes[1].Reached);
            Assert.Equal(1, byId.Nodes[1].ActionCount);

            await repository.SaveAsync(
                order with
                {
                    Status = RobotOrderStatuses.Completed,
                    CompletedAt = created.AddSeconds(5),
                    Nodes = [new RobotOrderNode(orderId, 0, "wa100", true, true, 0)],
                },
                CancellationToken.None);

            var latest = await repository.GetLatestAsync(serial, CancellationToken.None);
            Assert.NotNull(latest);
            Assert.Equal(orderId, latest!.OrderId);
            Assert.Equal(RobotOrderStatuses.Completed, latest.Status);
            Assert.NotNull(latest.CompletedAt);
            Assert.Single(latest.Nodes);
        }
        finally
        {
            await DeleteOrderAsync(factory, orderId);
        }
    }

    [SkippableFact]
    public async Task NextAsync_AfterInsertedOrder_IncrementsNumber()
    {
        var factory = await CreateFactoryAsync();
        var orders = new RobotOrderRepository(factory);
        var source = new VrtRobotOrderNumberSource(factory, NullLogger<VrtRobotOrderNumberSource>.Instance);
        var serial = $"test-{Guid.NewGuid():N}"[..24];
        var created = DateTimeOffset.UtcNow;

        var first = await source.NextAsync(CancellationToken.None);
        var orderId = $"ORD_{first:D5}";

        try
        {
            await orders.SaveAsync(
                new RobotOrder(
                    orderId,
                    serial,
                    0,
                    RobotOrderStatuses.Draft,
                    null,
                    0,
                    "wa1",
                    null,
                    created,
                    null,
                    null,
                    null,
                    null,
                    Array.Empty<RobotOrderNode>()),
                CancellationToken.None);

            var second = await source.NextAsync(CancellationToken.None);
            Assert.Equal(first + 1, second);
        }
        finally
        {
            await DeleteOrderAsync(factory, orderId);
        }
    }

    private static async Task<TestDbContextFactory> CreateFactoryAsync()
    {
        var connectionString = TestDatabase.RequireConnection();
        var factory = new TestDbContextFactory(TestDatabase.BuildOptions(connectionString));

        await using var context = factory.CreateDbContext();
        await MigrationRunner.MigrateAsync(
            context,
            MigrationRunner.HistoryTable,
            MigrationRunner.LockKey,
            NullLogger.Instance,
            CancellationToken.None);

        return factory;
    }

    private static async Task DeleteSessionAsync(TestDbContextFactory factory, string serial)
    {
        await using var context = factory.CreateDbContext();
        await context.RobotSessions
            .Where(row => row.AgvSerialNumber == serial)
            .ExecuteDeleteAsync();
    }

    private static async Task DeleteOrderAsync(TestDbContextFactory factory, string orderId)
    {
        await using var context = factory.CreateDbContext();
        await context.RobotOrders
            .Where(row => row.OrderId == orderId)
            .ExecuteDeleteAsync();
    }
}
