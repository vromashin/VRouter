namespace VR.RoutePlanner.Application.Robots;

/// <summary>
/// Запись живой позиции робота в <c>public."Agvs"</c>, чтобы движение было видно на карте.
/// Вызывается только при включенном <c>Legacy:WriteAgvsEnabled</c>.
/// </summary>
public interface ILegacyAgvVisualizationWriter
{
    /// <summary>
    /// Обновляет позицию, батарею и состояние робота по визуализации.
    /// </summary>
    /// <param name="visualization">Визуализация.</param>
    /// <param name="ct">Токен отмены.</param>
    Task ApplyVisualizationAsync(RobotVisualization visualization, CancellationToken ct);
}
