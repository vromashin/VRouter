using VR.RoutePlanner.Application.Graph;

namespace VR.RoutePlanner.Infrastructure.Graph;

/// <summary>
/// Загрузка и проверка растра инфлированной карты препятствий.
/// </summary>
public static class MapRasterLoader
{
    /// <summary>
    /// Допуск на расхождение размера растра с метаданными карты, пиксели.
    /// </summary>
    public const double SizeTolerancePixels = 1.0;

    /// <summary>
    /// Проверяет размер растра относительно метаданных карты и создает <see cref="MapRaster"/>.
    /// </summary>
    /// <param name="fileBytes">Содержимое файла PNG.</param>
    /// <param name="sourceName">Имя файла для диагностики.</param>
    /// <param name="resolution">Разрешение карты, метры на пиксель.</param>
    /// <param name="originX">Смещение начала координат по X, метры.</param>
    /// <param name="originY">Смещение начала координат по Y, метры.</param>
    /// <param name="mapWidth">Ширина карты из справочника, метры.</param>
    /// <param name="mapHeight">Высота карты из справочника, метры.</param>
    /// <param name="obstacleThreshold">Порог препятствия.</param>
    /// <returns>Готовый растр.</returns>
    /// <exception cref="InvalidDataException">Размер растра не совпадает с метаданными карты.</exception>
    public static MapRaster Load(
        byte[] fileBytes,
        string sourceName,
        double resolution,
        double originX,
        double originY,
        double mapWidth,
        double mapHeight,
        int obstacleThreshold)
    {
        ArgumentNullException.ThrowIfNull(fileBytes);

        if (resolution <= 0)
        {
            throw new InvalidOperationException(
                $"У карты не задано разрешение растра: Resolution = {resolution}.");
        }

        var image = PngGrayscaleDecoder.Decode(fileBytes);
        ValidateSize(image, sourceName, resolution, mapWidth, mapHeight);

        return new MapRaster(
            image.Pixels,
            image.Width,
            image.Height,
            resolution,
            originX,
            originY,
            obstacleThreshold)
        {
            SourceName = sourceName,
        };
    }

    /// <summary>
    /// Сверяет размер растра с размерами карты из справочника.
    /// </summary>
    /// <param name="image">Декодированный растр.</param>
    /// <param name="sourceName">Имя файла для диагностики.</param>
    /// <param name="resolution">Разрешение карты, метры на пиксель.</param>
    /// <param name="mapWidth">Ширина карты, метры.</param>
    /// <param name="mapHeight">Высота карты, метры.</param>
    /// <exception cref="InvalidDataException">Размер не совпадает с допуском в один пиксель.</exception>
    public static void ValidateSize(
        GrayscaleImage image,
        string sourceName,
        double resolution,
        double mapWidth,
        double mapHeight)
    {
        ArgumentNullException.ThrowIfNull(image);

        var expectedWidth = mapWidth / resolution;
        var expectedHeight = mapHeight / resolution;
        var widthDeviation = Math.Abs(image.Width - expectedWidth);
        var heightDeviation = Math.Abs(image.Height - expectedHeight);

        if (widthDeviation <= SizeTolerancePixels && heightDeviation <= SizeTolerancePixels)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Растр '{sourceName}' имеет размер {image.Width} x {image.Height} пикселей, "
            + $"а карта {mapWidth} x {mapHeight} м при разрешении {resolution} м/пиксель "
            + $"требует {expectedWidth:0.##} x {expectedHeight:0.##}. Похоже, файл от другой версии карты.");
    }
}
