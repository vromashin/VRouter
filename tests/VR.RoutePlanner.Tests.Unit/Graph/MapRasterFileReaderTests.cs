using Microsoft.Extensions.Logging.Abstractions;
using VR.RoutePlanner.Infrastructure.Graph;

namespace VR.RoutePlanner.Tests.Unit.Graph;

/// <summary>
/// Проверки чтения файла растра: HTTP допускается только при пустом каталоге растров.
/// </summary>
public sealed class MapRasterFileReaderTests
{
    [Fact]
    public async Task ReadAsync_DirectorySetAndFileMissing_ThrowsFileNotFoundWithoutHttp()
    {
        var directory = Path.Combine(Path.GetTempPath(), "vr-route-planner-no-raster-" + Guid.NewGuid().ToString("N"));

        var exception = await Assert.ThrowsAsync<FileNotFoundException>(
            () => MapRasterFileReader.ReadAsync(
                directory,
                "http://127.0.0.1:9/map-files",
                "inflation_map_H80.png",
                NullLogger.Instance,
                CancellationToken.None));

        Assert.Contains("Routing:MapRasterPath", exception.Message, StringComparison.Ordinal);
        Assert.Equal(Path.Combine(directory, "inflation_map_H80.png"), exception.FileName);
    }

    [Fact]
    public async Task ReadAsync_DirectorySetAndFileExists_ReturnsBytesFromDisk()
    {
        var directory = Directory.CreateTempSubdirectory("vr-route-planner-raster-");
        try
        {
            var path = Path.Combine(directory.FullName, "inflation_map_H80.png");
            var bytes = new byte[] { 137, 80, 78, 71 };
            await File.WriteAllBytesAsync(path, bytes);

            var (read, stamp) = await MapRasterFileReader.ReadAsync(
                directory.FullName,
                "http://127.0.0.1:9/map-files",
                "inflation_map_H80.png",
                NullLogger.Instance,
                CancellationToken.None);

            Assert.Equal(bytes, read);
            Assert.StartsWith("file:", stamp, StringComparison.Ordinal);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ReadAsync_DirectoryEmptyAndUrlEmpty_ThrowsFileNotFound()
    {
        await Assert.ThrowsAsync<FileNotFoundException>(
            () => MapRasterFileReader.ReadAsync(
                null,
                null,
                "inflation_map_H80.png",
                NullLogger.Instance,
                CancellationToken.None));
    }
}
