using Microsoft.EntityFrameworkCore;
using VR.RoutePlanner.Infrastructure.Persistence;
using VR.RoutePlanner.Tests.Integration.Support;

namespace VR.RoutePlanner.Tests.Integration.Persistence;

/// <summary>
/// Проверки формы выборок графа: порядок обхода не должен зависеть от базы.
/// </summary>
public sealed class WarehouseGraphQueryTests
{
    [Fact]
    public void ActiveNodesQuery_OrdersByNodeIdThenId()
    {
        using var context = TestDatabase.CreateOfflineContext();

        var sql = WarehouseGraphRepository.ActiveNodesQuery(context, Guid.Empty).ToQueryString();

        AssertOrderByContains(sql, "\"NodeId\"");
        AssertOrderByContains(sql, "\"Id\"");
    }

    [Fact]
    public void ActiveEdgesQuery_OrdersByEdgeIdThenId()
    {
        using var context = TestDatabase.CreateOfflineContext();

        var sql = WarehouseGraphRepository.ActiveEdgesQuery(context, Guid.Empty).ToQueryString();

        AssertOrderByContains(sql, "\"EdgeId\"");
        AssertOrderByContains(sql, "\"Id\"");
    }

    [Fact]
    public void ActiveMapsQuery_OrdersByMapVersion()
    {
        using var context = TestDatabase.CreateOfflineContext();

        var sql = WarehouseGraphRepository.ActiveMapsQuery(context, "801").ToQueryString();

        AssertOrderByContains(sql, "\"MapVersion\"");
    }

    private static void AssertOrderByContains(string sql, string column)
    {
        var orderByIndex = sql.IndexOf("ORDER BY", StringComparison.Ordinal);
        Assert.True(orderByIndex >= 0, $"В запросе нет ORDER BY: {sql}");

        var orderBy = sql[orderByIndex..];
        Assert.True(
            orderBy.Contains(column, StringComparison.Ordinal),
            $"В ORDER BY нет {column}: {orderBy}");
    }
}
