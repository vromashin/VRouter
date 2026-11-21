namespace VR.RoutePlanner.Application.Planning;

/// <summary>
/// Оценка ожидания этапа 2: резервирований еще нет, всегда ноль.
/// </summary>
public sealed class ZeroWaitEstimator : IWaitEstimator
{
    /// <inheritdoc />
    public ValueTask<double> EstimateAsync(
        RoutePlanRequest request,
        Guid edgeId,
        CancellationToken ct) => ValueTask.FromResult(0.0);
}
