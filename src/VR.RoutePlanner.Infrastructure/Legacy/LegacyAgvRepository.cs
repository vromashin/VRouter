using Microsoft.EntityFrameworkCore;

namespace VR.RoutePlanner.Infrastructure.Legacy;

public sealed class LegacyAgvRepository(LegacyReadDbContext context) : ILegacyAgvRepository
{
    public async Task<IReadOnlyList<LegacyAgvRecord>> ListAsync(CancellationToken ct) =>
        await context.Agvs.AsNoTracking().OrderBy(record => record.SerialNumber).ToListAsync(ct);

    public Task<LegacyAgvRecord?> FindAsync(Guid id, CancellationToken ct) =>
        context.Agvs.AsNoTracking().FirstOrDefaultAsync(record => record.Id == id, ct);
}
