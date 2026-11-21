using VR.RoutePlanner.Application.Configuration;
using VR.RoutePlanner.Application.Planning;
using Xunit;

namespace VR.RoutePlanner.Tests.Unit.Planning;

/// <summary>
/// Тесты 1-3: формула цены ребра и цена поворота.
/// </summary>
public sealed class CostFormulaTests
{
    private const double ProfileWidth = 0.5;
    private const double Epsilon = 1e-9;

    [Theory]
    [InlineData(1.3, 2.0, 0.8, 1.2)]
    [InlineData(0.7, 1.0, 0.4, 0.6)]
    [InlineData(1.0, 3.0, 0.5, 0.6)]
    [InlineData(2.0, 10.0, 1.5, 2.0)]
    [InlineData(1.2, 4.0, 0.7, 1.2)]
    public void Calculate_MatchesFormula(double width, double length, double clearance, double speed)
    {
        var cost = new CostOptions();
        var calculator = new EdgeCostCalculator(cost);

        var breakdown = calculator.Calculate(length, width, ProfileWidth, hasLoad: false);

        Assert.Equal(clearance, breakdown.ClearanceMeters, 9);
        Assert.Equal(speed, breakdown.SpeedMetersPerSecond, 9);
        Assert.Equal(length / speed, breakdown.TravelSeconds, 9);
        Assert.Equal(cost.ClearanceWeight * Math.Pow(cost.ClearanceFloorMeters / clearance, 2), breakdown.ClearanceSeconds, 9);
        Assert.Equal(cost.LengthWeight * length, breakdown.LengthWeightSeconds, 9);
    }

    [Fact]
    public void TurnCost_NinetyDegrees_AddsFullShare()
    {
        var cost = new CostOptions();
        var calculator = new EdgeCostCalculator(cost);

        Assert.Equal(cost.TurnPenaltySeconds, calculator.TurnCost(90), 9);
        Assert.Equal(cost.TurnPenaltySeconds * 10 / 90, calculator.TurnCost(10), 9);
        Assert.True(calculator.TurnCost(10) < calculator.TurnCost(90));
        Assert.Equal(2 * cost.TurnPenaltySeconds, calculator.TurnCost(180), 9);
        Assert.True(calculator.TurnCost(180) < 10 * cost.TurnPenaltySeconds);
    }

    [Fact]
    public void TurnCost_BelowThreshold_IsZero()
    {
        var calculator = new EdgeCostCalculator(new CostOptions());

        Assert.Equal(0, calculator.TurnCost(0));
        Assert.Equal(0, calculator.TurnCost(EdgeCostCalculator.TurnThresholdDegrees));
        Assert.True(calculator.TurnCost(EdgeCostCalculator.TurnThresholdDegrees + 0.1) > 0);
    }

    [Fact]
    public void ClearanceCost_HalvingClearance_QuadruplesCost()
    {
        var calculator = new EdgeCostCalculator(new CostOptions());

        var wide = calculator.Calculate(1.0, 1.3, ProfileWidth, hasLoad: false);
        var narrow = calculator.Calculate(1.0, 0.9, ProfileWidth, hasLoad: false);

        Assert.Equal(0.8, wide.ClearanceMeters, 9);
        Assert.Equal(0.4, narrow.ClearanceMeters, 9);
        Assert.Equal(4 * wide.ClearanceSeconds, narrow.ClearanceSeconds, 9);
    }

    [Fact]
    public void Calculate_WithLoad_LowersSpeed()
    {
        var cost = new CostOptions();
        var calculator = new EdgeCostCalculator(cost);

        var empty = calculator.Calculate(10.0, 2.0, ProfileWidth, hasLoad: false);
        var loaded = calculator.Calculate(10.0, 2.0, ProfileWidth, hasLoad: true);

        Assert.Equal(empty.SpeedMetersPerSecond * cost.LoadSpeedFactor, loaded.SpeedMetersPerSecond, 9);
        Assert.True(loaded.TravelSeconds > empty.TravelSeconds);
    }

    [Fact]
    public void Defaults_MatchSpec()
    {
        var cost = new CostOptions();

        Assert.Equal(2.0, cost.TurnPenaltySeconds);
        Assert.Equal(5.0, cost.ClearanceWeight);
        Assert.Equal(0.4, cost.ClearanceFloorMeters);
        Assert.Equal(0.0, cost.LengthWeight);
        Assert.Equal(0.6, cost.NarrowClearanceMeters);
        Assert.Equal(1.0, cost.SafeClearanceMeters);
        Assert.Equal(2.0, cost.SafetyOverrideRatio);
        Assert.Equal(0.7, cost.LoadSpeedFactor);
        Assert.Equal(2.0, cost.ProfileTopSpeedMetersPerSecond);
        Assert.Equal(3, cost.SpeedBands.Count);
        Assert.Null(cost.Validate());
    }

    [Fact]
    public void Defaults_NarrowClearanceReachable()
    {
        var cost = new CostOptions();
        var costs = new EdgeCostCalculator(cost);

        // Ширина коридора 1.65 м при профиле 1.102 м: запас 0.548 м — выше пола и ниже порога тесноты.
        var clearance = Math.Max(1.65 - 1.102, cost.ClearanceFloorMeters);

        Assert.True(clearance > cost.ClearanceFloorMeters);
        Assert.True(clearance < cost.NarrowClearanceMeters);
        Assert.True(costs.IsNarrow(clearance), "при дефолтах правило тесноты должно срабатывать");
    }

    [Fact]
    public void Validation_RejectsNonIncreasingClearance()
    {
        var equalFloorAndNarrow = new CostOptions { ClearanceFloorMeters = 0.6, NarrowClearanceMeters = 0.6 };
        Assert.NotNull(equalFloorAndNarrow.Validate());

        var equalNarrowAndSafe = new CostOptions { NarrowClearanceMeters = 1.0, SafeClearanceMeters = 1.0 };
        Assert.NotNull(equalNarrowAndSafe.Validate());

        var increasing = new CostOptions { ClearanceFloorMeters = 0.4, NarrowClearanceMeters = 0.6, SafeClearanceMeters = 1.0 };
        Assert.Null(increasing.Validate());
    }
}
