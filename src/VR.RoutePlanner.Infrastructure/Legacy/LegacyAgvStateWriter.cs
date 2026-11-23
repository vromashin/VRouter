using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using VR.RoutePlanner.Application.Robots;

namespace VR.RoutePlanner.Infrastructure.Legacy;

/// <summary>
/// Пишет в <c>public."Agvs"</c> только то, что относится к заказу и узлу: позицию и телеметрию
/// пишет <see cref="LegacyAgvVisualizationWriter"/>, чтобы карта не получала скачков от двух источников.
/// </summary>
public sealed class LegacyAgvStateWriter(
    IDbContextFactory<LegacyReadDbContext> contextFactory,
    ILogger<LegacyAgvStateWriter> logger) : ILegacyAgvStateWriter
{
    public async Task ApplyAsync(RobotSnapshot snapshot, string? orderId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        await using var context = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await context.Agvs
            .FirstOrDefaultAsync(record => record.SerialNumber == snapshot.SerialNumber, ct)
            .ConfigureAwait(false);

        if (row is null)
        {
            logger.LogDebug("Робот {Serial} отсутствует в Agvs, запись пропущена.", snapshot.SerialNumber);
            return;
        }

        // Позицию, курс, батарею и LastUpdateTime пишет только писатель visualization.
        row.LastNodeId = snapshot.LastNodeId ?? row.LastNodeId;
        row.CurrentOrderId = orderId;
        if (string.IsNullOrWhiteSpace(row.MapId) && !string.IsNullOrWhiteSpace(snapshot.MapId))
        {
            row.MapId = snapshot.MapId;
        }

        await context.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
