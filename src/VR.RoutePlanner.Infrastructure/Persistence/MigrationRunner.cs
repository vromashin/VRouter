using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace VR.RoutePlanner.Infrastructure.Persistence;

/// <summary>
/// Запуск миграций своей истории: advisory lock, отдельная таблица истории
/// <c>public."__EFMigrationsHistory_RoutePlanner"</c> и никакого касания истории соседних сервисов.
/// </summary>
public static class MigrationRunner
{
    /// <summary>Имя таблицы истории миграций сервиса.</summary>
    public const string HistoryTable = "__EFMigrationsHistory_RoutePlanner";

    /// <summary>Ключ advisory lock, сериализующего миграции сервиса.</summary>
    public const string LockKey = "rms:migrations:routeplanner";

    /// <summary>Имя таблицы истории, которую могли создать ранние сборки сервиса.</summary>
    public const string LegacyHistoryTable = "RoutePlannerMigrationsHistory";

    /// <summary>
    /// Вычисляет 64-битный ключ advisory lock из строкового ключа.
    /// </summary>
    /// <param name="lockKey">Строковый ключ блокировки.</param>
    /// <returns>Ключ для <c>pg_advisory_lock(bigint)</c>.</returns>
    public static long ComputeLockKey(string lockKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lockKey);

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(lockKey));
        return BitConverter.ToInt64(hash, 0);
    }

    /// <summary>
    /// Возвращает SQL создания таблицы истории миграций, если ее нет.
    /// </summary>
    /// <param name="historyTable">Имя таблицы истории.</param>
    /// <returns>Идемпотентный SQL.</returns>
    public static string CreateHistoryTableSql(string historyTable)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(historyTable);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"""
             CREATE TABLE IF NOT EXISTS public."{historyTable}" (
                 "MigrationId" character varying(150) NOT NULL,
                 "ProductVersion" character varying(32) NOT NULL,
                 CONSTRAINT "PK_{historyTable}" PRIMARY KEY ("MigrationId")
             );
             """);
    }

    /// <summary>
    /// Применяет ожидающие миграции под advisory lock.
    /// </summary>
    /// <param name="context">Контекст базы <c>rms</c>.</param>
    /// <param name="historyTable">Имя таблицы истории миграций.</param>
    /// <param name="lockKey">Строковый ключ advisory lock.</param>
    /// <param name="logger">Логгер.</param>
    /// <param name="ct">Токен отмены.</param>
    /// <returns>Количество примененных миграций.</returns>
    public static async Task<int> MigrateAsync(
        DbContext context,
        string historyTable,
        string lockKey,
        ILogger logger,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(logger);

        var applied = 0;
        await context.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        var lockId = ComputeLockKey(lockKey);

        try
        {
            await ExecuteAsync(context, "SELECT pg_advisory_lock(@lock_id)", ("lock_id", lockId), ct).ConfigureAwait(false);

            try
            {
                await ExecuteAsync(context, CreateHistoryTableSql(historyTable), null, ct).ConfigureAwait(false);
                await CopyLegacyHistoryAsync(context, historyTable, logger, ct).ConfigureAwait(false);

                var pending = context.Database.GetPendingMigrations().ToList();
                if (pending.Count == 0)
                {
                    logger.LogInformation("Ожидающих миграций нет, схема RoutePlanner актуальна.");
                    return 0;
                }

                logger.LogInformation(
                    "Применяются миграции RoutePlanner: {Migrations}.",
                    string.Join(", ", pending));

                await context.Database.MigrateAsync(ct).ConfigureAwait(false);
                applied = pending.Count;
                logger.LogInformation(
                    "Миграции RoutePlanner применены: {Count} шт., история в {HistoryTable}.",
                    applied,
                    historyTable);
            }
            finally
            {
                await ExecuteAsync(context, "SELECT pg_advisory_unlock(@lock_id)", ("lock_id", lockId), CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            await context.Database.CloseConnectionAsync().ConfigureAwait(false);
        }

        return applied;
    }

    /// <summary>
    /// Проверяет, что ожидающих миграций нет, и падает с понятным сообщением, если они есть.
    /// </summary>
    /// <param name="context">Контекст базы <c>rms</c>.</param>
    /// <param name="historyTable">Имя таблицы истории миграций.</param>
    /// <param name="lockKey">Строковый ключ advisory lock.</param>
    /// <param name="logger">Логгер.</param>
    /// <param name="ct">Токен отмены.</param>
    /// <exception cref="InvalidOperationException">В базе есть непримененные миграции.</exception>
    public static async Task EnsureNoPendingMigrationsAsync(
        DbContext context,
        string historyTable,
        string lockKey,
        ILogger logger,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(logger);

        await context.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        var lockId = ComputeLockKey(lockKey);

        try
        {
            await ExecuteAsync(context, "SELECT pg_advisory_lock(@lock_id)", ("lock_id", lockId), ct).ConfigureAwait(false);

            try
            {
                var pending = context.Database.GetPendingMigrations().ToList();
                if (pending.Count > 0)
                {
                    throw new InvalidOperationException(
                        $"В базе rms есть непримененные миграции RoutePlanner: {string.Join(", ", pending)}. "
                        + $"Примените их вручную в таблицу {historyTable} или включите Database:AutoMigrate в Development. "
                        + "Сервис не работает на устаревшей схеме.");
                }

                logger.LogInformation("Ожидающих миграций нет, схема RoutePlanner актуальна.");
            }
            finally
            {
                await ExecuteAsync(context, "SELECT pg_advisory_unlock(@lock_id)", ("lock_id", lockId), CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            await context.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    private static async Task CopyLegacyHistoryAsync(
        DbContext context,
        string historyTable,
        ILogger logger,
        CancellationToken ct)
    {
        var exists = await LegacyHistoryExistsAsync(context, ct).ConfigureAwait(false);
        if (!exists)
        {
            return;
        }

        var known = context.Database.GetMigrations().ToList();
        if (known.Count == 0)
        {
            return;
        }

        var sql = string.Create(
            CultureInfo.InvariantCulture,
            $"""
             INSERT INTO public."{historyTable}" ("MigrationId", "ProductVersion")
             SELECT legacy."MigrationId", legacy."ProductVersion"
             FROM public."{LegacyHistoryTable}" AS legacy
             WHERE legacy."MigrationId" IN ({string.Join(", ", known.Select((_, index) => $"@id{index}"))})
               AND NOT EXISTS (
                   SELECT 1 FROM public."{historyTable}" AS current
                   WHERE current."MigrationId" = legacy."MigrationId");
             """);

        var parameters = known
            .Select((id, index) => ($"id{index}", (object?)id))
            .ToArray();

        var copied = await ExecuteAsync(context, sql, parameters, ct).ConfigureAwait(false);
        if (copied > 0)
        {
            logger.LogInformation(
                "Из устаревшей таблицы {LegacyTable} перенесено записей истории: {Count}.",
                LegacyHistoryTable,
                copied);
        }
    }

    private static async Task<bool> LegacyHistoryExistsAsync(DbContext context, CancellationToken ct)
    {
        var connection = context.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public' AND table_name = @name";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "name";
        parameter.Value = LegacyHistoryTable;
        command.Parameters.Add(parameter);

        var result = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return Convert.ToInt64(result, CultureInfo.InvariantCulture) > 0;
    }

    private static async Task<int> ExecuteAsync(
        DbContext context,
        string sql,
        (string Name, object? Value)[]? parameters,
        CancellationToken ct)
    {
        var connection = context.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        if (parameters is not null)
        {
            foreach (var (name, value) in parameters)
            {
                var parameter = command.CreateParameter();
                parameter.ParameterName = name;
                parameter.Value = value ?? DBNull.Value;
                command.Parameters.Add(parameter);
            }
        }

        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static Task<int> ExecuteAsync(
        DbContext context,
        string sql,
        (string Name, object? Value) parameter,
        CancellationToken ct) =>
        ExecuteAsync(context, sql, new[] { parameter }, ct);
}
