using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using VR.RoutePlanner.Application.Robots;

namespace VR.RoutePlanner.Infrastructure.Legacy;

public sealed class LegacyAgvStateWriter(
    LegacyReadDbContext context,
    ILogger<LegacyAgvStateWriter> logger) : ILegacyAgvStateWriter
{
    public async Task ApplyAsync(RobotSnapshot snapshot, string? orderId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var row = await context.Agvs
            .FirstOrDefaultAsync(record => record.SerialNumber == snapshot.SerialNumber, ct)
            .ConfigureAwait(false);

        if (row is null)
        {
            logger.LogDebug("Робот {Serial} отсутствует в Agvs, запись пропущена.", snapshot.SerialNumber);
            return;
        }

        row.PositionX = snapshot.PositionX;
        row.PositionY = snapshot.PositionY;
        row.Theta = snapshot.Theta;
        row.Driving = snapshot.Driving;
        row.BatteryCharge = snapshot.BatteryCharge;
        row.ConnectionState = snapshot.ConnectionState;
        row.CurrentState = snapshot.Driving ? "WORKING" : "IDLE";
        row.LastNodeId = snapshot.LastNodeId;
        row.CurrentOrderId = orderId;
        row.LastUpdateTime = snapshot.ReceivedAt.UtcDateTime;
        row.UpdatedDate = DateTime.UtcNow;
        if (!string.IsNullOrWhiteSpace(snapshot.MapId))
        {
            row.MapId = snapshot.MapId;
        }

        await context.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
