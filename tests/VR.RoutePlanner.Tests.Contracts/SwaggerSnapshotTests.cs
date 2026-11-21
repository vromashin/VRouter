using System.Text.Json;

namespace VR.RoutePlanner.Tests.Contracts;

/// <summary>
/// Машинная проверка снимка OpenAPI и чек-листа паритета. Сеть и база не используются.
/// </summary>
public sealed class SwaggerSnapshotTests
{
    /// <summary>Снимок содержит ровно тот набор операций и схем, который зафиксирован в спеке.</summary>
    [Fact]
    public void Snapshot_HasExpectedOperationAndSchemaCounts()
    {
        using var document = ContractFiles.OpenSwagger();
        var root = document.RootElement;

        Assert.Equal("3.0.4", root.GetProperty("openapi").GetString());

        var operations = 0;
        var paths = root.GetProperty("paths");
        foreach (var path in paths.EnumerateObject())
        {
            foreach (var operation in path.Value.EnumerateObject())
            {
                if (operation.Name is "get" or "post" or "put" or "delete" or "patch")
                {
                    operations++;
                }
            }
        }

        Assert.Equal(109, operations);
        Assert.Equal(32, root.GetProperty("components").GetProperty("schemas").EnumerateObject().Count());
    }

    /// <summary>
    /// Каждый путь уровня 1 из чек-листа присутствует в снимке с тем же методом.
    /// Имена параметров пути не сравниваются: в снимке они длиннее.
    /// </summary>
    [Fact]
    public void Checklist_Level1Contracts_ExistInSnapshot()
    {
        using var document = ContractFiles.OpenSwagger();
        var snapshot = ReadSnapshot(document.RootElement.GetProperty("paths"));

        var level1 = ContractFiles.ReadChecklist().Where(row => row.Level == 1).ToList();
        Assert.NotEmpty(level1);

        var problems = new List<string>();
        foreach (var row in level1)
        {
            var key = (row.Method, ContractFiles.Normalize(row.Path));
            if (!snapshot.Contains(key))
            {
                problems.Add($"{row.Method} {row.Path}");
            }
        }

        Assert.True(
            problems.Count == 0,
            "в снимке отсутствуют контракты уровня 1 из чек-листа: " + string.Join(", ", problems));
    }

    /// <summary>
    /// Уровень 2 фиксируется справочно: пути, которых в снимке нет, не валят тест,
    /// а те, что есть, обязаны совпадать по методу.
    /// </summary>
    [Fact]
    public void Checklist_Level2Contracts_ShownButNotRequired()
    {
        using var document = ContractFiles.OpenSwagger();
        var snapshot = ReadSnapshot(document.RootElement.GetProperty("paths"));

        var level2 = ContractFiles.ReadChecklist().Where(row => row.Level == 2).ToList();
        Assert.NotEmpty(level2);

        var problems = new List<string>();
        foreach (var row in level2)
        {
            var normalized = ContractFiles.Normalize(row.Path);
            var sameMethod = snapshot.Contains((row.Method, normalized));
            var otherMethod = snapshot.Any(entry => entry.Path == normalized && entry.Method != row.Method);
            if (otherMethod && !sameMethod)
            {
                problems.Add($"{row.Method} {row.Path}");
            }
        }

        Assert.True(
            problems.Count == 0,
            "у путей уровня 2 в снимке другой метод: " + string.Join(", ", problems));
    }

    private static HashSet<(string Method, string Path)> ReadSnapshot(JsonElement paths)
    {
        var result = new HashSet<(string Method, string Path)>();
        foreach (var path in paths.EnumerateObject())
        {
            foreach (var operation in path.Value.EnumerateObject())
            {
                if (operation.Name is "get" or "post" or "put" or "delete" or "patch")
                {
                    result.Add((operation.Name.ToUpperInvariant(), ContractFiles.Normalize(path.Name)));
                }
            }
        }

        return result;
    }
}
