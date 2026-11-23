using VR.RoutePlanner.Application.Robots;

namespace VR.RoutePlanner.Application.Traffic;

/// <summary>
/// Заглушка диспетчера трафика: ничего не резервирует и всегда отвечает «свободно».
///
/// Регистрируется в <c>AddInfrastructure</c>, поэтому ее получает <c>VR.RoutePlanner.Api</c>
/// со своей пустой памятью. Владельцем трафика объявлен только <c>VR.RoutePlanner.Workers</c>:
/// его <c>Program.cs</c> перекрывает регистрацию настоящим <see cref="TrafficCoordinator"/>.
/// </summary>
public sealed class NoOpTrafficCoordinator : ITrafficCoordinator
{
    /// <inheritdoc />
    public Task<TrafficReservationResult> TryReserveForOrderAsync(
        RobotSnapshot snapshot,
        string? planJson,
        CancellationToken ct) =>
        Task.FromResult(TrafficReservationResult.Accept());

    /// <inheritdoc />
    public Task<TrafficReservationResult> OnStateAsync(
        RobotSnapshot snapshot,
        string? planJson,
        CancellationToken ct) =>
        Task.FromResult(TrafficReservationResult.Accept());

    /// <inheritdoc />
    public Task<int> ReleaseAsync(string agvSerialNumber, CancellationToken ct) => Task.FromResult(0);

    /// <inheritdoc />
    public Task<int> SweepAsync(DateTimeOffset now, CancellationToken ct) => Task.FromResult(0);
}
