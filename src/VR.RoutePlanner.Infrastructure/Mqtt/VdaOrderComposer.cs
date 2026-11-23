using VR.RoutePlanner.Application.Robots;

namespace VR.RoutePlanner.Infrastructure.Mqtt;

/// <summary>
/// Сборка тела VDA-заказа через <see cref="VdaOrderBuilder"/>.
/// </summary>
public sealed class VdaOrderComposer : IVdaOrderComposer
{
    /// <inheritdoc />
    public string Compose(VdaOrderRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return VdaOrderBuilder.Serialize(VdaOrderBuilder.Build(request));
    }
}
