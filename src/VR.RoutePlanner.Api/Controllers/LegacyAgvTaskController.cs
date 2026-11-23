using Microsoft.AspNetCore.Mvc;
using VR.RoutePlanner.Api.Contracts;
using VR.RoutePlanner.Application.Robots;

namespace VR.RoutePlanner.Api.Controllers;

/// <summary>
/// Шим легаси <c>api/AgvTask</c>: админка и WMS запрашивают последний узел робота.
///
/// В AGVControl эндпоинт отвечал 404, если у робота нет узла или карты, из-за чего админка
/// показывала ошибку. Здесь ответ всегда 200: объект узла либо <c>null</c>.
/// Резолв узла общий с трафиком: <see cref="IRobotLastNodeResolver"/>.
/// </summary>
[ApiController]
[Route("api/AgvTask")]
public sealed class LegacyAgvTaskController(IRobotLastNodeResolver lastNodes) : ControllerBase
{
    /// <summary>
    /// Отдает последний узел робота из его состояния (<c>state.lastNodeId</c>).
    /// </summary>
    /// <param name="serial">Серийный номер робота.</param>
    /// <param name="ct">Токен отмены.</param>
    /// <returns>Всегда 200: узел либо <c>null</c>.</returns>
    [HttpGet("lastnode/{serial}")]
    public async Task<IActionResult> LastNodeAsync(string serial, CancellationToken ct)
    {
        var node = await lastNodes.ResolveAsync(serial, ct).ConfigureAwait(false);
        if (node is null)
        {
            return Ok(null);
        }

        return Ok(new AgvNodeResponse(
            node.Id,
            node.NodeId,
            "{}",
            node.MapKey,
            node.X,
            node.Y,
            node.Theta,
            node.AllowedDeviationXY,
            node.AllowedDeviationTheta,
            node.NodeType,
            null,
            null,
            null,
            null));
    }
}
