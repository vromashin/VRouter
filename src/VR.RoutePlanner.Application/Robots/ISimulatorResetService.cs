namespace VR.RoutePlanner.Application.Robots;

/// <summary>
/// Перезапуск симулятора на стенде, когда он не очищает состояние робота.
/// На реальном роботе не используется: там состояние очищает прошивка.
/// </summary>
public interface ISimulatorResetService
{
    /// <summary>Включена ли автоочистка через перезапуск симулятора.</summary>
    bool Enabled { get; }

    /// <summary>Перезапускает симулятор и ждет его готовности.</summary>
    /// <param name="ct">Отмена.</param>
    Task ResetAsync(CancellationToken ct);
}
