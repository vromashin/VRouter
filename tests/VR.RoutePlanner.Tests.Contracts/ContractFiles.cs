using System.Text.Json;
using System.Text.RegularExpressions;

namespace VR.RoutePlanner.Tests.Contracts;

/// <summary>
/// Доступ к снимкам контрактов в каталоге сборки. Тесты не ходят в сеть и в базу:
/// единственный источник данных — файлы, скопированные из <c>docs/contracts</c>.
/// </summary>
internal static partial class ContractFiles
{
    /// <summary>Имя файла со снимком OpenAPI сервиса AGVControl.</summary>
    public const string SwaggerSnapshot = "agvcontrol-swagger.json";

    /// <summary>Имя файла с чек-листом паритета.</summary>
    public const string ParityChecklist = "parity-checklist.md";

    /// <summary>Каталог снимков в каталоге сборки.</summary>
    public static string ContractsDirectory => Path.Combine(AppContext.BaseDirectory, "contracts");

    /// <summary>Каталог примеров сообщений.</summary>
    public static string SamplesDirectory => Path.Combine(ContractsDirectory, "samples");

    /// <summary>Читает снимок OpenAPI как разобранный <see cref="JsonDocument"/>.</summary>
    public static JsonDocument OpenSwagger()
    {
        var path = Path.Combine(ContractsDirectory, SwaggerSnapshot);
        Assert.True(File.Exists(path), $"нет снимка контракта: {path}");
        return JsonDocument.Parse(File.ReadAllText(path));
    }

    /// <summary>
    /// Читает чек-лист как строки таблицы: канал, метод, путь, уровень.
    /// Разбираются только строки HTTP, у остальных каналов в колонке контракта нет метода.
    /// </summary>
    public static IReadOnlyList<ChecklistRow> ReadChecklist()
    {
        var path = Path.Combine(ContractsDirectory, ParityChecklist);
        Assert.True(File.Exists(path), $"нет чек-листа: {path}");

        var rows = new List<ChecklistRow>();
        foreach (var line in File.ReadLines(path))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith('|'))
            {
                continue;
            }

            var cells = trimmed
                .Trim('|')
                .Split('|')
                .Select(cell => cell.Trim().Trim('`').Trim())
                .ToArray();

            if (cells.Length < 4 || !int.TryParse(cells[3], out var level))
            {
                continue;
            }

            var contract = cells[1];
            var match = HttpContract().Match(contract);
            if (!match.Success)
            {
                continue;
            }

            var route = match.Groups["path"].Value;
            if (route.Contains('*') || route.Contains(',') || !route.StartsWith('/'))
            {
                continue;
            }

            rows.Add(new ChecklistRow(match.Groups["method"].Value.ToUpperInvariant(), route, level));
        }

        return rows;
    }

    /// <summary>
    /// Приводит шаблон пути к сравнимому виду: имена параметров в снимке и в чек-листе разные
    /// (<c>{serial}</c> против <c>{agvSerialNumber}</c>), значим только сам факт параметра.
    /// </summary>
    public static string Normalize(string path) => PathParameter().Replace(path, "{}");

    /// <summary>Строка чек-листа с HTTP-контрактом.</summary>
    /// <param name="Method">HTTP-метод.</param>
    /// <param name="Path">Шаблон пути.</param>
    /// <param name="Level">Уровень паритета.</param>
    public sealed record ChecklistRow(string Method, string Path, int Level);

    [GeneratedRegex(@"^(?<method>GET|POST|PUT|DELETE|PATCH)\s+(?<path>/\S+)$")]
    private static partial Regex HttpContract();

    [GeneratedRegex(@"\{[^}]*\}")]
    private static partial Regex PathParameter();
}
