namespace VR.RoutePlanner.Infrastructure.Legacy;

/// <summary>Чтение списка роботов из legacy-таблицы <c>Agvs</c>.</summary>
public interface ILegacyAgvRepository
{
    Task<IReadOnlyList<LegacyAgvRecord>> ListAsync(CancellationToken ct);

    Task<LegacyAgvRecord?> FindAsync(Guid id, CancellationToken ct);
}
