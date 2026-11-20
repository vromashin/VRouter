using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using VR.RoutePlanner.Application.Startup;
using VR.RoutePlanner.Infrastructure.Persistence;

namespace VR.RoutePlanner.Infrastructure.Startup;

/// <summary>
/// Блокировка прогрева на сессионном advisory lock PostgreSQL (<c>pg_advisory_lock</c>).
/// Держит отдельное соединение от захвата до освобождения: снять блокировку может только
/// сессия-владелец, поэтому Api и Workers видят одну и ту же блокировку.
/// </summary>
public sealed class PostgresWarmupLock : IWarmupLock
{
    /// <summary>Префикс ключа блокировки; к префиксу дописывается идентификатор карты.</summary>
    public const string LockKeyPrefix = "rms:warmup:routeplanner:";

    private readonly IDbContextFactory<RoutePlannerDbContext> _factory;
    private readonly ILogger<PostgresWarmupLock> _logger;

    /// <summary>Создает блокировку прогрева поверх базы <c>rms</c>.</summary>
    /// <param name="factory">Фабрика контекста базы <c>rms</c>.</param>
    /// <param name="logger">Логгер.</param>
    public PostgresWarmupLock(
        IDbContextFactory<RoutePlannerDbContext> factory,
        ILogger<PostgresWarmupLock> logger)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(logger);

        _factory = factory;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IAsyncDisposable> AcquireAsync(string mapId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mapId);

        var lockId = MigrationRunner.ComputeLockKey(LockKeyPrefix + mapId);
        var context = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);

        try
        {
            await context.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
            _logger.LogDebug("Ожидание блокировки прогрева карты {MapId}.", mapId);
            await ExecuteAsync(context, "SELECT pg_advisory_lock(@lock_id)", lockId, ct).ConfigureAwait(false);
            _logger.LogDebug("Блокировка прогрева карты {MapId} взята.", mapId);

            return new Lease(context, lockId, mapId, _logger);
        }
        catch
        {
            // Отмененное ожидание: соединение возвращается в пул, а Npgsql сбрасывает
            // сессию через DISCARD ALL, поэтому случайно взятая блокировка не остается.
            await context.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task ExecuteAsync(
        DbContext context,
        string sql,
        long lockId,
        CancellationToken ct)
    {
        var connection = context.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        var parameter = command.CreateParameter();
        parameter.ParameterName = "lock_id";
        parameter.Value = lockId;
        command.Parameters.Add(parameter);

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Владение блокировкой: живет столько же, сколько прогресс.</summary>
    private sealed class Lease : IAsyncDisposable
    {
        private readonly RoutePlannerDbContext _context;
        private readonly long _lockId;
        private readonly string _mapId;
        private readonly ILogger _logger;
        private bool _released;

        public Lease(RoutePlannerDbContext context, long lockId, string mapId, ILogger logger)
        {
            _context = context;
            _lockId = lockId;
            _mapId = mapId;
            _logger = logger;
        }

        public async ValueTask DisposeAsync()
        {
            if (_released)
            {
                return;
            }

            _released = true;

            try
            {
                if (_context.Database.GetDbConnection().State == System.Data.ConnectionState.Open)
                {
                    await ExecuteAsync(_context, "SELECT pg_advisory_unlock(@lock_id)", _lockId, CancellationToken.None)
                        .ConfigureAwait(false);
                    _logger.LogDebug("Блокировка прогрева карты {MapId} снята.", _mapId);
                }
            }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    exception,
                    "Не удалось снять блокировку прогрева карты {MapId}: {Error}.",
                    _mapId,
                    exception.Message);
            }
            finally
            {
                await _context.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
