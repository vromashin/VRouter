using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VR.RoutePlanner.Application.Configuration;
using VR.RoutePlanner.Application.Robots;
using VR.RoutePlanner.Domain.Robots;
using VR.RoutePlanner.Infrastructure.Mqtt;

namespace VR.RoutePlanner.Infrastructure.Persistence;

/// <summary>
/// Состояние роботов с приоритетом памяти процесса и переходом на таблицу
/// <c>vrt_RobotSession</c>. Нужен, чтобы API видел состояние, записанное процессом Workers.
/// </summary>
public sealed class DatabaseRobotStateReader : IRobotStateReader
{
    private readonly MqttRobotStateReader _memory;
    private readonly IRobotSessionRepository _sessions;
    private readonly IOptions<AgvMonitoringOptions> _options;
    private readonly TimeProvider _time;
    private readonly ILogger<DatabaseRobotStateReader> _logger;

    /// <summary>
    /// Создает читатель состояния.
    /// </summary>
    /// <param name="memory">Снимки процесса, читающего MQTT.</param>
    /// <param name="sessions">Хранилище сессий.</param>
    /// <param name="options">Параметры <c>AgvMonitoring</c>.</param>
    /// <param name="time">Источник времени.</param>
    /// <param name="logger">Журнал.</param>
    public DatabaseRobotStateReader(
        MqttRobotStateReader memory,
        IRobotSessionRepository sessions,
        IOptions<AgvMonitoringOptions> options,
        TimeProvider time,
        ILogger<DatabaseRobotStateReader> logger)
    {
        ArgumentNullException.ThrowIfNull(memory);
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);

        _memory = memory;
        _sessions = sessions;
        _options = options;
        _time = time;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<RobotSnapshot?> GetLatestAsync(string serialNumber, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serialNumber);

        var now = _time.GetUtcNow();
        var stale = TimeSpan.FromSeconds(_options.Value.StaleStateSeconds);

        var fromMemory = await _memory.GetLatestAsync(serialNumber, ct).ConfigureAwait(false);
        if (fromMemory is not null && now - fromMemory.ReceivedAt <= stale)
        {
            return fromMemory;
        }

        var session = await _sessions.GetAsync(serialNumber, ct).ConfigureAwait(false);
        if (session is null)
        {
            return null;
        }

        if (now - session.LastSeenAt > stale)
        {
            _logger.LogDebug(
                "Состояние робота {Serial} протухло: последнее сообщение {Ago:F1} с назад",
                serialNumber,
                (now - session.LastSeenAt).TotalSeconds);
            return null;
        }

        return ToSnapshot(session);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyCollection<RobotSnapshot>> GetAllAsync(CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        var stale = TimeSpan.FromSeconds(_options.Value.StaleStateSeconds);
        var result = new Dictionary<string, RobotSnapshot>(StringComparer.Ordinal);

        foreach (var snapshot in await _memory.GetAllAsync(ct).ConfigureAwait(false))
        {
            if (now - snapshot.ReceivedAt <= stale)
            {
                result[snapshot.SerialNumber] = snapshot;
            }
        }

        return result.Values.ToArray();
    }

    private static RobotSnapshot ToSnapshot(RobotSession session)
    {
        if (!string.IsNullOrWhiteSpace(session.StateJson)
            && RobotStateParser.TryParse(session.StateJson, out var parsed, out _)
            && parsed is not null)
        {
            return parsed with
            {
                ConnectionState = session.ConnectionState ?? parsed.ConnectionState,
                ReceivedAt = session.LastSeenAt,
            };
        }

        return new RobotSnapshot(
            session.AgvSerialNumber,
            session.ConnectionState ?? "UNKNOWN",
            false,
            null,
            0,
            0,
            0,
            0,
            null,
            0,
            Array.Empty<string>(),
            Array.Empty<RobotNodeState>(),
            session.LastSeenAt,
            session.ProtocolVersion ?? string.Empty);
    }
}
