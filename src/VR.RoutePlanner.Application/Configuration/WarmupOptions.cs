using Microsoft.Extensions.Options;

namespace VR.RoutePlanner.Application.Configuration;

/// <summary>
/// Параметры прогрева ресурсов из секции конфигурации <c>Routing:Warmup</c>.
/// </summary>
public sealed class WarmupOptions
{
    /// <summary>Имя секции конфигурации.</summary>
    public const string SectionName = "Routing:Warmup";

    /// <summary>Включен ли прогрев: при <c>false</c> хостед-сервис ничего не делает.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Карта, которую прогревает сервис, например <c>801</c>.</summary>
    public string MapId { get; set; } = "801";

    /// <summary>Выполнять прогрев сразу при старте сервиса.</summary>
    public bool RunOnStartup { get; set; } = true;

    /// <summary>
    /// Периодичность повторного прогрева в минутах. Ноль отключает периодичность:
    /// прогрев выполняется только при старте.
    /// </summary>
    public int RefreshIntervalMinutes { get; set; }

    /// <summary>
    /// Ошибка прогрева останавливает хост. При <c>false</c> ошибка только логируется,
    /// и сервис продолжает работать по расписанию.
    /// </summary>
    public bool FailFast { get; set; }

    /// <summary>
    /// Проверяет параметры прогрева.
    /// </summary>
    /// <returns>Текст ошибки либо <c>null</c>, если параметры корректны.</returns>
    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(MapId))
        {
            return "Не задан Routing:Warmup:MapId: непонятно, какую карту прогревать.";
        }

        if (RefreshIntervalMinutes < 0)
        {
            return "Routing:Warmup:RefreshIntervalMinutes не может быть отрицательным.";
        }

        return null;
    }
}

/// <summary>
/// Проверка <see cref="WarmupOptions"/> при старте сервиса.
/// </summary>
public sealed class WarmupOptionsValidator : IValidateOptions<WarmupOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, WarmupOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var error = options.Validate();
        return error is null ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(error);
    }
}
