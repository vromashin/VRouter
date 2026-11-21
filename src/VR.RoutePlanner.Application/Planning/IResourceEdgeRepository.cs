using VR.RoutePlanner.Domain.Traffic;

namespace VR.RoutePlanner.Application.Planning;

/// <summary>
/// Чтение реберных ресурсов профиля из <c>vrt_TrafficResourceEdge</c>. Только чтение:
/// таблицу наполняет прогрев.
/// </summary>
public interface IResourceEdgeRepository
{
    /// <summary>
    /// Отдает реберные ресурсы профиля для версии карты.
    /// </summary>
    /// <param name="mapVersionId">Идентификатор строки <c>vrt_MapVersion</c>.</param>
    /// <param name="profile">Имя профиля робота.</param>
    /// <param name="ct">Токен отмены.</param>
    /// <returns>Строки ресурсов ребер; пустой список, если ресурсов нет.</returns>
    Task<IReadOnlyList<TrafficResourceEdge>> GetAsync(
        Guid mapVersionId,
        string profile,
        CancellationToken ct);
}
