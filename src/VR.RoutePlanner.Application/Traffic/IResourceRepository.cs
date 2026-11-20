using VR.RoutePlanner.Domain.Traffic;

namespace VR.RoutePlanner.Application.Traffic;

/// <summary>
/// Сохранение построенных ресурсов одной версии карты.
/// </summary>
public interface IResourceRepository
{
    /// <summary>
    /// Заменяет ресурсы версии карты: старые строки удаляются, новые пишутся одной транзакцией.
    /// </summary>
    /// <param name="mapVersionId">Идентификатор строки <c>vrt_MapVersion</c>.</param>
    /// <param name="sets">Наборы ресурсов по профилям.</param>
    /// <param name="ct">Токен отмены.</param>
    Task SaveAsync(Guid mapVersionId, IReadOnlyList<ProfileResourceSet> sets, CancellationToken ct);
}
