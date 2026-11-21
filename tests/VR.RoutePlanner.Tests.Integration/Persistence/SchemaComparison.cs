using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using VR.RoutePlanner.Infrastructure.Persistence;

namespace VR.RoutePlanner.Tests.Integration.Persistence;

internal static class SchemaComparison
{
    public static async Task AssertColumnsAsync(
        RoutePlannerDbContext context,
        CancellationToken ct = default)
    {
        var actual = await ReadColumnsAsync(context, ct);
        var problems = new List<string>();

        foreach (var entity in context.Model.GetEntityTypes()
                     .Where(type => type.GetTableName()?.StartsWith("vrt_", StringComparison.Ordinal) == true))
        {
            var table = entity.GetTableName()!;
            if (!actual.TryGetValue(table, out var columns))
            {
                problems.Add($"Таблицы public.\"{table}\" нет в базе.");
                continue;
            }

            var expected = entity.GetProperties()
                .Select(property => new
                {
                    Column = property.GetColumnName(),
                    Type = Normalize(StoreType(property)),
                    Nullable = property.IsNullable,
                })
                .ToList();

            foreach (var item in expected)
            {
                var found = columns.FirstOrDefault(column => column.Name == item.Column);
                if (found is null)
                {
                    problems.Add($"{table}: в базе нет колонки \"{item.Column}\".");
                    continue;
                }

                if (found.Type != item.Type)
                {
                    problems.Add($"{table}.\"{item.Column}\": тип в базе {found.Type}, в модели {item.Type}.");
                }

                if (found.Nullable != item.Nullable)
                {
                    problems.Add($"{table}.\"{item.Column}\": nullable в базе {found.Nullable}, в модели {item.Nullable}.");
                }
            }

            foreach (var column in columns.Where(column => expected.All(item => item.Column != column.Name)))
            {
                problems.Add($"{table}: в базе лишняя колонка \"{column.Name}\" ({column.Type}).");
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    /// <summary>
    /// Сверяет с базой все индексы модели у таблиц <c>vrt_*</c>: и уникальные, и обычные.
    /// Неуникальные индексы раньше не сверялись, поэтому их отсутствие в базе проходило незамеченным.
    /// </summary>
    public static async Task AssertIndexesAsync(
        RoutePlannerDbContext context,
        CancellationToken ct = default)
    {
        var actual = await ReadIndexesAsync(context, ct);
        var problems = new List<string>();

        foreach (var entity in context.Model.GetEntityTypes()
                     .Where(type => type.GetTableName()?.StartsWith("vrt_", StringComparison.Ordinal) == true))
        {
            var table = entity.GetTableName()!;
            var expected = entity.GetIndexes()
                .Select(index => new { Name = index.GetDatabaseName(), index.IsUnique })
                .ToList();

            if (!actual.TryGetValue(table, out var indexes))
            {
                problems.Add($"Таблицы public.\"{table}\" нет в базе.");
                continue;
            }

            foreach (var item in expected)
            {
                var found = indexes.FirstOrDefault(index => index.Name == item.Name);
                if (found is null)
                {
                    var kind = item.IsUnique ? "уникального" : "обычного";
                    problems.Add($"{table}: в базе нет {kind} индекса \"{item.Name}\".");
                    continue;
                }

                if (found.IsUnique != item.IsUnique)
                {
                    problems.Add(
                        $"{table}.\"{item.Name}\": уникальность в базе {found.IsUnique}, в модели {item.IsUnique}.");
                }
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    private static string StoreType(IReadOnlyProperty property) =>
        property.GetColumnType() ?? property.ClrType.Name switch
        {
            "Guid" => "uuid",
            "Boolean" => "boolean",
            "Int32" => "integer",
            "Int64" => "bigint",
            "Double" => "double precision",
            "String" => "text",
            _ => property.ClrType.Name,
        };

    private sealed record Column(string Name, string Type, bool Nullable);

    private static string Normalize(string storeType)
    {
        var type = storeType.Trim().ToLowerInvariant();
        return type == "character varying" ? "varchar" : type;
    }

    private static string NormalizeStore(string storeType) => Normalize(storeType);

    private static async Task<Dictionary<string, List<Column>>> ReadColumnsAsync(
        RoutePlannerDbContext context,
        CancellationToken ct)
    {
        var sql = """
            SELECT table_name || '.' || column_name AS "Value",
                   data_type AS "DataType",
                   COALESCE(character_maximum_length, 0) AS "Length",
                   CASE WHEN is_nullable = 'YES' THEN true ELSE false END AS "IsNullable"
            FROM information_schema.columns
            WHERE table_schema = 'public' AND table_name LIKE 'vrt/_%' ESCAPE '/'
            """;

        var rows = await context.Database.SqlQueryRaw<SchemaRow>(sql).ToListAsync(ct);

        return rows
            .GroupBy(row => row.Value.Split('.')[0], StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group
                    .Select(row =>
                    {
                        var name = row.Value[(row.Value.IndexOf('.', StringComparison.Ordinal) + 1)..];
                        var type = row.Length > 0 ? $"{row.DataType}({row.Length})" : row.DataType;
                        return new Column(name, NormalizeStore(type), row.IsNullable);
                    })
                    .ToList(),
                StringComparer.Ordinal);
    }

    private static async Task<Dictionary<string, List<IndexInfo>>> ReadIndexesAsync(
        RoutePlannerDbContext context,
        CancellationToken ct)
    {
        var sql = """
            SELECT tablename || '.' || indexname AS "Value",
                   CASE WHEN indexdef LIKE '%UNIQUE%' THEN true ELSE false END AS "IsUnique"
            FROM pg_indexes
            WHERE schemaname = 'public' AND tablename LIKE 'vrt/_%' ESCAPE '/'
            """;

        var rows = await context.Database.SqlQueryRaw<IndexRow>(sql).ToListAsync(ct);

        return rows
            .GroupBy(row => row.Value.Split('.')[0], StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group
                    .Select(row => new IndexInfo(
                        row.Value[(row.Value.IndexOf('.', StringComparison.Ordinal) + 1)..],
                        row.IsUnique))
                    .ToList(),
                StringComparer.Ordinal);
    }

    private sealed record IndexInfo(string Name, bool IsUnique);

    private sealed class IndexRow
    {
        public string Value { get; set; } = string.Empty;

        public bool IsUnique { get; set; }
    }

    private sealed class SchemaRow
    {        public string Value { get; set; } = string.Empty;

        public string DataType { get; set; } = string.Empty;

        public int Length { get; set; }

        public bool IsNullable { get; set; }
    }
}

