using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace VR.RoutePlanner.Infrastructure.Graph;

/// <summary>
/// Декодер PNG для 8-битного grayscale без интерлейса. Другие форматы отвергаются
/// с понятным сообщением, чтобы подмена файла растра не прошла молча.
/// </summary>
public static class PngGrayscaleDecoder
{
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];

    /// <summary>
    /// Декодирует PNG в массив пикселей.
    /// </summary>
    /// <param name="data">Содержимое файла PNG.</param>
    /// <returns>Пиксели, ширина и высота.</returns>
    /// <exception cref="InvalidDataException">Файл поврежден или формат не поддерживается.</exception>
    public static GrayscaleImage Decode(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);

        if (data.Length < Signature.Length || !data.AsSpan(0, Signature.Length).SequenceEqual(Signature))
        {
            throw new InvalidDataException("Файл растра не является PNG.");
        }

        var width = 0;
        var height = 0;
        var bitDepth = 0;
        var colorType = -1;
        var interlace = 0;
        var hasHeader = false;
        using var compressed = new MemoryStream();

        var offset = Signature.Length;
        while (offset + 8 <= data.Length)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(offset, 4));
            var type = Encoding.ASCII.GetString(data, offset + 4, 4);
            var dataStart = offset + 8;

            if (length < 0 || (long)dataStart + length + 4 > data.Length)
            {
                throw new InvalidDataException($"Поврежденный PNG: блок {type} выходит за границы файла.");
            }

            switch (type)
            {
                case "IHDR":
                    if (length < 13)
                    {
                        throw new InvalidDataException("Поврежденный PNG: короткий блок IHDR.");
                    }

                    width = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(dataStart, 4));
                    height = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(dataStart + 4, 4));
                    bitDepth = data[dataStart + 8];
                    colorType = data[dataStart + 9];
                    interlace = data[dataStart + 12];
                    hasHeader = true;
                    break;

                case "IDAT":
                    compressed.Write(data, dataStart, length);
                    break;
            }

            offset = dataStart + length + 4;
        }

        if (!hasHeader || width <= 0 || height <= 0)
        {
            throw new InvalidDataException("Поврежденный PNG: нет корректного блока IHDR.");
        }

        if (bitDepth != 8 || colorType != 0)
        {
            throw new InvalidDataException(
                $"Растр должен быть 8-битным grayscale PNG, получено bitDepth={bitDepth}, colorType={colorType}.");
        }

        if (interlace != 0)
        {
            throw new InvalidDataException("Растр не должен быть черезстрочным (interlace).");
        }

        var stride = width;
        var raw = Inflate(compressed, height * (stride + 1));
        var pixels = Unfilter(raw, width, height);

        return new GrayscaleImage(pixels, width, height);
    }

    private static byte[] Inflate(MemoryStream compressed, int expectedSize)
    {
        compressed.Position = 0;
        using var zlib = new ZLibStream(compressed, CompressionMode.Decompress);
        var raw = new byte[expectedSize];
        var read = 0;
        while (read < raw.Length)
        {
            var chunk = zlib.Read(raw, read, raw.Length - read);
            if (chunk <= 0)
            {
                throw new InvalidDataException(
                    $"Поврежденный PNG: распаковано {read} байт вместо ожидаемых {expectedSize}.");
            }

            read += chunk;
        }

        return raw;
    }

    private static byte[] Unfilter(byte[] raw, int width, int height)
    {
        var stride = width;
        var pixels = new byte[width * height];
        var previous = new byte[stride];

        for (var row = 0; row < height; row++)
        {
            var rowStart = row * (stride + 1);
            var filter = raw[rowStart];
            var current = new byte[stride];
            Array.Copy(raw, rowStart + 1, current, 0, stride);

            for (var column = 0; column < stride; column++)
            {
                var left = column >= 1 ? current[column - 1] : (byte)0;
                var up = previous[column];
                var upLeft = column >= 1 ? previous[column - 1] : (byte)0;

                current[column] = filter switch
                {
                    0 => current[column],
                    1 => (byte)(current[column] + left),
                    2 => (byte)(current[column] + up),
                    3 => (byte)(current[column] + ((left + up) >> 1)),
                    4 => (byte)(current[column] + Paeth(left, up, upLeft)),
                    _ => throw new InvalidDataException($"Поврежденный PNG: неизвестный фильтр строки {filter}."),
                };
            }

            Array.Copy(current, 0, pixels, row * stride, stride);
            previous = current;
        }

        return pixels;
    }

    private static int Paeth(byte left, byte up, byte upLeft)
    {
        var p = left + up - upLeft;
        var pa = Math.Abs(p - left);
        var pb = Math.Abs(p - up);
        var pc = Math.Abs(p - upLeft);

        if (pa <= pb && pa <= pc)
        {
            return left;
        }

        return pb <= pc ? up : upLeft;
    }
}

/// <summary>
/// Результат декодирования PNG: пиксели и размеры.
/// </summary>
/// <param name="Pixels">Пиксели, по одному байту на точку.</param>
/// <param name="Width">Ширина в пикселях.</param>
/// <param name="Height">Высота в пикселях.</param>
public sealed record GrayscaleImage(byte[] Pixels, int Width, int Height);
