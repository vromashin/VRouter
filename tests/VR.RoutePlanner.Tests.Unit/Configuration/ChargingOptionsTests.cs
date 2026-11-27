using VR.RoutePlanner.Application.Configuration;

namespace VR.RoutePlanner.Tests.Unit.Configuration;

/// <summary>
/// Проверки параметров Routing:Charging: дефолты, границы и тексты ошибок.
/// </summary>
public sealed class ChargingOptionsTests
{
    [Fact]
    public void Defaults_MatchSpecification()
    {
        var options = new ChargingOptions();

        Assert.False(options.Enabled);
        Assert.Equal("801", options.MapId);
        Assert.Equal(20, options.LowerLimit);
        Assert.Equal(80, options.TargetLimit);
        Assert.Equal(5, options.WaitingMinutes);
        Assert.Equal(5, options.PollSeconds);
        Assert.Equal(60, options.RetryDelaySeconds);
        Assert.Equal(60, options.MaxAttempts);
        Assert.Equal(0.5, options.IdleNodeToleranceMeters);
        Assert.Equal(0.75, options.OccupiedRadiusMeters);
        Assert.Equal("20373", options.ChargerId);
        Assert.True(options.PublishChargingCompleted);
        Assert.Equal("agv-charging-completed", options.ChargingCompletedTopic);
        Assert.False(options.AutoStopCharging);
        Assert.Equal("Routing:Charging", ChargingOptions.SectionName);
    }

    [Fact]
    public void Validate_Defaults_ReturnsNull()
    {
        Assert.Null(new ChargingOptions().Validate());
    }

    [Theory]
    [InlineData(80.0)]
    [InlineData(90.0)]
    public void Validate_LowerLimitNotBelowTargetLimit_ReturnsError(double lowerLimit)
    {
        var options = new ChargingOptions { LowerLimit = lowerLimit };

        var error = options.Validate();

        Assert.NotNull(error);
        Assert.Contains("Routing:Charging:LowerLimit", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-5.0)]
    public void Validate_NonPositiveLowerLimit_ReturnsError(double lowerLimit)
    {
        var options = new ChargingOptions { LowerLimit = lowerLimit };

        var error = options.Validate();

        Assert.NotNull(error);
        Assert.Contains("Routing:Charging:LowerLimit", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_TargetLimitAboveHundred_ReturnsError()
    {
        var options = new ChargingOptions { TargetLimit = 101 };

        var error = options.Validate();

        Assert.NotNull(error);
        Assert.Contains("Routing:Charging:TargetLimit", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    public void Validate_NonPositiveWaitingMinutes_ReturnsError(double waitingMinutes)
    {
        var options = new ChargingOptions { WaitingMinutes = waitingMinutes };

        var error = options.Validate();

        Assert.NotNull(error);
        Assert.Contains("Routing:Charging:WaitingMinutes", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    public void Validate_NonPositivePollSeconds_ReturnsError(double pollSeconds)
    {
        var options = new ChargingOptions { PollSeconds = pollSeconds };

        var error = options.Validate();

        Assert.NotNull(error);
        Assert.Contains("Routing:Charging:PollSeconds", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_NegativeRetryDelay_ReturnsError()
    {
        var options = new ChargingOptions { RetryDelaySeconds = -1 };

        var error = options.Validate();

        Assert.NotNull(error);
        Assert.Contains("Routing:Charging:RetryDelaySeconds", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_ZeroRetryDelay_Passes()
    {
        var options = new ChargingOptions { RetryDelaySeconds = 0 };

        Assert.Null(options.Validate());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void Validate_NonPositiveMaxAttempts_ReturnsError(int maxAttempts)
    {
        var options = new ChargingOptions { MaxAttempts = maxAttempts };

        var error = options.Validate();

        Assert.NotNull(error);
        Assert.Contains("Routing:Charging:MaxAttempts", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-0.5)]
    public void Validate_NonPositiveIdleNodeTolerance_ReturnsError(double tolerance)
    {
        var options = new ChargingOptions { IdleNodeToleranceMeters = tolerance };

        var error = options.Validate();

        Assert.NotNull(error);
        Assert.Contains("Routing:Charging:IdleNodeToleranceMeters", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-0.75)]
    public void Validate_NonPositiveOccupiedRadius_ReturnsError(double radius)
    {
        var options = new ChargingOptions { OccupiedRadiusMeters = radius };

        var error = options.Validate();

        Assert.NotNull(error);
        Assert.Contains("Routing:Charging:OccupiedRadiusMeters", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_EmptyMapId_ReturnsError()
    {
        var options = new ChargingOptions { MapId = "  " };

        var error = options.Validate();

        Assert.NotNull(error);
        Assert.Contains("Routing:Charging:MapId", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_EmptyTopicWhilePublishing_ReturnsError()
    {
        var options = new ChargingOptions { ChargingCompletedTopic = string.Empty };

        var error = options.Validate();

        Assert.NotNull(error);
        Assert.Contains("Routing:Charging:ChargingCompletedTopic", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_EmptyTopicWithoutPublishing_Passes()
    {
        var options = new ChargingOptions
        {
            ChargingCompletedTopic = string.Empty,
            PublishChargingCompleted = false,
        };

        Assert.Null(options.Validate());
    }

    [Fact]
    public void Validator_InvalidOptions_ReturnsFailure()
    {
        var result = new ChargingOptionsValidator().Validate(null, new ChargingOptions { MaxAttempts = 0 });

        Assert.False(result.Succeeded);
        Assert.Contains("Routing:Charging:MaxAttempts", result.FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Validator_ValidOptions_ReturnsSuccess()
    {
        var result = new ChargingOptionsValidator().Validate(null, new ChargingOptions());

        Assert.True(result.Succeeded);
    }
}
