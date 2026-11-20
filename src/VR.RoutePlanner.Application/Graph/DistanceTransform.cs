namespace VR.RoutePlanner.Application.Graph;

/// <summary>
/// Точный евклидов distance transform (алгоритм Фельзенцвальба, два прохода).
/// Считает для каждого пикселя расстояние до ближайшего препятствия в пикселях.
/// </summary>
public static class DistanceTransform
{
    /// <summary>
    /// Считает поле расстояний до ближайшего препятствия.
    /// </summary>
    /// <param name="obstacle">Функция проверки пикселя на препятствие, аргументы: столбец, строка.</param>
    /// <param name="width">Ширина растра в пикселях.</param>
    /// <param name="height">Высота растра в пикселях.</param>
    /// <returns>Расстояния в пикселях, индекс <c>row * width + column</c>.</returns>
    public static float[] Compute(Func<int, int, bool> obstacle, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(obstacle);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        const float Infinity = 1e20f;

        var size = width * height;
        var grid = new float[size];
        for (var row = 0; row < height; row++)
        {
            for (var column = 0; column < width; column++)
            {
                grid[(row * width) + column] = obstacle(column, row) ? 0f : Infinity;
            }
        }

        var maxSide = Math.Max(width, height);
        var buffer = new float[maxSide];
        var result = new float[maxSide];
        var v = new int[maxSide];
        var z = new float[maxSide + 1];

        // Проход по столбцам.
        for (var column = 0; column < width; column++)
        {
            for (var row = 0; row < height; row++)
            {
                buffer[row] = grid[(row * width) + column];
            }

            Transform1D(buffer, height, result, v, z);

            for (var row = 0; row < height; row++)
            {
                grid[(row * width) + column] = result[row];
            }
        }

        // Проход по строкам.
        for (var row = 0; row < height; row++)
        {
            var offset = row * width;
            for (var column = 0; column < width; column++)
            {
                buffer[column] = grid[offset + column];
            }

            Transform1D(buffer, width, result, v, z);

            for (var column = 0; column < width; column++)
            {
                grid[offset + column] = result[column];
            }
        }

        for (var i = 0; i < size; i++)
        {
            grid[i] = (float)Math.Sqrt(grid[i]);
        }

        return grid;
    }

    private static void Transform1D(float[] f, int n, float[] d, int[] v, float[] z)
    {
        var k = 0;
        v[0] = 0;
        z[0] = float.NegativeInfinity;
        z[1] = float.PositiveInfinity;

        for (var q = 1; q < n; q++)
        {
            var s = Intersection(f, q, v[k]);

            // z[0] = -inf, поэтому цикл всегда останавливается до ухода за границу.
            while (s <= z[k])
            {
                k--;
                s = Intersection(f, q, v[k]);
            }

            k++;
            v[k] = q;
            z[k] = s;
            z[k + 1] = float.PositiveInfinity;
        }

        k = 0;
        for (var q = 0; q < n; q++)
        {
            while (z[k + 1] < q)
            {
                k++;
            }

            var delta = q - v[k];
            d[q] = (delta * (float)delta) + f[v[k]];
        }
    }

    private static float Intersection(float[] f, int q, int vk)
        => (((f[q] + (q * (float)q)) - (f[vk] + (vk * (float)vk))) / (2f * q - 2f * vk));
}
