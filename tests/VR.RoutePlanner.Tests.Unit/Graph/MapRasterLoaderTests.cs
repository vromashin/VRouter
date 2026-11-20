using VR.RoutePlanner.Application.Traffic;
using VR.RoutePlanner.Infrastructure.Graph;
using VR.RoutePlanner.Tests.Unit.Support;

namespace VR.RoutePlanner.Tests.Unit.Graph;

/// <summary>
/// Проверки декодера PNG и проверки соответствия растра метаданным карты.
/// </summary>
public sealed class MapRasterLoaderTests
{
    [Fact]
    public void Load_MatchingSize_ReturnsRasterWithPixels()
    {
        var png = PngWriter.CreateGrayscale(8, 4, (column, row) => (byte)(column == 3 && row == 2 ? 0 : 255));

        var raster = MapRasterLoader.Load(png, "inflation_map_H80.png", 0.1, 0, 0, 0.8, 0.4, 128);

        Assert.Equal(8, raster.Width);
        Assert.Equal(4, raster.Height);
        Assert.True(raster.IsObstacle(3, 2));
        Assert.False(raster.IsObstacle(0, 0));
        Assert.Equal("inflation_map_H80.png", raster.SourceName);
    }

    [Fact]
    public void Load_SizeMismatch_Throws()
    {
        var png = PngWriter.CreateGrayscale(8, 4, (_, _) => 255);

        var exception = Assert.Throws<InvalidDataException>(
            () => MapRasterLoader.Load(png, "inflation_map.png", 0.1, 0, 0, 5.0, 0.4, 128));

        Assert.Contains("пикселей", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_NotPng_Throws()
    {
        var bytes = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 };

        Assert.Throws<InvalidDataException>(
            () => MapRasterLoader.Load(bytes, "broken.png", 0.1, 0, 0, 0.8, 0.4, 128));
    }

    [Fact]
    public void Load_SixteenBitDepth_Throws()
    {
        var png = PngWriter.CreateWithHeader(4, 2, bitDepth: 16, colorType: 0);

        Assert.Throws<InvalidDataException>(
            () => MapRasterLoader.Load(png, "deep.png", 0.1, 0, 0, 0.4, 0.2, 128));
    }

    [Fact]
    public void Load_TrueColor_Throws()
    {
        var png = PngWriter.CreateWithHeader(4, 2, bitDepth: 8, colorType: 2);

        Assert.Throws<InvalidDataException>(
            () => MapRasterLoader.Load(png, "rgb.png", 0.1, 0, 0, 0.4, 0.2, 128));
    }

    [Theory]
    [InlineData("inflation_map_H80_2608281345.png", 80d)]
    [InlineData("inflation_H100.png", 100d)]
    [InlineData("map_h120_v2.png", 120d)]
    [InlineData("inflation_map.png", null)]
    public void TryReadInflationCentimeters_FileName_ReturnsExpected(string fileName, double? expected)
    {
        Assert.Equal(expected, RasterNameParser.TryReadInflationCentimeters(fileName));
    }
}
