using Microsoft.EntityFrameworkCore;
using VR.RoutePlanner.Infrastructure.Persistence;
using VR.RoutePlanner.Tests.Integration.Support;
using Xunit;

namespace VR.RoutePlanner.Tests.Integration.Persistence;

public sealed class ModelSchemaTests
{
    [SkippableFact]
    public async Task Model_MatchesInformationSchema()
    {
        var connection = TestDatabase.RequireConnection();
        await using var context = new RoutePlannerDbContext(TestDatabase.BuildOptions(connection));
        await context.Database.MigrateAsync();

        await SchemaComparison.AssertColumnsAsync(context);
        await SchemaComparison.AssertIndexesAsync(context);
    }
}
