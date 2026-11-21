using VR.RoutePlanner.Application.Configuration;
using VR.RoutePlanner.Application.Traffic;
using VR.RoutePlanner.Domain.Graph;
using VR.RoutePlanner.Domain.Traffic;

namespace VR.RoutePlanner.Tests.Unit.Traffic;

/// <summary>
/// Физический минимум проезда: коридор уже робота плюс два зазора
/// <see cref="RoutePlannerOptions.MinPhysicalClearanceMeters"/> закрыт, более широкий
/// проезд разрешен независимо от желаемого <see cref="RoutePlannerOptions.SideMargin"/>.
/// </summary>
public sealed class PhysicalClearanceTests
{
    private static readonly AgvProfile Forklift = new("tusk-forklift-1102", 1.102, 1.455, 0.17);

    [Theory]
    [InlineData(1.302, true)]
    [InlineData(1.303, true)]
    [InlineData(1.301, false)]
    [InlineData(1.100, false)]
    [InlineData(1.500, true)]
    public void IsPassable_PhysicalMinimum_IsTheOnlyHardLimit(double width, bool expected)
    {
        Assert.Equal(expected, CorridorClassifier.IsPassable(width, Forklift, 0.1));
    }

    [Fact]
    public void IsPassable_TightButFits_StaysPassableEvenBelowSideMargin()
    {
        // 1.35 м: робот 1.102 проходит, желаемый зазор 0.2 м (1.502) не достигается.
        Assert.True(CorridorClassifier.IsPassable(1.35, Forklift, 0.1));
        Assert.False(CorridorClassifier.IsPassable(1.35, Forklift, 0.2));
    }

    [Fact]
    public void ComputeNmax_TightCorridor_StillGivesSingleRobotCapacity()
    {
        var nmax = CorridorClassifier.ComputeNmax(1.35, Forklift.WidthMeters, 0.2);

        Assert.Equal(1, Math.Max(1, nmax));
        Assert.Equal(CorridorClass.C1, CorridorClassifier.Classify(nmax, new CorridorClassThresholds()));
    }

    [Theory]
    [InlineData(0.1, true)]
    [InlineData(0.05, true)]
    [InlineData(0.2, false)]
    [InlineData(0.0, false)]
    [InlineData(-0.1, false)]
    public void Validator_PhysicalClearance_MustBePositiveAndBelowSideMargin(double value, bool valid)
    {
        var options = new RoutePlannerOptions
        {
            MinPhysicalClearanceMeters = value,
            SideMargin = 0.2,
            AgvProfiles = new Dictionary<string, AgvProfileOptions>
            {
                [Forklift.Name] = new() { WidthMeters = 1.102, LengthMeters = 1.455, HeightMeters = 0.17 },
            },
            DefaultAgvProfile = Forklift.Name,
        };

        var result = new RoutePlannerOptionsValidator().Validate(null, options);

        Assert.Equal(valid, result.Succeeded);
    }
}
