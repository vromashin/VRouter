using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace VR.RoutePlanner.Tests.Unit.Support;

/// <summary>
/// Минимальный писатель PNG для тестов: grayscale 8 бит на пиксель, фильтр None.
/// </summary>
internal static class PngWriter
{
    private static readonly byte[] Signature = { 137, 80, 78, 71, 13, 10, 26, 10 };

    /// <summary>
    /// Создает корректный grayscale PNG.
    /// </summary>
    public static byte[] CreateGrayscale(int width, int height, Func<int, int, byte> sample)
    {
        var raw = new byte[height * (width + 1)];
        var index = 0;
        for (var row = 0; row < height; row++)
        {
            raw[index++] = 0;
            for (var column = 0; column < width; column++)
            {
                raw[index++] = sample(column, row);
            }
        }

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(raw, 0, raw.Length);
        }

        return BuildPng(width, height, bitDepth: 8, colorType: 0, compressed.ToArray());
    }

    /// <summary>
    /// Создает PNG с произвольными параметрами заголовка и пустым потоком данных.
    /// Нужен для проверки отказа на неподдерживаемых форматах.
    /// </summary>
    public static byte[] CreateWithHeader(int width, int height, byte bitDepth, byte colorType)
    {
        return BuildPng(width, height, bitDepth, colorType, Array.Empty<byte>());
    }

    private static byte[] BuildPng(int width, int height, byte bitDepth, byte colorType, byte[] imageData)
    {
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0, 4), width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4, 4), height);
        header[8] = bitDepth;
        header[9] = colorType;

        using var png = new MemoryStream();
        png.Write(Signature, 0, Signature.Length);
        WriteChunk(png, "IHDR", header);
        WriteChunk(png, "IDAT", imageData);
        WriteChunk(png, "IEND", Array.Empty<byte>());
        return png.ToArray();
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        stream.Write(length);

        var typeBytes = Encoding.ASCII.GetBytes(type);
        stream.Write(typeBytes, 0, typeBytes.Length);
        stream.Write(data, 0, data.Length);

        var crcInput = new byte[typeBytes.Length + data.Length];
        typeBytes.CopyTo(crcInput, 0);
        data.CopyTo(crcInput, typeBytes.Length);

        Span<byte> crc = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(crcInput));
        stream.Write(crc);
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) == 1 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }

        return crc ^ 0xFFFFFFFFu;
    }
}
