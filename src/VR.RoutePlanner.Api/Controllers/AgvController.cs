using Microsoft.AspNetCore.Mvc;
using VR.RoutePlanner.Api.Contracts;
using VR.RoutePlanner.Application.Robots;
using VR.RoutePlanner.Application.Traffic;

namespace VR.RoutePlanner.Api.Controllers;

/// <summary>
/// HTTP оператора: заказ, мгновенные действия, состояние и чтение заказа.
/// </summary>
[ApiController]
[Route("api/agv/{serial}")]
public sealed class AgvController : ControllerBase
{
    /// <summary>Код ошибки при активном заказе.</summary>
    public const string OrderInProgress = "ORDER_IN_PROGRESS";

    private static readonly string[] AllowedActions =
    [
        "pause",
        "resume",
        "cancelOrder",
        "startCharging",
        "stopCharging",
    ];

    private readonly RobotOrderService _orders;
    private readonly IRobotStateReader _states;
    private readonly IRobotOrderRepository _repository;

    /// <summary>
    /// Создает контроллер оператора.
    /// </summary>
    /// <param name="orders">Сервис заказов.</param>
    /// <param name="states">Последние состояния роботов.</param>
    /// <param name="repository">Хранилище заказов.</param>
    public AgvController(
        RobotOrderService orders,
        IRobotStateReader states,
        IRobotOrderRepository repository)
    {
        ArgumentNullException.ThrowIfNull(orders);
        ArgumentNullException.ThrowIfNull(states);
        ArgumentNullException.ThrowIfNull(repository);

        _orders = orders;
        _states = states;
        _repository = repository;
    }

    /// <summary>
    /// Создает заказ роботу.
    /// </summary>
    /// <param name="serial">Серийный номер робота.</param>
    /// <param name="body">Параметры заказа.</param>
    /// <param name="ct">Токен отмены.</param>
    /// <returns>Заказ либо код причины.</returns>
    [HttpPost("order")]
    public async Task<IActionResult> CreateOrderAsync(
        string serial,
        [FromBody] AgvOrderBody? body,
        CancellationToken ct)
    {
        if (body is null
            || string.IsNullOrWhiteSpace(body.MapId)
            || string.IsNullOrWhiteSpace(body.TargetNodeId))
        {
            return BadRequestProblem("BAD_REQUEST", "mapId и targetNodeId обязательны.");
        }

        try
        {
            var order = await _orders
                .CreateOrderAsync(body.MapId, serial, body.TargetNodeId, body.HasLoad, ct)
                .ConfigureAwait(false);
            return Ok(AgvDtoMapper.ToResponse(order));
        }
        catch (OrderInProgressException exception)
        {
            return ConflictProblem(OrderInProgress, exception.Message);
        }
        catch (OrderRejectedException exception)
        {
            // Трафик влияет только на выдачу заказа: занятый коридор — 409 в том же формате.
            return string.Equals(
                exception.Reason,
                TrafficReservationResult.ReasonBlocked,
                StringComparison.Ordinal)
                ? ConflictProblem(TrafficReservationResult.ReasonBlocked, exception.Message)
                : NotFoundProblem(exception.Reason, exception.Message);
        }
    }

    /// <summary>
    /// Отправляет мгновенное действие.
    /// </summary>
    /// <param name="serial">Серийный номер робота.</param>
    /// <param name="body">Тип действия.</param>
    /// <param name="ct">Токен отмены.</param>
    /// <returns>Идентификатор действия.</returns>
    [HttpPost("instant-action")]
    public async Task<IActionResult> InstantActionAsync(
        string serial,
        [FromBody] AgvInstantActionBody? body,
        CancellationToken ct)
    {
        if (body is null || string.IsNullOrWhiteSpace(body.ActionType))
        {
            return BadRequestProblem("BAD_REQUEST", "actionType обязателен.");
        }

        if (!AllowedActions.Contains(body.ActionType, StringComparer.Ordinal))
        {
            return BadRequestProblem(
                "UNSUPPORTED_ACTION",
                $"Действие '{body.ActionType}' не поддерживается: {string.Join(", ", AllowedActions)}.");
        }

        var actionId = string.IsNullOrWhiteSpace(body.ActionId) ? Guid.NewGuid().ToString("N") : body.ActionId;
        var sent = await _orders
            .SendInstantActionAsync(serial, body.ActionType, actionId, ct)
            .ConfigureAwait(false);

        return Ok(new AgvInstantActionResponse(sent, true));
    }

    /// <summary>
    /// Отдает последнее состояние робота.
    /// </summary>
    /// <param name="serial">Серийный номер робота.</param>
    /// <param name="ct">Токен отмены.</param>
    /// <returns>Состояние либо 404.</returns>
    [HttpGet("state")]
    public async Task<IActionResult> GetStateAsync(string serial, CancellationToken ct)
    {
        var snapshot = await _states.GetLatestAsync(serial, ct).ConfigureAwait(false);
        return snapshot is null
            ? NotFoundProblem("STATE_NOT_FOUND", $"Состояния робота '{serial}' нет.")
            : Ok(AgvDtoMapper.ToResponse(snapshot));
    }

    /// <summary>
    /// Отдает заказ по идентификатору.
    /// </summary>
    /// <param name="serial">Серийный номер робота.</param>
    /// <param name="orderId">Идентификатор заказа.</param>
    /// <param name="ct">Токен отмены.</param>
    /// <returns>Заказ либо 404.</returns>
    [HttpGet("order/{orderId}")]
    public async Task<IActionResult> GetOrderAsync(string serial, string orderId, CancellationToken ct)
    {
        var order = await _repository.GetAsync(orderId, ct).ConfigureAwait(false);
        if (order is null || !string.Equals(order.AgvSerialNumber, serial, StringComparison.Ordinal))
        {
            return NotFoundProblem("ORDER_NOT_FOUND", $"Заказа '{orderId}' у робота '{serial}' нет.");
        }

        return Ok(AgvDtoMapper.ToResponse(order));
    }

    private IActionResult BadRequestProblem(string code, string message) =>
        base.Problem(detail: message, title: code, statusCode: StatusCodes.Status400BadRequest);

    private IActionResult ConflictProblem(string code, string message) =>
        base.Problem(detail: message, title: code, statusCode: StatusCodes.Status409Conflict);

    private IActionResult NotFoundProblem(string code, string message) =>
        base.Problem(detail: message, title: code, statusCode: StatusCodes.Status404NotFound);
}
