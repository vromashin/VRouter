namespace VR.RoutePlanner.Application.Startup;

/// <summary>
/// Ошибка одного шага прогрева ресурсов: имя шага нужно, чтобы понять,
/// на чем именно остановился прогрев.
/// </summary>
public sealed class WarmupStepException : Exception
{
    /// <summary>
    /// Создает ошибку шага.
    /// </summary>
    /// <param name="step">Имя шага прогрева.</param>
    /// <param name="inner">Исходное исключение.</param>
    public WarmupStepException(string step, Exception inner)
        : base($"Шаг прогрева «{step}» завершился ошибкой: {inner.Message}", inner)
    {
        Step = step;
    }

    /// <summary>Имя шага, на котором произошла ошибка.</summary>
    public string Step { get; }
}
