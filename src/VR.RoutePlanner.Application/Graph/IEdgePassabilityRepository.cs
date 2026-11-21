namespace VR.RoutePlanner.Application.Graph;

/// <summary>
/// Чтение строк <c>vrt_EdgePassability</c> по версии карты. Только чтение: таблицу наполняет прогрев.
/// </summary>
public interface IEdgePassabilityRepository
{
    /// <summary>
    /// Возвращает строки проходимости версии карты для профиля.
    /// </summary>
    /// <param name="mapVersionId">Идентификатор строки <c>vrt_MapVersion</c>.</param>
    /// <param name="agvProfile">Имя профиля робота.</param>
    /// <param name="ct">Токен отмены.</param>
    /// <returns>Строки проходимости; пустой список, если прогрев еще не писал по этой версии.</returns>
    Task<IReadOnlyList<Domain.Graph.EdgePassability>> GetAsync(
        Guid mapVersionId,
        string agvProfile,
        CancellationToken ct);
}
