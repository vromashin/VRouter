using VR.RoutePlanner.Application.Robots;

namespace VR.RoutePlanner.Infrastructure.Legacy;

/// <summary>
/// Пишет живое состояние робота в <c>public."Agvs"</c>, чтобы админка и карта видели движение.
/// Включается флагом <c>Legacy:WriteAgvsEnabled</c> и только при остановленном старом мастере.
/// </summary>
public interface ILegacyAgvStateWriter
{
    Task ApplyAsync(RobotSnapshot snapshot, string? orderId, CancellationToken ct);
}
