using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using VR.RoutePlanner.Application.Robots;

namespace VR.RoutePlanner.Infrastructure.Legacy;

/// <summary>
/// Обновление <c>public."Agvs"</c> по сообщению <c>visualization</c>: позиция, ориентация,
/// батарея, движение и момент обновления.
/// </summary>
public sealed class LegacyAgvVisualizationWriter : ILegacyAgvVisualizationWriter
{
    private readonly IDbContextFactory<LegacyReadDbContext> _contextFactory;
    private readonly ILogger<LegacyAgvVisualizationWriter> _logger;

    /// <summary>
    /// Создает писатель позиции.
    /// </summary>
    /// <param name="contextFactory">Фабрика контекста legacy-таблиц.</param>
    /// <param name="logger">Журнал.</param>
    public LegacyAgvVisualizationWriter(
        IDbContextFactory<LegacyReadDbContext> contextFactory,
        ILogger<LegacyAgvVisualizationWriter> logger)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(logger);

        _contextFactory = contextFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task ApplyVisualizationAsync(RobotVisualization visualization, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(visualization);

        await using var context = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await context.Agvs
            .FirstOrDefaultAsync(record => record.SerialNumber == visualization.SerialNumber, ct)
            .ConfigureAwait(false);

        if (row is null)
        {
            _logger.LogDebug("Робот {Serial} не найден в Agvs, строка не обновлена.", visualization.SerialNumber);
            return;
        }

        // Отбрасываем устаревшие снимки: позиция должна идти только вперед по времени,
        // иначе на карте появляются скачки между источниками.
        if (visualization.ReceivedAt.UtcDateTime < row.LastUpdateTime)
        {
            return;
        }

        row.PositionX = visualization.PositionX;
        row.PositionY = visualization.PositionY;
        row.Theta = visualization.Theta;
        row.Driving = visualization.Driving;
        row.BatteryCharge = visualization.BatteryCharge;
        row.CurrentState = visualization.Driving ? "WORKING" : "IDLE";
        row.LastUpdateTime = visualization.ReceivedAt.UtcDateTime;
        row.UpdatedDate = DateTime.UtcNow;
        if (!string.IsNullOrWhiteSpace(visualization.MapId))
        {
            row.MapId = visualization.MapId;
        }

        await context.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
