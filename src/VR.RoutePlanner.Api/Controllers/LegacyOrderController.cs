using Microsoft.AspNetCore.Mvc;
using VR.RoutePlanner.Application.Robots;
using VR.RoutePlanner.Domain.Robots;

namespace VR.RoutePlanner.Api.Controllers;

/// <summary>
/// Совместимость с AGVControl для админки: те же пути и форма ответа, что у старого сервиса.
/// Заказы и команды уходят в наш сервис заказов.
/// </summary>
[ApiController]
public sealed class LegacyOrderController(RobotOrderService orders) : ControllerBase
{
    /// <summary>Заказ на узел: <c>POST /api/Order/move-to-node/{serial}/{nodeId}/{dir}</c>.</summary>
    [HttpPost("api/Order/move-to-node/{serial}/{nodeId}/{direction}")]
    [HttpPost("api/Order/move-to-node/{serial}/{nodeId}")]
    public async Task<IActionResult> MoveToNodeAsync(
        string serial,
        string nodeId,
        string? direction,
        [FromQuery] string mapId = "801",
        CancellationToken ct = default)
    {
        try
        {
            var order = await orders.CreateOrderAsync(serial, mapId, nodeId, hasLoad: false, ct).ConfigureAwait(false);
            return Ok(ApiResponse(order));
        }
        catch (OrderInProgressException exception)
        {
            return Ok(Failure(exception.Message));
        }
        catch (OrderRejectedException exception)
        {
            return Ok(Failure($"{exception.Reason}: {exception.Message}"));
        }
    }

    /// <summary>Заказ на зарядку: <c>POST /api/Order/move-to-charge/{serial}</c>.</summary>
    [HttpPost("api/Order/move-to-charge/{serial}")]
    public async Task<IActionResult> MoveToChargeAsync(string serial, CancellationToken ct)
    {
        try
        {
            var result = await orders
                .SendInstantActionAsync(serial, "startCharging", Guid.NewGuid().ToString("N"), ct)
                .ConfigureAwait(false);
            return Ok(new { success = true, actionId = result, agvSerialNumber = serial, message = "startCharging" });
        }
        catch (OrderRejectedException exception)
        {
            return Ok(Failure($"{exception.Reason}: {exception.Message}"));
        }
    }

    /// <summary>Команда роботу: <c>POST /api/AgvCommand/{serial}/{command}</c>.</summary>
    [HttpPost("api/AgvCommand/{serial}/{command}")]
    public async Task<IActionResult> CommandAsync(string serial, string command, CancellationToken ct)
    {
        var actionType = command switch
        {
            "cancel" or "cancelOrder" => "cancelOrder",
            "pause" => "pause",
            "resume" => "resume",
            "stateRequest" => "stateRequest",
            "startCharging" => "startCharging",
            "stopCharging" => "stopCharging",
            _ => null,
        };

        if (actionType is null)
        {
            return Ok(Failure($"UNKNOWN_COMMAND: {command}"));
        }

        try
        {
            var actionId = await orders
                .SendInstantActionAsync(serial, actionType, Guid.NewGuid().ToString("N"), ct)
                .ConfigureAwait(false);
            return Ok(new { success = true, actionId, agvSerialNumber = serial, message = actionType });
        }
        catch (OrderRejectedException exception)
        {
            return Ok(Failure($"{exception.Reason}: {exception.Message}"));
        }
    }

    private object ApiResponse(RobotOrder order) => new
    {
        success = true,
        orderId = order.OrderId,
        orderUpdateId = order.OrderUpdateId,
        sequenceId = 0,
        agvSerialNumber = order.AgvSerialNumber,
        message = $"Заказ {order.TargetNodeId ?? "-"} принят",
    };

    private static object Failure(string message) => new
    {
        success = false,
        orderId = (string?)null,
        orderUpdateId = 0,
        sequenceId = 0,
        agvSerialNumber = (string?)null,
        message,
    };
}
