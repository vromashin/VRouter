using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using VR.RoutePlanner.Application.Robots;
using VR.RoutePlanner.Infrastructure.Persistence;

namespace VR.RoutePlanner.Infrastructure.Persistence;

/// <summary>
/// Номер заказа из таблицы <c>vrt_RobotOrder</c>. Если таблицы или строк еще нет,
/// счетчик начинается с 1: заказ не должен падать из-за отсутствия истории.
/// </summary>
public sealed class VrtRobotOrderNumberSource : IRobotOrderNumberSource
{
    private const string Sql = """
        SELECT COALESCE(MAX("OrderNumber"), 0) FROM public."vrt_RobotOrder"
        """;

    private readonly IDbContextFactory<RoutePlannerDbContext> _contextFactory;
    private readonly ILogger<VrtRobotOrderNumberSource> _logger;
    private int _warned;

    /// <summary>
    /// Создает источник номеров заказов.
    /// </summary>
    /// <param name="contextFactory">Фабрика контекста базы <c>rms</c>.</param>
    /// <param name="logger">Журнал.</param>
    public VrtRobotOrderNumberSource(
        IDbContextFactory<RoutePlannerDbContext> contextFactory,
        ILogger<VrtRobotOrderNumberSource> logger)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(logger);

        _contextFactory = contextFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<long> NextAsync(CancellationToken ct)
    {
        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            var connection = context.Database.GetDbConnection();
            if (connection.State != System.Data.ConnectionState.Open)
            {
                await connection.OpenAsync(ct).ConfigureAwait(false);
            }

            await using var command = connection.CreateCommand();
            command.CommandText = Sql;
            var value = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
            var last = value is null or DBNull ? 0 : Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
            return last + 1;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (Interlocked.CompareExchange(ref _warned, 1, 0) == 0)
            {
                _logger.LogWarning(
                    exception,
                    "Таблица vrt_RobotOrder недоступна, нумерация заказов начинается с 1");
            }

            return 1;
        }
    }
}
