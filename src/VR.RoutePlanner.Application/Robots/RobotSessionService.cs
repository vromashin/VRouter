using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using VR.RoutePlanner.Application.Robots;
using VR.RoutePlanner.Domain.Robots;

namespace VR.RoutePlanner.Application.Robots;

/// <summary>
/// Запись состояния роботов в <c>vrt_RobotSession</c> с троттлингом: не чаще одного
/// сохранения на робота в секунду. Нужна, чтобы API и Workers видели одно состояние.
/// </summary>
public sealed class RobotSessionService
{
    private static readonly TimeSpan Throttle = TimeSpan.FromSeconds(1);

    private readonly IRobotSessionRepository _sessions;
    private readonly TimeProvider _time;
    private readonly ILogger<RobotSessionService> _logger;
    private readonly ConcurrentDictionary<string, (DateTimeOffset At, string StateJson)> _lastSaved = new(StringComparer.Ordinal);

    /// <summary>
    /// Создает сервис сессий.
    /// </summary>
    /// <param name="sessions">Хранилище сессий.</param>
    /// <param name="time">Источник времени.</param>
    /// <param name="logger">Журнал.</param>
    public RobotSessionService(
        IRobotSessionRepository sessions,
        TimeProvider time,
        ILogger<RobotSessionService> logger)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);

        _sessions = sessions;
        _time = time;
        _logger = logger;
    }

    /// <summary>
    /// Сохраняет снимок робота, соблюдая троттлинг.
    /// </summary>
    /// <param name="snapshot">Снимок.</param>
    /// <param name="stateJson">Тело исходного сообщения <c>state</c>.</param>
    /// <param name="ct">Токен отмены.</param>
    /// <returns><c>true</c>, если строка записана.</returns>
    public async Task<bool> SaveAsync(RobotSnapshot snapshot, string? stateJson, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshot.SerialNumber);

        var now = _time.GetUtcNow();
        if (_lastSaved.TryGetValue(snapshot.SerialNumber, out var last)
            && now - last.At < Throttle
            && string.Equals(last.StateJson, stateJson, StringComparison.Ordinal))
        {
            return false;
        }

        var session = new RobotSession(
            snapshot.SerialNumber,
            ManufacturerOf(stateJson),
            snapshot.Version,
            snapshot.ConnectionState,
            snapshot.ReceivedAt,
            stateJson,
            now);

        await _sessions.SaveAsync(session, ct).ConfigureAwait(false);
        _lastSaved[snapshot.SerialNumber] = (now, stateJson ?? string.Empty);
        _logger.LogDebug("Сессия робота {Serial} сохранена", snapshot.SerialNumber);
        return true;
    }

    private static string? ManufacturerOf(string? stateJson)
    {
        if (string.IsNullOrWhiteSpace(stateJson))
        {
            return null;
        }

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(stateJson);
            return document.RootElement.TryGetProperty("manufacturer", out var manufacturer)
                && manufacturer.ValueKind == System.Text.Json.JsonValueKind.String
                ? manufacturer.GetString()
                : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}
