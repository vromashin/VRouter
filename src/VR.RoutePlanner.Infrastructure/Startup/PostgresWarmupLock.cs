using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
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

    /// <summary>
    /// Предельное ожидание блокировки по умолчанию: столько процесс ждет прогрева, идущего
    /// в соседнем процессе, и падает с <see cref="TimeoutException"/> вместо бесконечного ожидания.
    /// </summary>
    public static readonly TimeSpan DefaultWaitTimeout = TimeSpan.FromMinutes(5);

    private readonly IDbContextFactory<RoutePlannerDbContext> _factory;
    private readonly ILogger<PostgresWarmupLock> _logger;
    private readonly TimeSpan _waitTimeout;

    /// <summary>Создает блокировку прогрева поверх базы <c>rms</c>.</summary>
    /// <param name="factory">Фабрика контекста базы <c>rms</c>.</param>
    /// <param name="logger">Логгер.</param>
    /// <param name="waitTimeout">Предельное ожидание блокировки; по умолчанию <see cref="DefaultWaitTimeout"/>.</param>
    public PostgresWarmupLock(
        IDbContextFactory<RoutePlannerDbContext> factory,
        ILogger<PostgresWarmupLock> logger,
        TimeSpan? waitTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(logger);

        _factory = factory;
        _logger = logger;
        _waitTimeout = waitTimeout ?? DefaultWaitTimeout;
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
            await SetStatementTimeoutAsync(context, _waitTimeout, ct).ConfigureAwait(false);
            _logger.LogDebug(
                "Ожидание блокировки прогрева карты {MapId}, не дольше {Timeout}.",
                mapId,
                _waitTimeout);

            try
            {
                await ExecuteAsync(context, "SELECT pg_advisory_lock(@lock_id)", lockId, ct).ConfigureAwait(false);
            }
            catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.QueryCanceled)
            {
                throw new TimeoutException(
                    $"Не удалось дождаться блокировки прогрева карты {mapId} за {_waitTimeout}: "
                    + "в другом процессе прогрев идет слишком долго.",
                    exception);
            }

            await ExecuteAsync(context, "SET statement_timeout = 0", CancellationToken.None)
                .ConfigureAwait(false);
            _logger.LogDebug("Блокировка прогрева карты {MapId} взята.", mapId);

            return new Lease(context, lockId, mapId, _logger);
        }
        catch
        {
            // Неудачное или отмененное ожидание: контекст освобождается, соединение возвращается
            // в пул, а Npgsql перед возвратом сбрасывает сессию (NoResetOnClose в строке
            // подключения не задан), и DISCARD ALL снимает сессионные advisory lock.
            await context.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task SetStatementTimeoutAsync(
        DbContext context,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var milliseconds = (long)timeout.TotalMilliseconds;
        var sql = string.Create(CultureInfo.InvariantCulture, $"SET statement_timeout = {milliseconds}");
        await ExecuteAsync(context, sql, ct).ConfigureAwait(false);
    }

    private static async Task ExecuteAsync(DbContext context, string sql, CancellationToken ct)
    {
        var connection = context.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
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
