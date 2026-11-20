using System.Text.Json;
using Microsoft.Extensions.Logging;
using VR.RoutePlanner.Application.Graph;
using VR.RoutePlanner.Domain.Graph;

namespace VR.RoutePlanner.Infrastructure.Graph;

/// <summary>
/// Разбор jsonb-поля <c>Edges."Trajectory"</c> в NURBS-кривую и полилинию.
/// </summary>
public static class TrajectoryParser
{
    /// <summary>
    /// Разбирает траекторию ребра. Любая ошибка разбора не приводит к исключению:
    /// возвращается пустая кривая, сырой текст сохраняется для диагностики.
    /// </summary>
    /// <param name="rawJson">Содержимое поля Trajectory, может быть null.</param>
    /// <param name="sampleStep">Шаг дискретизации, метры.</param>
    /// <param name="logger">Логгер для предупреждений, может быть null.</param>
    /// <param name="edgeId">Идентификатор ребра для сообщений.</param>
    /// <returns>Кривая (может быть null) и дискретизированная полилиния.</returns>
    public static TrajectoryParseResult Parse(
        string? rawJson,
        double sampleStep,
        ILogger? logger,
        string? edgeId)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            LogWarning(logger, "У ребра {EdgeId} нет траектории, полилиния пуста.", edgeId);
            return TrajectoryParseResult.Empty;
        }

        TrajectoryCurve curve;
        try
        {
            curve = ReadCurve(rawJson);
        }
        catch (Exception exception) when (exception is JsonException or FormatException or InvalidOperationException)
        {
            LogWarning(
                logger,
                "Траектория ребра {EdgeId} не разобрана: {Message}. Полилиния пуста, сырой текст сохранен для диагностики.",
                edgeId,
                exception.Message);
            return new TrajectoryParseResult(null, Array.Empty<MapPoint>());
        }

        var error = curve.Validate();
        if (error is not null)
        {
            LogWarning(
                logger,
                "Траектория ребра {EdgeId} не прошла проверку: {Error}. Полилиния пуста.",
                edgeId,
                error);
            return new TrajectoryParseResult(curve, Array.Empty<MapPoint>());
        }

        var points = NurbsDiscretizer.Discretize(curve, sampleStep);
        return new TrajectoryParseResult(curve, points);
    }

    private static TrajectoryCurve ReadCurve(string rawJson)
    {
        using var document = JsonDocument.Parse(rawJson);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException("ожидался объект с полями degree, knotVector, controlPoints");
        }

        var degree = (int)Math.Round(ReadDouble(root, "degree", required: true));
        var knots = ReadKnots(root);
        var controlPoints = ReadControlPoints(root);

        return new TrajectoryCurve(degree, knots, controlPoints, rawJson);
    }

    private static IReadOnlyList<double> ReadKnots(JsonElement root)
    {
        if (!TryGetProperty(root, "knotVector", out var array) || array.ValueKind != JsonValueKind.Array)
        {
            throw new FormatException("поле knotVector отсутствует или не является массивом");
        }

        var knots = new List<double>(array.GetArrayLength());
        foreach (var item in array.EnumerateArray())
        {
            knots.Add(item.GetDouble());
        }

        return knots;
    }

    private static IReadOnlyList<ControlPoint> ReadControlPoints(JsonElement root)
    {
        if (!TryGetProperty(root, "controlPoints", out var array) || array.ValueKind != JsonValueKind.Array)
        {
            throw new FormatException("поле controlPoints отсутствует или не является массивом");
        }

        var points = new List<ControlPoint>(array.GetArrayLength());
        foreach (var item in array.EnumerateArray())
        {
            var x = ReadDouble(item, "x", required: true);
            var y = ReadDouble(item, "y", required: true);
            var weight = ReadDouble(item, "weight", required: false, defaultValue: 1.0);
            points.Add(new ControlPoint(x, y, weight));
        }

        return points;
    }

    private static double ReadDouble(
        JsonElement element,
        string name,
        bool required,
        double defaultValue = 0)
    {
        if (!TryGetProperty(element, name, out var value))
        {
            if (required)
            {
                throw new FormatException($"отсутствует поле {name}");
            }

            return defaultValue;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetDouble(),
            JsonValueKind.String => double.TryParse(
                value.GetString(),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var parsed)
                ? parsed
                : throw new FormatException($"поле {name} не является числом"),
            _ => throw new FormatException($"поле {name} не является числом"),
        };
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static void LogWarning(ILogger? logger, string message, params object?[] args) =>
        logger?.LogWarning(message, args);
}

/// <summary>
/// Результат разбора траектории ребра.
/// </summary>
/// <param name="Curve">Разобранная кривая, null при невалидном JSON.</param>
/// <param name="Points">Дискретизированная полилиния, пустая при ошибке.</param>
public sealed record TrajectoryParseResult(TrajectoryCurve? Curve, IReadOnlyList<MapPoint> Points)
{
    /// <summary>Пустой результат: нет ни кривой, ни точек.</summary>
    public static TrajectoryParseResult Empty { get; } =
        new(null, Array.Empty<MapPoint>());
}
