using VR.RoutePlanner.Application.Configuration;
using VR.RoutePlanner.Domain.Graph;
using VR.RoutePlanner.Tests.Unit.Support;

namespace VR.RoutePlanner.Tests.Unit.Configuration;

/// <summary>
/// Проверки валидации параметров Routing.
/// </summary>
public sealed class RoutePlannerOptionsTests
{
    [Fact]
    public void Validate_Defaults_ReturnsProfileError()
    {
        // Профили задаются конфигурацией: без них параметры неполные.
        Assert.NotNull(new RoutePlannerOptions().Validate());
    }

    [Fact]
    public void Validate_DefaultAgvProfileMissing_ReturnsError()
    {
        var options = new RoutePlannerOptions
        {
            AgvProfiles = TestProfiles.Create(),
            DefaultAgvProfile = "no-such-profile",
        };

        var error = options.Validate();

        Assert.NotNull(error);
        Assert.Contains("DefaultAgvProfile", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-0.5)]
    public void Validate_NonPositiveProfileWidth_ReturnsError(double width)
    {
        var options = new RoutePlannerOptions { AgvProfiles = TestProfiles.Create(width), DefaultAgvProfile = TestProfiles.Name };

        Assert.NotNull(options.Validate());
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.455)]
    public void Validate_NonPositiveProfileLength_ReturnsError(double length)
    {
        var profiles = TestProfiles.Create();
        profiles[TestProfiles.Name].LengthMeters = length;
        var options = new RoutePlannerOptions { AgvProfiles = profiles, DefaultAgvProfile = TestProfiles.Name };

        var error = options.Validate();

        Assert.NotNull(error);
        Assert.Contains("LengthMeters", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-0.17)]
    public void Validate_NonPositiveProfileHeight_ReturnsError(double height)
    {
        var profiles = TestProfiles.Create();
        profiles[TestProfiles.Name].HeightMeters = height;
        var options = new RoutePlannerOptions { AgvProfiles = profiles, DefaultAgvProfile = TestProfiles.Name };

        var error = options.Validate();

        Assert.NotNull(error);
        Assert.Contains("HeightMeters", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_LengthShorterThanWidth_ReturnsError()
    {
        var profiles = TestProfiles.Create();
        profiles[TestProfiles.Name].LengthMeters = 0.5;
        var options = new RoutePlannerOptions { AgvProfiles = profiles, DefaultAgvProfile = TestProfiles.Name };

        var error = options.Validate();

        Assert.NotNull(error);
        Assert.Contains("LengthMeters", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_NegativeSideMargin_ReturnsError()    {
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
            AgvProfiles = TestProfiles.Create(),
            DefaultAgvProfile = TestProfiles.Name,
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
        var options = new RoutePlannerOptions
        {
            AgvProfiles = TestProfiles.Create(),
            DefaultAgvProfile = TestProfiles.Name,
            TrajectorySampleStep = step,
        };

        Assert.NotNull(options.Validate());
    }

    [Fact]
    public void Validate_NegativeInflationRadius_ReturnsError()
    {
        var options = new RoutePlannerOptions
        {
            AgvProfiles = TestProfiles.Create(),
            DefaultAgvProfile = TestProfiles.Name,
            InflationRadius = -0.1,
        };

        Assert.NotNull(options.Validate());
    }

    [Fact]
    public void Profiles_TuskrobotsMaximums_ProduceFootprint()
    {
        var profiles = new Dictionary<string, AgvProfileOptions>(StringComparer.Ordinal)
        {
            ["tusk-forklift-1102"] = new()
            {
                Name = "tusk-forklift-1102",
                WidthMeters = 1.102,
                LengthMeters = 1.455,
                HeightMeters = 0.17,
            },
        };
        var options = new RoutePlannerOptions
        {
            AgvProfiles = profiles,
            DefaultAgvProfile = "tusk-forklift-1102",
        };

        Assert.Null(options.Validate());
        var profile = new AgvProfile("tusk-forklift-1102", 1.102, 1.455, 0.17);
        Assert.Equal(0.9126, profile.FootprintRadius, 1e-4);
    }

    [Fact]
    public void Validate_UnknownDefaultProfile_ReturnsError()
    {
        var options = new RoutePlannerOptions
        {
            AgvProfiles = TestProfiles.Create(),
            DefaultAgvProfile = "missing",
        };

        var error = options.Validate();

        Assert.NotNull(error);
        Assert.Contains("DefaultAgvProfile", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_EmptyProfiles_ReturnsError()
    {
        var options = new RoutePlannerOptions { AgvProfiles = new Dictionary<string, AgvProfileOptions>(), DefaultAgvProfile = "x" };

        var error = options.Validate();

        Assert.NotNull(error);
        Assert.Contains("AgvProfiles", error, StringComparison.Ordinal);
    }
}
