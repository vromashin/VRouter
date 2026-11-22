using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using VR.RoutePlanner.Infrastructure.Mqtt;

namespace VR.RoutePlanner.Api.Controllers;

/// <summary>
/// Совместимость с AGVControl по визуализации: карта опрашивает
/// <c>/api/Visualization/rawVisualizationAll</c> каждые 200 мс и рисует роботов.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public sealed class VisualizationController(MqttRobotStateReader reader) : ControllerBase
{
    /// <summary>Сырые сообщения visualization по всем роботам: серийный номер → JSON.</summary>
    [HttpGet("rawVisualizationAll")]
    public IActionResult GetRawAll()
    {
        // Карта ждет объект вида { "98001": <сырое VDA-сообщение>, ... },
        // то есть значения должны быть JSON-объектами, а не строками.
        var root = new System.Text.Json.Nodes.JsonObject();
        foreach (var pair in reader.GetRawVisualizations())
        {
            try
            {
                root[pair.Key] = System.Text.Json.Nodes.JsonNode.Parse(pair.Value);
            }
            catch (JsonException)
            {
                // Битое сообщение пропускаем: карте нужен валидный JSON.
            }
        }

        return Content(root.ToJsonString(), "application/json");
    }

    /// <summary>Сырое сообщение visualization конкретного робота.</summary>
    [HttpGet("{serialNumber}")]
    public IActionResult GetRaw(string serialNumber)
    {
        var raw = reader.GetRawVisualizations();
        if (!raw.TryGetValue(serialNumber, out var payload))
        {
            return NotFound(new { error = $"Нет визуализации робота {serialNumber}" });
        }

        return Content(payload, "application/json");
    }
}
