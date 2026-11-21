using Microsoft.Extensions.Options;
using VR.RoutePlanner.Application.Configuration;

namespace VR.RoutePlanner.Infrastructure.Startup;

/// <summary>
/// Проверка параметров <c>AgvMonitoring</c> на старте сервиса.
/// </summary>
public sealed class AgvMonitoringOptionsValidator : IValidateOptions<AgvMonitoringOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, AgvMonitoringOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var error = options.Validate();
        return error is null ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(error);
    }
}
