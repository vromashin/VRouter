using VR.RoutePlanner.Application.Robots;

namespace VR.RoutePlanner.Infrastructure.Mqtt;

/// <summary>
/// Идентификаторы сообщений из таймера: делегирует <see cref="VdaOrderPublisher.NextHeaderId"/>.
/// </summary>
public sealed class TimerHeaderIdSource : IHeaderIdSource
{
    /// <inheritdoc />
    public long Next() => VdaOrderPublisher.NextHeaderId();
}
