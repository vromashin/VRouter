using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using VR.RoutePlanner.Application.Startup;
using VR.RoutePlanner.Domain.Traffic;
using VR.RoutePlanner.Infrastructure.Persistence;

namespace VR.RoutePlanner.Infrastructure.Startup;

/// <summary>
/// Хранит итог последнего прогрева в <c>public."vrt_WarmupState"</c>: одна строка на карту,
/// поэтому статус, записанный одним процессом, читается из любого другого.
/// </summary>
public sealed class WarmupStateRepository : IWarmupStateRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IDbContextFactory<RoutePlannerDbContext> _factory;

    /// <summary>Создает хранилище состояния прогрева.</summary>
    /// <param name="factory">Фабрика контекста базы <c>rms</c>.</param>
    public WarmupStateRepository(IDbContextFactory<RoutePlannerDbContext> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        _factory = factory;
    }

    /// <inheritdoc />
    public async Task<WarmupResult?> GetAsync(string mapId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mapId);

        await using var context = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var record = await context.WarmupStates
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.MapId == mapId, ct)
            .ConfigureAwait(false);

        return record is null ? null : ToResult(record);
    }

    /// <inheritdoc />
    public async Task SaveAsync(WarmupResult result, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(result);

        await using var context = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var record = await context.WarmupStates
            .FirstOrDefaultAsync(item => item.MapId == result.MapId, ct)
            .ConfigureAwait(false);

        if (record is null)
        {
            record = new WarmupStateRecord { MapId = result.MapId };
            context.WarmupStates.Add(record);
        }

        record.MapVersionId = result.MapVersionId;
        record.GraphVersion = result.GraphVersion;
        record.EdgeCount = result.EdgeCount;
        record.WidthSource = result.WidthSource.ToString();
        record.ResourceCount = JsonSerializer.Serialize(result.ResourceCount, JsonOptions);
        record.ImpassableCount = JsonSerializer.Serialize(result.ImpassableCount, JsonOptions);
        record.DurationMs = result.DurationMs;
        record.ReusedWidths = result.ReusedWidths;
        record.CompletedAt = result.CompletedAt;

        await context.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private static WarmupResult ToResult(WarmupStateRecord record) => new(
        record.MapId,
        record.MapVersionId,
        record.GraphVersion,
        record.EdgeCount,
        Enum.Parse<WidthSource>(record.WidthSource),
        ReadCounts(record.ResourceCount),
        ReadCounts(record.ImpassableCount),
        record.DurationMs,
        record.ReusedWidths,
        record.CompletedAt);

    private static IReadOnlyDictionary<string, int> ReadCounts(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new Dictionary<string, int>(StringComparer.Ordinal);
        }

        var counts = JsonSerializer.Deserialize<Dictionary<string, int>>(json, JsonOptions)
            ?? new Dictionary<string, int>(StringComparer.Ordinal);

        return new Dictionary<string, int>(counts, StringComparer.Ordinal);
    }
}
