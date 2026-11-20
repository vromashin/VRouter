using Microsoft.EntityFrameworkCore;
using VR.RoutePlanner.Domain.Graph;
using VR.RoutePlanner.Infrastructure.Persistence;

namespace VR.RoutePlanner.Tests.Integration.Support;

/// <summary>
/// Строка <c>vrt_MapVersion</c> для тестового графа: без нее не работают внешние ключи
/// таблиц ресурсов и ширин.
/// </summary>
internal static class MapVersionSeed
{
    /// <summary>
    /// Возвращает существующую строку версии карты либо создает новую.
    /// </summary>
    /// <param name="connectionString">Строка подключения к базе <c>rms</c>.</param>
    /// <param name="graph">Загруженный граф карты.</param>
    /// <param name="ct">Токен отмены.</param>
    /// <returns>Идентификатор <c>vrt_MapVersion.Id</c>.</returns>
    public static async Task<Guid> EnsureAsync(string connectionString, WarehouseGraph graph, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(graph);

        await using var context = new RoutePlannerDbContext(TestDatabase.BuildOptions(connectionString));

        var existing = await context.MapVersions
            .AsNoTracking()
            .Where(record => record.MapId == TestDatabase.MapId && record.GraphVersion == graph.GraphVersion)
            .Select(record => record.Id)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (existing != Guid.Empty)
        {
            return existing;
        }

        var record = new MapVersionRecord
        {
            Id = Guid.NewGuid(),
            MapId = TestDatabase.MapId,
            MapVersion = graph.MapVersion,
            GraphVersion = graph.GraphVersion,
            CreatedAt = DateTime.UtcNow,
        };

        context.MapVersions.Add(record);
        await context.SaveChangesAsync(ct).ConfigureAwait(false);

        return record.Id;
    }
}
