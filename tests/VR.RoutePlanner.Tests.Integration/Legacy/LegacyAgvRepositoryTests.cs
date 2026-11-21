using Microsoft.EntityFrameworkCore;
using VR.RoutePlanner.Infrastructure.Legacy;
using VR.RoutePlanner.Tests.Integration.Support;
using Xunit;

namespace VR.RoutePlanner.Tests.Integration.Legacy;

[Collection(VrtWriteCollection.Name)]
public sealed class LegacyAgvRepositoryTests
{
    [SkippableFact]
    public async Task ListAsync_ReturnsRowFromLegacyAgvs()
    {
        var connection = TestDatabase.RequireConnection();
        var options = new DbContextOptionsBuilder<LegacyReadDbContext>().UseNpgsql(connection).Options;
        var id = Guid.NewGuid();
        const string serial = "test-legacy-01";
        await using var context = new LegacyReadDbContext(options);
        await context.Database.ExecuteSqlRawAsync(
            """INSERT INTO "Agvs" ("Id","Manufacturer","SerialNumber","AgvType","Kinematic","CurrentState","OperatingMode","BatteryCharge","IsCharging","IsPaused","Velocity","MaxSpeed","MaxLoadMass","HasLoad","EStopStatus","FieldViolation","ConnectionState","Driving","LastUpdateTime","CreatedDate","UpdatedDate") VALUES ({0},{1},{2},{3},{4},{5},{6},{7},{8},{9},{10},{11},{12},{13},{14},{15},{16},{17},now(),now(),now())""",
            id, "Tuskrobots", serial, "FORKLIFT", "DIFF", "IDLE", "AUTOMATIC",
            80.0, false, false, 0.0, 2.0, 1000.0, false, "NONE", false, "ONLINE", false);

        try
        {
            var repository = new LegacyAgvRepository(context);
            var rows = await repository.ListAsync(CancellationToken.None);
            var found = Assert.Single(rows, row => row.SerialNumber == serial);
            Assert.Equal("IDLE", found.CurrentState);
            var byId = await repository.FindAsync(id, CancellationToken.None);
            Assert.NotNull(byId);
            Assert.Equal(serial, byId!.SerialNumber);
        }
        finally
        {
            await context.Database.ExecuteSqlRawAsync("""DELETE FROM "Agvs" WHERE "Id" = {0}""", id);
        }
    }
}
