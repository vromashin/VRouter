using System.Globalization;
using Microsoft.Extensions.Logging;

namespace VR.RoutePlanner.Infrastructure.Graph;

/// <summary>
/// Чтение байтов файла растра.
/// Если каталог <c>Routing:MapRasterPath</c> задан, файл читается только с диска:
/// HTTP-загрузка в этом случае запрещена, отсутствие файла это ошибка (исключение наружу).
/// Если каталог пуст, файл скачивается по адресу <c>Routing:MapRasterUrl</c>.
/// </summary>
public static class MapRasterFileReader
{
    private static readonly Lazy<HttpClient> SharedHttpClient = new(
        () => new HttpClient { Timeout = TimeSpan.FromSeconds(30) },
        isThreadSafe: true);

    /// <summary>
    /// Читает файл растра.
    /// </summary>
    /// <param name="directory">Каталог с файлами растров, может быть пустым.</param>
    /// <param name="url">Базовый адрес HTTP-загрузки, используется только при пустом каталоге.</param>
    /// <param name="fileName">Имя файла растра.</param>
    /// <param name="logger">Логгер.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Байты файла и штамп источника для ключа кэша.</returns>
    /// <exception cref="FileNotFoundException">Файл не найден, а HTTP-загрузка запрещена или не настроена.</exception>
    public static async Task<(byte[] Bytes, string Stamp)> ReadAsync(
        string? directory,
        string? url,
        string fileName,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(logger);

        if (!string.IsNullOrWhiteSpace(directory))
        {
            var fullPath = Path.Combine(directory, fileName);
            var info = new FileInfo(fullPath);
            if (!info.Exists)
            {
                throw new FileNotFoundException(
                    $"Файл растра '{fileName}' не найден в каталоге Routing:MapRasterPath '{directory}'. "
                    + "HTTP-загрузка при заданном каталоге не используется.",
                    fullPath);
            }

            var fileBytes = await File.ReadAllBytesAsync(fullPath, cancellationToken).ConfigureAwait(false);
            var stamp = string.Create(
                CultureInfo.InvariantCulture,
                $"file:{info.LastWriteTimeUtc.Ticks}:{fileBytes.Length}");

            return (fileBytes, stamp);
        }

        if (string.IsNullOrWhiteSpace(url))
        {
            throw new FileNotFoundException(
                $"Файл растра '{fileName}': Routing:MapRasterPath пуст, а Routing:MapRasterUrl не задан.");
        }

        var address = url.TrimEnd('/') + "/" + Uri.EscapeDataString(fileName);
        var downloaded = await SharedHttpClient.Value
            .GetByteArrayAsync(address, cancellationToken)
            .ConfigureAwait(false);

        logger.LogInformation(
            "Растр {FileName} скачан по адресу {Url}, {Size} байт.",
            fileName,
            address,
            downloaded.Length);

        return (downloaded, string.Create(CultureInfo.InvariantCulture, $"http:{downloaded.Length}"));
    }
}
