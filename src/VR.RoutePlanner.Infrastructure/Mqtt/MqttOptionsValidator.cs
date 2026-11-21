using Microsoft.Extensions.Options;

namespace VR.RoutePlanner.Infrastructure.Mqtt;

/// <summary>
/// Проверка параметров <c>Routing:Mqtt</c> на старте сервиса.
/// </summary>
public sealed class MqttOptionsValidator : IValidateOptions<MqttOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, MqttOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var error = options.Validate();
        return error is null ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(error);
    }
}
