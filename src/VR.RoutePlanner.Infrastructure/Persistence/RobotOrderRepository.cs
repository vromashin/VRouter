using Microsoft.EntityFrameworkCore;
using VR.RoutePlanner.Application.Robots;
using VR.RoutePlanner.Domain.Robots;
using VR.RoutePlanner.Infrastructure.Persistence;

namespace VR.RoutePlanner.Infrastructure.Persistence;

/// <summary>
/// Заказы роботам в <c>vrt_RobotOrder</c> и <c>vrt_RobotOrderNode</c>; узлы пишутся
/// той же транзакцией, что и сам заказ.
/// </summary>
public sealed class RobotOrderRepository : IRobotOrderRepository
{
    private readonly IDbContextFactory<RoutePlannerDbContext> _contextFactory;

    /// <summary>
    /// Создает репозиторий заказов.
    /// </summary>
    /// <param name="contextFactory">Фабрика контекста базы <c>rms</c>.</param>
    public RobotOrderRepository(IDbContextFactory<RoutePlannerDbContext> contextFactory)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);

        _contextFactory = contextFactory;
    }

    /// <inheritdoc />
    public async Task SaveAsync(RobotOrder order, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentException.ThrowIfNullOrWhiteSpace(order.OrderId);
        ArgumentException.ThrowIfNullOrWhiteSpace(order.AgvSerialNumber);

        await using var context = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await context.RobotOrders
            .SingleOrDefaultAsync(item => item.OrderId == order.OrderId, ct)
            .ConfigureAwait(false);

        if (row is null)
        {
            row = new RobotOrderRecord { OrderId = order.OrderId };
            context.RobotOrders.Add(row);
        }

        row.AgvSerialNumber = order.AgvSerialNumber;
        row.OrderUpdateId = order.OrderUpdateId;
        row.Status = order.Status;
        row.PlanJson = order.PlanJson;
        row.ReleasedNodeCount = order.ReleasedNodeCount;
        row.TargetNodeId = order.TargetNodeId;
        row.ApproachNodeId = order.ApproachNodeId;
        row.CreatedAt = order.CreatedAt;
        row.SentAt = order.SentAt;
        row.AcceptedAt = order.AcceptedAt;
        row.CompletedAt = order.CompletedAt;
        row.FailureReason = order.FailureReason;

        var existingNodes = await context.RobotOrderNodes
            .Where(node => node.OrderId == order.OrderId)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        context.RobotOrderNodes.RemoveRange(existingNodes);

        foreach (var node in order.Nodes)
        {
            context.RobotOrderNodes.Add(new RobotOrderNodeRecord
            {
                OrderId = order.OrderId,
                SequenceId = node.SequenceId,
                NodeId = node.NodeId,
                Released = node.Released,
                Reached = node.Reached,
                ActionCount = node.ActionCount,
            });
        }

        await context.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<RobotOrder?> GetAsync(string orderId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(orderId);

        await using var context = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await ReadAsync(context, item => item.OrderId == orderId, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<RobotOrder?> GetLatestAsync(string agvSerialNumber, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agvSerialNumber);

        await using var context = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var orderId = await context.RobotOrders
            .AsNoTracking()
            .Where(item => item.AgvSerialNumber == agvSerialNumber)
            .OrderByDescending(item => item.CreatedAt)
            .Select(item => item.OrderId)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        return orderId is null
            ? null
            : await ReadAsync(context, item => item.OrderId == orderId, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RobotOrder>> GetActiveAsync(CancellationToken ct)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var rows = await context.RobotOrders
            .AsNoTracking()
            .Where(row => row.Status == "Draft" || row.Status == "Sent" || row.Status == "Accepted" || row.Status == "Executing")
            .OrderBy(row => row.CreatedAt)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return rows
            .Select(row => new RobotOrder(
                row.OrderId,
                row.AgvSerialNumber,
                row.OrderUpdateId,
                row.Status,
                row.PlanJson,
                row.ReleasedNodeCount,
                row.TargetNodeId,
                row.ApproachNodeId,
                row.CreatedAt,
                row.SentAt,
                row.AcceptedAt,
                row.CompletedAt,
                row.FailureReason,
                Array.Empty<RobotOrderNode>()))
            .ToList();
    }

    /// <inheritdoc />
    public async Task<RobotOrder?> GetActiveForRobotAsync(string agvSerialNumber, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agvSerialNumber);

        await using var context = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var orderId = await context.RobotOrders
            .AsNoTracking()
            .Where(item => item.AgvSerialNumber == agvSerialNumber
                && (item.Status == "Draft"
                    || item.Status == "Sent"
                    || item.Status == "Accepted"
                    || item.Status == "Executing"))
            .OrderByDescending(item => item.CreatedAt)
            .Select(item => item.OrderId)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        return orderId is null
            ? null
            : await ReadAsync(context, item => item.OrderId == orderId, ct).ConfigureAwait(false);
    }

    private static async Task<RobotOrder?> ReadAsync(
        RoutePlannerDbContext context,
        System.Linq.Expressions.Expression<Func<RobotOrderRecord, bool>> filter,
        CancellationToken ct)
    {
        var row = await context.RobotOrders
            .AsNoTracking()
            .SingleOrDefaultAsync(filter, ct)
            .ConfigureAwait(false);

        if (row is null)
        {
            return null;
        }

        var nodes = await context.RobotOrderNodes
            .AsNoTracking()
            .Where(node => node.OrderId == row.OrderId)
            .OrderBy(node => node.SequenceId)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return new RobotOrder(
            row.OrderId,
            row.AgvSerialNumber,
            row.OrderUpdateId,
            row.Status,
            row.PlanJson,
            row.ReleasedNodeCount,
            row.TargetNodeId,
            row.ApproachNodeId,
            row.CreatedAt,
            row.SentAt,
            row.AcceptedAt,
            row.CompletedAt,
            row.FailureReason,
            nodes
                .Select(node => new RobotOrderNode(
                    node.OrderId,
                    node.SequenceId,
                    node.NodeId,
                    node.Released,
                    node.Reached,
                    node.ActionCount))
                .ToList());
    }
}
