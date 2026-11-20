using VR.RoutePlanner.Infrastructure.Graph;
using VR.RoutePlanner.Infrastructure.Persistence;

namespace VR.RoutePlanner.Tests.Unit.Persistence;

/// <summary>
/// Проверки версии графа: она считается по бизнес-идентификаторам, а не по первичным ключам строк.
/// </summary>
public sealed class WarehouseGraphVersionTests
{
    [Fact]
    public void BuildGraphVersion_SameSetsInDifferentOrder_ReturnsSameValue()
    {
        var direct = WarehouseGraphRepository.BuildGraphVersion(
            "v1",
            new[] { "wa1", "wa2", "wa3" },
            new[] { "e1", "e2" });

        var reversed = WarehouseGraphRepository.BuildGraphVersion(
            "v1",
            new[] { "wa3", "wa1", "wa2" },
            new[] { "e2", "e1" });

        Assert.Equal(direct, reversed);
        Assert.StartsWith("v1-", direct, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildGraphVersion_DifferentNodeIds_ReturnsDifferentValue()
    {
        var first = WarehouseGraphRepository.BuildGraphVersion("v1", new[] { "wa1" }, new[] { "e1" });
        var second = WarehouseGraphRepository.BuildGraphVersion("v1", new[] { "wa2" }, new[] { "e1" });

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void BuildGraphVersion_DigitOrdering_IsOrdinalNotNumeric()
    {
        var asStrings = WarehouseGraphRepository.BuildGraphVersion("v1", new[] { "wa2", "wa10" }, Array.Empty<string>());
        var swapped = WarehouseGraphRepository.BuildGraphVersion("v1", new[] { "wa10", "wa2" }, Array.Empty<string>());

        Assert.Equal(asStrings, swapped);
        Assert.Matches("^v1-[0-9a-f]{16}$", asStrings);
    }

    [Fact]
    public void BuildGraphVersion_MapVersionIsPartOfValue()
    {
        var first = WarehouseGraphRepository.BuildGraphVersion("v1", new[] { "wa1" }, Array.Empty<string>());
        var second = WarehouseGraphRepository.BuildGraphVersion("v2", new[] { "wa1" }, Array.Empty<string>());

        Assert.NotEqual(first, second);
        Assert.EndsWith(first["v1".Length..], second, StringComparison.Ordinal);
    }
}
