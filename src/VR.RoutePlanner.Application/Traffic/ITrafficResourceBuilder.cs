using VR.RoutePlanner.Domain.Graph;
using VR.RoutePlanner.Domain.Traffic;

namespace VR.RoutePlanner.Application.Traffic;

/// <summary>
/// Построение ресурсов профилей по графу карты. Интерфейс нужен для подмены
/// построителя в тестах оркестратора прогрева.
/// </summary>
public interface ITrafficResourceBuilder
{
    /// <summary>
    /// Источник ширины коридоров из конфигурации: растр, ручное значение или значение по умолчанию.
    /// </summary>
    WidthSource WidthSource { get; }

    /// <summary>
    /// Строит ресурсы ребер, ресурсы узлов и конфликты между ними по растру карты.
    /// </summary>
    /// <param name="graph">Загруженный граф карты.</param>
    /// <param name="mapVersionId">Идентификатор строки <c>vrt_MapVersion</c>.</param>
    /// <param name="ct">Токен отмены.</param>
    /// <returns>Набор ресурсов на каждый профиль из конфигурации.</returns>
    Task<IReadOnlyList<ProfileResourceSet>> BuildAsync(
        WarehouseGraph graph,
        Guid mapVersionId,
        CancellationToken ct = default);

    /// <summary>
    /// Строит ресурсы по готовому набору ширин: растр не читается.
    /// </summary>
    /// <param name="graph">Загруженный граф карты.</param>
    /// <param name="widths">Ширины ребер версии карты.</param>
    /// <param name="mapVersionId">Идентификатор строки <c>vrt_MapVersion</c>.</param>
    /// <param name="ct">Токен отмены.</param>
    /// <returns>Набор ресурсов на каждый профиль из конфигурации.</returns>
    Task<IReadOnlyList<ProfileResourceSet>> BuildAsync(
        WarehouseGraph graph,
        IReadOnlyList<EdgeWidth> widths,
        Guid mapVersionId,
        CancellationToken ct = default);
}
