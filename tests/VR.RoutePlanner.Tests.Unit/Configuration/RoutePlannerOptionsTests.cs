using VR.RoutePlanner.Application.Configuration;

namespace VR.RoutePlanner.Tests.Unit.Configuration;

/// <summary>
/// Проверки валидации параметров Routing.
/// </summary>
public sealed class RoutePlannerOptionsTests
{
    [Fact]
    public void Validate_Defaults_ReturnsNull()
    {
        Assert.Null(new RoutePlannerOptions().Validate());
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-0.5)]
    public void Validate_NonPositiveRobotWidth_ReturnsError(double robotWidth)
    {
        var options = new RoutePlannerOptions { RobotWidth = robotWidth };

        Assert.NotNull(options.Validate());
    }

    [Fact]
    public void Validate_NegativeSideMargin_ReturnsError()
    {
        var options = new RoutePlannerOptions { SideMargin = -0.1 };

        Assert.NotNull(options.Validate());
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(256)]
    public void Validate_ObstacleThresholdOutOfRange_ReturnsError(int threshold)
    {
        var options = new RoutePlannerOptions { ObstacleThreshold = threshold };

        Assert.NotNull(options.Validate());
    }

    [Fact]
    public void Validate_NonIncreasingThresholds_ReturnsError()
    {
        var options = new RoutePlannerOptions
        {
            CorridorClassThresholds = new CorridorClassThresholds { C1 = 2, C2 = 2, C3 = 3, C4 = 4, C5 = 5 },
        };

        var error = options.Validate();

        Assert.NotNull(error);
        Assert.Contains("CorridorClassThresholds", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-0.05)]
    public void Validate_NonPositiveSampleStep_ReturnsError(double step)
    {
        var options = new RoutePlannerOptions { TrajectorySampleStep = step };

        Assert.NotNull(options.Validate());
    }

    [Fact]
    public void Validate_NonPositiveInflationRadius_ReturnsError()
    {
        var options = new RoutePlannerOptions { InflationRadius = 0 };

        Assert.NotNull(options.Validate());
    }
}
