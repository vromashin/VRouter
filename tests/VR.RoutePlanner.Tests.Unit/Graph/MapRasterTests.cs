using VR.RoutePlanner.Application.Graph;
using VR.RoutePlanner.Tests.Unit.Support;

namespace VR.RoutePlanner.Tests.Unit.Graph;

/// <summary>
/// Проверки растра: поле расстояний, порог препятствия, координаты.
/// </summary>
public sealed class MapRasterTests
{
    [Fact]
    public void GetObstacleDistanceMeters_OnObstacle_ReturnsZero()
    {
        var raster = FakeMapRasterProvider.CreateRaster(3, 3, 1.0, 128, (1, 1));

        Assert.Equal(0.0, raster.GetObstacleDistanceMeters(1.0, 1.0), 6);
    }

    [Fact]
    public void GetObstacleDistanceMeters_Diagonal_ReturnsEuclideanDistance()
    {
        var raster = FakeMapRasterProvider.CreateRaster(3, 3, 1.0, 128, (1, 1));

        Assert.Equal(Math.Sqrt(2), raster.GetObstacleDistanceMeters(0.0, 0.0), 3);
    }

    [Fact]
    public void GetObstacleDistanceMeters_Resolution_ScalesDistance()
    {
        var raster = FakeMapRasterProvider.CreateRaster(3, 3, 0.5, 128, (1, 1));

        Assert.Equal(0.5, raster.GetObstacleDistanceMeters(1.0, 0.5), 6);
    }

    [Fact]
    public void GetObstacleDistanceMeters_OutsideRaster_ClampsToBorder()
    {
        var raster = FakeMapRasterProvider.CreateRaster(3, 3, 1.0, 128, (1, 1));

        var inside = raster.GetObstacleDistanceMeters(0.0, 0.0);
        var outside = raster.GetObstacleDistanceMeters(-100.0, -100.0);

        Assert.Equal(inside, outside, 6);
    }

    [Fact]
    public void Constructor_PixelBufferMismatch_Throws()
    {
        Assert.Throws<ArgumentException>(() => new MapRaster(new byte[8], 3, 3, 1.0, 0, 0, 128));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(256)]
    public void Constructor_ThresholdOutOfRange_Throws(int threshold)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new MapRaster(new byte[9], 3, 3, 1.0, 0, 0, threshold));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_NonPositiveResolution_Throws(double resolution)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new MapRaster(new byte[9], 3, 3, resolution, 0, 0, 128));
    }

    [Fact]
    public void IsObstacle_Threshold_ComparesPixelValue()
    {
        var pixels = new byte[9];
        Array.Fill(pixels, (byte)255);
        pixels[4] = 100;

        var raster = new MapRaster(pixels, 3, 3, 1.0, 0, 0, 128);

        Assert.True(raster.IsObstacle(1, 1));
        Assert.False(raster.IsObstacle(0, 0));
    }
}
