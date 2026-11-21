using System.Text.Json;

namespace VR.RoutePlanner.Tests.Contracts;

/// <summary>
/// Проверка примеров сообщений из <c>docs/contracts/samples</c>: файл — ровно один валидный
/// JSON с обязательными полями канала.
/// </summary>
public sealed class SampleMessageTests
{
    /// <summary>Все примеры сообщений на месте.</summary>
    [Fact]
    public void Samples_ContainAllExpectedFiles()
    {
        string[] expected =
        [
            "mqtt-connection.json",
            "mqtt-state.json",
            "mqtt-visualization.json",
            "kafka-simple-tasks.json",
            "kafka-tasks-events.json",
            "kafka-agv-waiting.json",
        ];

        var actual = Directory
            .GetFiles(ContractFiles.SamplesDirectory, "*.json")
            .Select(Path.GetFileName)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expected.OrderBy(name => name, StringComparer.Ordinal), actual);

        foreach (var file in actual)
        {
            Assert.NotNull(file);
        }
    }

    /// <summary>Пример валиден как JSON-объект и содержит обязательные поля своего канала.</summary>
    /// <param name="fileName">Имя файла в <c>samples</c>.</param>
    /// <param name="requiredFields">Обязательные поля верхнего уровня.</param>
    [Theory]
    [InlineData("mqtt-connection.json", new[] { "headerId", "timestamp", "version", "serialNumber" })]
    [InlineData("mqtt-state.json", new[] { "headerId", "timestamp", "version", "serialNumber", "orderId" })]
    [InlineData("mqtt-visualization.json", new[] { "headerId", "timestamp", "version", "serialNumber" })]
    [InlineData("kafka-simple-tasks.json", new[] { "taskId", "status", "actions" })]
    [InlineData("kafka-tasks-events.json", new[] { "taskId", "status" })]
    [InlineData("kafka-agv-waiting.json", new[] { "OrderId", "Status", "AgvSerialNumber" })]
    public void Sample_ParsesAndHasRequiredFields(string fileName, string[] requiredFields)
    {
        var path = Path.Combine(ContractFiles.SamplesDirectory, fileName);
        Assert.True(File.Exists(path), $"нет примера сообщения: {path}");

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);

        foreach (var field in requiredFields)
        {
            Assert.True(
                document.RootElement.TryGetProperty(field, out _),
                $"{fileName}: нет обязательного поля {field}");
        }
    }

    /// <summary>MQTT-примеры несут версию протокола VDA5050.</summary>
    [Theory]
    [InlineData("mqtt-connection.json")]
    [InlineData("mqtt-state.json")]
    [InlineData("mqtt-visualization.json")]
    public void MqttSample_DeclaresProtocolVersion(string fileName)
    {
        var path = Path.Combine(ContractFiles.SamplesDirectory, fileName);
        using var document = JsonDocument.Parse(File.ReadAllText(path));

        Assert.Equal("2.0.0", document.RootElement.GetProperty("version").GetString());
        Assert.Equal("Tuskrobots", document.RootElement.GetProperty("manufacturer").GetString());
    }
}
