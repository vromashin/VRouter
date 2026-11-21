using Microsoft.AspNetCore.Mvc;
using VR.RoutePlanner.Infrastructure.Legacy;

namespace VR.RoutePlanner.Api.Controllers;

/// <summary>
/// Шим легаси Robot API, который раньше слушал порт 5143 и отдавал данные админке
/// через WMS. Форма ответа совпадает с <c>NTLS.RMS.Definitions.Entities.Agv</c>.
/// </summary>
[ApiController]
[Route("api/v1/Robot")]
public sealed class LegacyRobotController(ILegacyAgvRepository repository) : ControllerBase
{
    [HttpGet("List")]
    public async Task<IActionResult> ListAsync(CancellationToken ct) =>
        Ok(await repository.ListAsync(ct));

    [HttpGet("{id:guid}/State")]
    public async Task<IActionResult> StateAsync(Guid id, CancellationToken ct)
    {
        var record = await repository.FindAsync(id, ct);
        return record is null ? NotFound() : Ok(record);
    }

    [HttpGet("State")]
    public async Task<IActionResult> StateAllAsync(CancellationToken ct) =>
        Ok(await repository.ListAsync(ct));
}
