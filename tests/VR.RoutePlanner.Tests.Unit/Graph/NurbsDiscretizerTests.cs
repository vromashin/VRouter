using VR.RoutePlanner.Application.Graph;
using VR.RoutePlanner.Domain.Graph;

namespace VR.RoutePlanner.Tests.Unit.Graph;

/// <summary>
/// Проверки дискретизации NURBS-кривой и ее длины.
/// </summary>
public sealed class NurbsDiscretizerTests
{
    [Fact]
    public void Discretize_StraightCubicCurve_KeepsStepAndEndpoints()
    {
        var curve = StraightCubic();

        var points = NurbsDiscretizer.Discretize(curve, 0.05);

        Assert.True(points.Count > 2);
        Assert.Equal(0.0, points[0].X, 1e-9);
        Assert.Equal(0.0, points[0].Y, 1e-9);
        Assert.Equal(1.0, points[^1].X, 1e-9);
        Assert.Equal(0.0, points[^1].Y, 1e-9);

        for (var i = 1; i < points.Count; i++)
        {
            var dx = points[i].X - points[i - 1].X;
            var dy = points[i].Y - points[i - 1].Y;
            Assert.True(Math.Sqrt((dx * dx) + (dy * dy)) <= 0.05 + 1e-9);
            Assert.Equal(0.0, points[i].Y, 1e-9);
        }
    }

    [Fact]
    public void Discretize_StraightCubicCurve_KeepsMonotoneX()
    {
        var points = NurbsDiscretizer.Discretize(StraightCubic(), 0.1);

        for (var i = 1; i < points.Count; i++)
        {
            Assert.True(points[i].X >= points[i - 1].X);
        }
    }

    [Fact]
    public void PolylineLength_StraightCurve_IsCloseToOneMeter()
    {
        var points = NurbsDiscretizer.Discretize(StraightCubic(), 0.01);

        var length = NurbsDiscretizer.PolylineLength(points);

        Assert.InRange(length, 0.999, 1.001);
    }

    [Fact]
    public void PolylineLength_TwoPoints_ReturnsEuclideanDistance()
    {
        var points = new[]
        {
            new MapPoint(0, 0, null),
            new MapPoint(3, 4, null),
        };

        Assert.Equal(5.0, NurbsDiscretizer.PolylineLength(points), 1e-12);
    }

    [Fact]
    public void PolylineLength_SinglePoint_ReturnsZero()
    {
        Assert.Equal(0.0, NurbsDiscretizer.PolylineLength(new[] { new MapPoint(1, 1, null) }));
    }

    [Fact]
    public void Discretize_NullCurve_ReturnsEmpty()
    {
        Assert.Empty(NurbsDiscretizer.Discretize(null, 0.05));
    }

    [Fact]
    public void Discretize_DegreeOne_ReturnsPolylineThroughAllControlPoints()
    {
        var curve = new TrajectoryCurve(
            Degree: 1,
            KnotVector: new[] { 0.0, 0.0, 0.5, 1.0, 1.0 },
            ControlPoints: new[]
            {
                new ControlPoint(0, 0, 1),
                new ControlPoint(1, 0, 1),
                new ControlPoint(1, 1, 1),
            },
            Raw: null);

        var points = NurbsDiscretizer.Discretize(curve, 0.25);

        Assert.True(points.Count > 3);
        foreach (var control in curve.ControlPoints)
        {
            Assert.Contains(
                points,
                point => Math.Abs(point.X - control.X) < 1e-9 && Math.Abs(point.Y - control.Y) < 1e-9);
        }

        Assert.Equal(2.0, NurbsDiscretizer.PolylineLength(points), 1e-9);
    }

    [Fact]
    public void Discretize_DegreeOneTwoPoints_ReturnsSegmentSplitByStep()
    {
        var curve = new TrajectoryCurve(
            Degree: 1,
            KnotVector: new[] { 0.0, 0.0, 1.0, 1.0 },
            ControlPoints: new[]
            {
                new ControlPoint(0, 0, 1),
                new ControlPoint(2, 0, 1),
            },
            Raw: null);

        var points = NurbsDiscretizer.Discretize(curve, 0.5);

        Assert.Equal(5, points.Count);
        Assert.Equal(2.0, points[^1].X, 1e-12);

        for (var i = 1; i < points.Count; i++)
        {
            Assert.True(points[i].X - points[i - 1].X <= 0.5 + 1e-9);
        }
    }

    [Fact]
    public void Discretize_InvalidKnotCount_ReturnsEmpty()
    {
        var curve = new TrajectoryCurve(
            Degree: 3,
            KnotVector: new[] { 0.0, 1.0 },
            ControlPoints: new[]
            {
                new ControlPoint(0, 0, 1),
                new ControlPoint(1, 0, 1),
                new ControlPoint(2, 0, 1),
                new ControlPoint(3, 0, 1),
            },
            Raw: null);

        Assert.Empty(NurbsDiscretizer.Discretize(curve, 0.05));
    }

    [Fact]
    public void Discretize_NonPositiveWeight_ReturnsEmpty()
    {
        var curve = new TrajectoryCurve(
            Degree: 1,
            KnotVector: new[] { 0.0, 0.0, 1.0, 1.0 },
            ControlPoints: new[]
            {
                new ControlPoint(0, 0, 1),
                new ControlPoint(2, 0, 0),
            },
            Raw: null);

        Assert.Empty(NurbsDiscretizer.Discretize(curve, 0.05));
    }

    [Fact]
    public void Discretize_FinerStep_GivesMorePoints()
    {
        var coarse = NurbsDiscretizer.Discretize(StraightCubic(), 0.2);
        var fine = NurbsDiscretizer.Discretize(StraightCubic(), 0.02);

        Assert.True(fine.Count > coarse.Count);
    }

    /// <summary>
    /// Прямая кубическая кривая из четырех контрольных точек вдоль оси X.
    /// </summary>
    private static TrajectoryCurve StraightCubic() => new(
        Degree: 3,
        KnotVector: new[] { 0.0, 0.0, 0.0, 0.0, 1.0, 1.0, 1.0, 1.0 },
        ControlPoints: new[]
        {
            new ControlPoint(0.0, 0.0, 1),
            new ControlPoint(0.3, 0.0, 1),
            new ControlPoint(0.6, 0.0, 1),
            new ControlPoint(1.0, 0.0, 1),
        },
        Raw: null);
}
