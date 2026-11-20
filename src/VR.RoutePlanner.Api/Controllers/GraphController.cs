using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using VR.RoutePlanner.Application.Configuration;
using VR.RoutePlanner.Application.Startup;

namespace VR.RoutePlanner.Api.Controllers;

/// <summary>
/// Прогрев ресурсов карты и статус последнего прогрева.
/// </summary>
[ApiController]
[Route("api/graph")]
public sealed class GraphController : ControllerBase
{
    private readonly IGraphWarmupService _warmup;
    private readonly IOptions<WarmupOptions> _options;

    /// <summary>
    /// Создает контроллер прогрева.
    /// </summary>
    /// <param name="warmup">Оркестратор прогрева.</param>
    /// <param name="options">Параметры прогрева.</param>
    public GraphController(IGraphWarmupService warmup, IOptions<WarmupOptions> options)
    {
        ArgumentNullException.ThrowIfNull(warmup);
        ArgumentNullException.ThrowIfNull(options);

        _warmup = warmup;
        _options = options;
    }

    /// <summary>
    /// Запускает прогрев ресурсов карты из <c>Routing:Warmup:MapId</c>.
    /// </summary>
    /// <param name="ct">Токен отмены запроса.</param>
    /// <returns>Итог прогрева.</returns>
    [HttpPost("warmup")]
    public async Task<ActionResult<WarmupResult>> WarmupAsync(CancellationToken ct)
    {
        try
        {
            var result = await _warmup.WarmupAsync(_options.Value.MapId, ct).ConfigureAwait(false);
            return Ok(result);
        }
        catch (WarmupStepException exception)
        {
            return Problem(
                title: "Прогрев ресурсов не выполнен",
                detail: exception.Message,
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    /// <summary>
    /// Отдает итог последнего прогрева.
    /// </summary>
    /// <returns>Итог прогрева либо <c>404</c>, если прогрева еще не было.</returns>
    [HttpGet("status")]
    public ActionResult<WarmupResult> GetStatus()
    {
        return _warmup.LastResult is { } result ? Ok(result) : NotFound();
    }
}
