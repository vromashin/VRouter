namespace VR.RoutePlanner.Application.Graph;

/// <summary>
/// Растр инфлированной карты препятствий: 8-битное grayscale-изображение,
/// где пиксели темнее порога считаются препятствиями.
/// </summary>
public sealed class MapRaster
{
    private readonly Lazy<float[]> _distancePixels;

    /// <summary>
    /// Создает растр и подготавливает ленивое поле расстояний.
    /// </summary>
    /// <param name="pixels">Пиксели, по одному байту на пиксель, построчно.</param>
    /// <param name="width">Ширина в пикселях.</param>
    /// <param name="height">Высота в пикселях.</param>
    /// <param name="resolution">Размер пикселя в метрах.</param>
    /// <param name="originX">Координата X левого верхнего угла растра, метры.</param>
    /// <param name="originY">Координата Y левого верхнего угла растра, метры.</param>
    /// <param name="obstacleThreshold">Порог препятствия: значение пикселя ниже порога это препятствие.</param>
    public MapRaster(
        byte[] pixels,
        int width,
        int height,
        double resolution,
        double originX,
        double originY,
        int obstacleThreshold)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(resolution);
        ArgumentOutOfRangeException.ThrowIfNegative(obstacleThreshold);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(obstacleThreshold, 255);

        if (pixels.Length != width * height)
        {
            throw new ArgumentException(
                $"Размер буфера пикселей {pixels.Length} не совпадает с {width} x {height} = {width * height}.",
                nameof(pixels));
        }

        Pixels = pixels;
        Width = width;
        Height = height;
        Resolution = resolution;
        OriginX = originX;
        OriginY = originY;
        ObstacleThreshold = obstacleThreshold;
        _distancePixels = new Lazy<float[]>(() => DistanceTransform.Compute((column, row) => IsObstacle(column, row), Width, Height), isThreadSafe: true);
    }

    /// <summary>Пиксели растра, по одному байту на пиксель, построчно.</summary>
    public byte[] Pixels { get; }

    /// <summary>Ширина растра в пикселях.</summary>
    public int Width { get; }

    /// <summary>Высота растра в пикселях.</summary>
    public int Height { get; }

    /// <summary>Размер пикселя в метрах.</summary>
    public double Resolution { get; }

    /// <summary>Координата X левого верхнего угла растра, метры.</summary>
    public double OriginX { get; }

    /// <summary>Координата Y левого верхнего угла растра, метры.</summary>
    public double OriginY { get; }

    /// <summary>Порог, ниже которого пиксель считается препятствием.</summary>
    public int ObstacleThreshold { get; }

    /// <summary>Имя файла растра для диагностики. Может быть пустым.</summary>
    public string? SourceName { get; init; }

    /// <summary>
    /// Проверяет пиксель на препятствие.
    /// </summary>
    /// <param name="column">Столбец.</param>
    /// <param name="row">Строка.</param>
    /// <returns><c>true</c>, если пиксель является препятствием.</returns>
    public bool IsObstacle(int column, int row)
    {
        if (column < 0 || column >= Width || row < 0 || row >= Height)
        {
            throw new ArgumentOutOfRangeException(nameof(column), $"Пиксель ({column}, {row}) вне растра {Width} x {Height}.");
        }

        return Pixels[(row * Width) + column] < ObstacleThreshold;
    }

    /// <summary>
    /// Расстояние от точки карты до ближайшего препятствия в метрах.
    /// Точки вне растра прижимаются к его границе.
    /// </summary>
    /// <param name="x">Координата X, метры.</param>
    /// <param name="y">Координата Y, метры.</param>
    /// <returns>Расстояние в метрах.</returns>
    public double GetObstacleDistanceMeters(double x, double y)
    {
        var column = (int)Math.Round((x - OriginX) / Resolution, MidpointRounding.AwayFromZero);
        var row = (int)Math.Round((y - OriginY) / Resolution, MidpointRounding.AwayFromZero);
        column = Math.Clamp(column, 0, Width - 1);
        row = Math.Clamp(row, 0, Height - 1);

        return _distancePixels.Value[(row * Width) + column] * Resolution;
    }
}
