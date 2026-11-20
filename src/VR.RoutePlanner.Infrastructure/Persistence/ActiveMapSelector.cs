using System.Globalization;

namespace VR.RoutePlanner.Infrastructure.Persistence;

/// <summary>
/// Сравнение версий карт «по-человечески»: цифровые части сравниваются как числа,
/// текстовые как строки. Нужно потому, что строка версии не обязана быть числом.
/// </summary>
public sealed class MapVersionComparer : IComparer<string>
{
    /// <summary>Единственный экземпляр компаратора.</summary>
    public static MapVersionComparer Instance { get; } = new();

    /// <inheritdoc />
    public int Compare(string? left, string? right)
    {
        if (ReferenceEquals(left, right))
        {
            return 0;
        }

        if (left is null)
        {
            return -1;
        }

        if (right is null)
        {
            return 1;
        }

        var leftIndex = 0;
        var rightIndex = 0;

        while (leftIndex < left.Length && rightIndex < right.Length)
        {
            var leftIsDigit = char.IsDigit(left[leftIndex]);
            var rightIsDigit = char.IsDigit(right[rightIndex]);

            if (leftIsDigit && rightIsDigit)
            {
                var leftEnd = ReadRun(left, leftIndex, char.IsDigit);
                var rightEnd = ReadRun(right, rightIndex, char.IsDigit);
                var leftNumber = left.AsSpan(leftIndex, leftEnd - leftIndex);
                var rightNumber = right.AsSpan(rightIndex, rightEnd - rightIndex);

                var leftTrimmed = leftNumber.TrimStart('0');
                var rightTrimmed = rightNumber.TrimStart('0');
                if (leftTrimmed.Length != rightTrimmed.Length)
                {
                    return leftTrimmed.Length.CompareTo(rightTrimmed.Length);
                }

                var digits = leftTrimmed.SequenceCompareTo(rightTrimmed);
                if (digits != 0)
                {
                    return digits;
                }

                leftIndex = leftEnd;
                rightIndex = rightEnd;
                continue;
            }

            var text = left[leftIndex].CompareTo(right[rightIndex]);
            if (text != 0)
            {
                return text;
            }

            leftIndex++;
            rightIndex++;
        }

        return (left.Length - leftIndex).CompareTo(right.Length - rightIndex);
    }

    private static int ReadRun(string text, int start, Func<char, bool> predicate)
    {
        var index = start;
        while (index < text.Length && predicate(text[index]))
        {
            index++;
        }

        return index;
    }
}

/// <summary>
/// Выбор актуальной активной карты из строк <c>public."Maps"</c>.
/// </summary>
public static class ActiveMapSelector
{
    /// <summary>
    /// Выбирает самую свежую активную строку карты по семантическому сравнению версий.
    /// </summary>
    /// <param name="maps">Активные строки карты с одним MapId.</param>
    /// <param name="mapId">Идентификатор карты для сообщений.</param>
    /// <returns>Выбранная строка.</returns>
    /// <exception cref="InvalidOperationException">Активных строк нет.</exception>
    public static RmsMapRecord Select(IReadOnlyList<RmsMapRecord> maps, string mapId)
    {
        ArgumentNullException.ThrowIfNull(maps);

        if (maps.Count == 0)
        {
            throw new InvalidOperationException(
                $"Активная карта '{mapId}' не найдена в public.\"Maps\" (IsActive = true).");
        }

        var ordered = maps
            .OrderByDescending(record => record.MapVersion, MapVersionComparer.Instance)
            .ThenByDescending(record => record.Id)
            .ToList();

        return ordered[0];
    }

    /// <summary>
    /// Возвращает строку с описанием версий для предупреждения в лог.
    /// </summary>
    /// <param name="maps">Активные строки карты.</param>
    /// <returns>Перечисление версий через запятую.</returns>
    public static string DescribeVersions(IEnumerable<RmsMapRecord> maps) =>
        string.Join(
            ", ",
            maps.Select(record => string.Create(
                CultureInfo.InvariantCulture,
                $"{record.MapVersion} ({record.Id})")));
}
