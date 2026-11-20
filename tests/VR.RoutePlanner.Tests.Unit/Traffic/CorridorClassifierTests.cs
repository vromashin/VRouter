using VR.RoutePlanner.Application.Configuration;
using VR.RoutePlanner.Application.Traffic;
using VR.RoutePlanner.Domain.Traffic;

namespace VR.RoutePlanner.Tests.Unit.Traffic;

/// <summary>
/// Проверки формулы Nmax и классификации коридоров.
/// </summary>
public sealed class CorridorClassifierTests
{
    [Theory]
    [InlineData(2.0, 0.8, 0.2, 2)]
    [InlineData(1.0, 0.8, 0.2, 1)]
    [InlineData(0.8, 0.8, 0.2, 1)]
    [InlineData(0.6, 0.8, 0.2, 1)]
    [InlineData(3.0, 0.8, 0.2, 3)]
    [InlineData(4.0, 0.8, 0.2, 4)]
    [InlineData(5.1, 0.8, 0.2, 5)]
    [InlineData(2.0, 1.0, 0.0, 2)]
    public void ComputeNmax_Formula_ReturnsExpected(
        double corridorWidth,
        double robotWidth,
        double sideMargin,
        int expected)
    {
        Assert.Equal(expected, CorridorClassifier.ComputeNmax(corridorWidth, robotWidth, sideMargin));
    }

    [Fact]
    public void ComputeNmax_NegativeWidth_ReturnsOne()
    {
        Assert.Equal(1, CorridorClassifier.ComputeNmax(-5, 0.8, 0.2));
    }

    [Fact]
    public void ComputeNmax_ZeroDenominator_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => CorridorClassifier.ComputeNmax(2.0, 0.0, 0.0));
    }

    [Theory]
    [InlineData(1, CorridorClass.C1)]
    [InlineData(2, CorridorClass.C2)]
    [InlineData(3, CorridorClass.C3)]
    [InlineData(4, CorridorClass.C4)]
    [InlineData(5, CorridorClass.C5)]
    [InlineData(9, CorridorClass.C5)]
    public void Classify_DefaultThresholds_ReturnsExpectedClass(int nmax, CorridorClass expected)
    {
        Assert.Equal(expected, CorridorClassifier.Classify(nmax, new CorridorClassThresholds()));
    }

    [Fact]
    public void Classify_CustomThresholds_UsesThem()
    {
        var thresholds = new CorridorClassThresholds { C1 = 1, C2 = 2, C3 = 3, C4 = 8, C5 = 9 };

        Assert.Equal(CorridorClass.C3, CorridorClassifier.Classify(3, thresholds));
        Assert.Equal(CorridorClass.C4, CorridorClassifier.Classify(7, thresholds));
        Assert.Equal(CorridorClass.C4, CorridorClassifier.Classify(8, thresholds));
        Assert.Equal(CorridorClass.C5, CorridorClassifier.Classify(9, thresholds));
        Assert.Equal(CorridorClass.C5, CorridorClassifier.Classify(10, thresholds));
    }

    [Fact]
    public void Classify_NmaxAboveAllThresholds_ReturnsC5()
    {
        var thresholds = new CorridorClassThresholds { C1 = 1, C2 = 2, C3 = 3, C4 = 4, C5 = 5 };

        Assert.Equal(CorridorClass.C5, CorridorClassifier.Classify(100, thresholds));
    }

    [Theory]
    [InlineData(CorridorClass.C1, TrafficResourceType.EXCLUSIVE)]
    [InlineData(CorridorClass.C2, TrafficResourceType.DIRECTIONAL)]
    [InlineData(CorridorClass.C3, TrafficResourceType.PARALLEL)]
    [InlineData(CorridorClass.C4, TrafficResourceType.PARALLEL)]
    [InlineData(CorridorClass.C5, TrafficResourceType.INDEPENDENT)]
    public void ResourceTypeOf_Class_ReturnsExpectedType(CorridorClass corridorClass, TrafficResourceType expected)
    {
        Assert.Equal(expected, CorridorClassifier.ResourceTypeOf(corridorClass));
    }

    [Fact]
    public void ResourceTypeOf_UnknownClass_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CorridorClassifier.ResourceTypeOf((CorridorClass)42));
    }
}
