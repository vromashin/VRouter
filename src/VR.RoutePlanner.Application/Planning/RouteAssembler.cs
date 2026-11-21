using System.Globalization;
using VR.RoutePlanner.Application.Configuration;
using VR.RoutePlanner.Domain.Planning;

namespace VR.RoutePlanner.Application.Planning;

/// <summary>
/// Собирает <see cref="PlannedRoute"/> и разложение цены из найденного пути.
/// </summary>
internal sealed class RouteAssembler
{
    private readonly EdgeCostCalculator _costs;
    private readonly CostOptions _costOptions;

    /// <summary>
    /// Создает сборщик маршрута.
    /// </summary>
    /// <param name="costs">Функция стоимости.</param>
    /// <param name="costOptions">Параметры <c>Routing:Cost</c> для текста формулы.</param>
    public RouteAssembler(EdgeCostCalculator costs, CostOptions costOptions)
    {
        ArgumentNullException.ThrowIfNull(costs);
        ArgumentNullException.ThrowIfNull(costOptions);

        _costs = costs;
        _costOptions = costOptions;
    }

    /// <summary>
    /// Собирает маршрут и разложение цены.
    /// </summary>
    /// <param name="outcome">Найденный путь.</param>
    /// <param name="reason">Код причины либо <c>null</c>.</param>
    /// <param name="predictedRisk">Признак риска по узкому коридору.</param>
    /// <param name="resources">
    /// Ресурсы ребер выбранного маршрута по паре «ребро, направление».
    /// </param>
    /// <returns>Маршрут и разложение цены.</returns>
    public (PlannedRoute Route, RouteExplanation Explanation) Build(
        SearchOutcome outcome,
        string? reason,
        bool predictedRisk,
        IReadOnlyDictionary<(Guid EdgeId, RouteEdgeDirection Direction), Guid> resources)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        ArgumentNullException.ThrowIfNull(resources);

        var segments = new List<RouteSegment>(outcome.Path.Count);
        var explanations = new List<SegmentExplanation>(outcome.Path.Count);

        var cumulative = 0.0;
        var travelSeconds = 0.0;
        var turnSeconds = 0.0;
        var clearanceSeconds = 0.0;
        var lengthSeconds = 0.0;
        var waitSeconds = 0.0;
        var totalLength = 0.0;
        var minClearance = double.PositiveInfinity;
        var turnCount = 0;

        EdgeTraversal? previous = null;
        foreach (var traversal in outcome.Path)
        {
            var degrees = previous is null
                ? 0
                : EdgeGeometry.TurnDegrees(
                    previous.Edge,
                    previous.Direction,
                    traversal.Edge,
                    traversal.Direction);

            var turn = _costs.TurnCost(degrees);
            if (degrees > EdgeCostCalculator.TurnThresholdDegrees)
            {
                turnCount++;
            }

            var breakdown = traversal.Breakdown;
            var cost = traversal.BaseCost + turn + traversal.WaitSeconds;
            cumulative += cost;

            segments.Add(new RouteSegment(
                traversal.Edge.EdgeId,
                traversal.Direction,
                breakdown.LengthMeters,
                breakdown.SpeedMetersPerSecond,
                breakdown.ClearanceMeters,
                cost,
                cumulative,
                _costs.IsNarrow(breakdown.ClearanceMeters),
                resources[(traversal.Edge.Id, traversal.Direction)],
                true));

            explanations.Add(new SegmentExplanation(
                traversal.Edge.EdgeId,
                degrees,
                breakdown.TravelSeconds,
                turn,
                breakdown.ClearanceSeconds,
                breakdown.LengthWeightSeconds,
                traversal.WaitSeconds));

            travelSeconds += breakdown.TravelSeconds;
            turnSeconds += turn;
            clearanceSeconds += breakdown.ClearanceSeconds;
            lengthSeconds += breakdown.LengthWeightSeconds;
            waitSeconds += traversal.WaitSeconds;
            totalLength += breakdown.LengthMeters;
            minClearance = Math.Min(minClearance, breakdown.ClearanceMeters);

            previous = traversal;
        }

        if (double.IsPositiveInfinity(minClearance))
        {
            minClearance = 0;
        }

        var totalCost = cumulative;
        var totalSeconds = travelSeconds + turnSeconds + waitSeconds;

        var route = new PlannedRoute(
            segments,
            totalLength,
            totalSeconds,
            totalCost,
            turnCount,
            minClearance,
            predictedRisk,
            reason);

        var explanation = new RouteExplanation(
            explanations,
            travelSeconds,
            turnSeconds,
            clearanceSeconds,
            lengthSeconds,
            waitSeconds,
            Formula);

        return (route, explanation);
    }

    private string Formula => string.Create(
        CultureInfo.InvariantCulture,
        $"cost = length / speed + {_costOptions.ClearanceWeight} * "
        + $"({_costOptions.ClearanceFloorMeters} / clearance)^2 + {_costOptions.LengthWeight} * length "
        + $"+ turn({_costOptions.TurnPenaltySeconds} * angle / 90, max "
        + $"{CostOptions.MaxTurnPenalties * _costOptions.TurnPenaltySeconds}) + wait");
}
