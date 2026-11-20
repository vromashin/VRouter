using VR.RoutePlanner.Application.Graph;
using VR.RoutePlanner.Domain.Graph;

namespace VR.RoutePlanner.Tests.Unit.Support;

/// <summary>
/// Подставной провайдер растра: отдает заранее заданный растр или ошибку.
/// </summary>
internal sealed class FakeMapRasterProvider : IMapRasterProvider
{
    private readonly MapRaster? _raster;
    private readonly Exception? _error;

    private FakeMapRasterProvider(MapRaster? raster, Exception? error)
    {
        _raster = raster;
        _error = error;
    }

    public int Calls { get; private set; }

    public static FakeMapRasterProvider WithRaster(MapRaster raster) => new(raster, null);

    public static FakeMapRasterProvider WithError(Exception error) => new(null, error);

    public Task<MapRaster> GetAsync(string mapId, CancellationToken ct)
    {
        Calls++;
        if (_error is not null)
        {
            throw _error;
        }

        return Task.FromResult(_raster!);
    }

    /// <summary>
    /// Создает растр с заданными размерами и списком препятствий.
    /// </summary>
    public static MapRaster CreateRaster(
        int width,
        int height,
        double resolution,
        int obstacleThreshold,
        params (int Column, int Row)[] obstacles)
    {
        var pixels = new byte[width * height];
        Array.Fill(pixels, (byte)255);

        foreach (var (column, row) in obstacles)
        {
            pixels[(row * width) + column] = 0;
        }

        return new MapRaster(pixels, width, height, resolution, 0, 0, obstacleThreshold);
    }

    /// <summary>
    /// Создает растр 40 на 40 пикселей с разрешением 0.1 м/пиксель и препятствием на оси X.
    /// </summary>
    public static MapRaster CreateDefault()
    {
        var pixels = new byte[40 * 40];
        Array.Fill(pixels, (byte)255);

        for (var row = 0; row < 40; row++)
        {
            pixels[(row * 40) + 20] = 0;
        }

        return new MapRaster(pixels, 40, 40, 0.1, 0, 0, 128);
    }
}
