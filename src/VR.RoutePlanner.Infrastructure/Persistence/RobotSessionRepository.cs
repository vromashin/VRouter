using Microsoft.EntityFrameworkCore;
using VR.RoutePlanner.Application.Robots;
using VR.RoutePlanner.Domain.Robots;
using VR.RoutePlanner.Infrastructure.Persistence;

namespace VR.RoutePlanner.Infrastructure.Persistence;

/// <summary>
/// Состояние роботов в <c>vrt_RobotSession</c>: одна строка на серийный номер.
/// </summary>
public sealed class RobotSessionRepository : IRobotSessionRepository
{
    private readonly IDbContextFactory<RoutePlannerDbContext> _contextFactory;

    /// <summary>
    /// Создает репозиторий сессий.
    /// </summary>
    /// <param name="contextFactory">Фабрика контекста базы <c>rms</c>.</param>
    public RobotSessionRepository(IDbContextFactory<RoutePlannerDbContext> contextFactory)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);

        _contextFactory = contextFactory;
    }

    /// <inheritdoc />
    public async Task SaveAsync(RobotSession session, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(session.AgvSerialNumber);

        await using var context = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await context.RobotSessions
            .SingleOrDefaultAsync(item => item.AgvSerialNumber == session.AgvSerialNumber, ct)
            .ConfigureAwait(false);

        if (row is null)
        {
            row = new RobotSessionRecord { AgvSerialNumber = session.AgvSerialNumber };
            context.RobotSessions.Add(row);
        }

        row.Manufacturer = session.Manufacturer;
        row.ProtocolVersion = session.ProtocolVersion;
        row.ConnectionState = session.ConnectionState;
        row.LastSeenAt = session.LastSeenAt;
        row.StateJson = session.StateJson;
        row.UpdatedAt = session.UpdatedAt;

        await context.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<RobotSession?> GetAsync(string agvSerialNumber, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agvSerialNumber);

        await using var context = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await context.RobotSessions
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.AgvSerialNumber == agvSerialNumber, ct)
            .ConfigureAwait(false);

        return row is null
            ? null
            : new RobotSession(
                row.AgvSerialNumber,
                row.Manufacturer,
                row.ProtocolVersion,
                row.ConnectionState,
                row.LastSeenAt,
                row.StateJson,
                row.UpdatedAt);
    }
}
