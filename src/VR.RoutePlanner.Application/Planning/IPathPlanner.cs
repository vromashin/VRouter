using VR.RoutePlanner.Domain.Planning;

namespace VR.RoutePlanner.Application.Planning;

/// <summary>
/// Планировщик маршрута по сети движения.
/// </summary>
public interface IPathPlanner
{
    /// <summary>
    /// Строит маршрут между узлами. Исключения бросаются только при отмене и ошибках
    /// инфраструктуры: отсутствие данных, профиля или пути возвращается кодом причины.
    /// </summary>
    /// <param name="request">Запрос на маршрут.</param>
    /// <param name="ct">Токен отмены.</param>
    /// <returns>Маршрут либо код причины, по которой его нет.</returns>
    Task<RoutePlanResult> PlanAsync(RoutePlanRequest request, CancellationToken ct);
}
