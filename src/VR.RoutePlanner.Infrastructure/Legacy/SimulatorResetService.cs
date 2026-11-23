using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using VR.RoutePlanner.Application.Robots;

namespace VR.RoutePlanner.Infrastructure.Legacy;

/// <summary>
/// Перезапускает unit симулятора через sudo (правило sudoers добавляется на стенде).
/// </summary>
public sealed class SimulatorResetService(
    IConfiguration configuration,
    ILogger<SimulatorResetService> logger) : ISimulatorResetService
{
    private readonly string? _unit = configuration["Legacy:SimulatorUnit"];

    /// <inheritdoc />
    public bool Enabled => !string.IsNullOrWhiteSpace(_unit);

    /// <inheritdoc />
    public async Task ResetAsync(CancellationToken ct)
    {
        if (!Enabled)
        {
            return;
        }

        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "sudo",
            Arguments = $"-n systemctl restart {_unit}",
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        });

        if (process is not null)
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                logger.LogWarning(
                    "Не удалось перезапустить {Unit}: {Error}",
                    _unit,
                    await process.StandardError.ReadToEndAsync(ct).ConfigureAwait(false));
            }
        }

        await Task.Delay(TimeSpan.FromSeconds(25), ct).ConfigureAwait(false);
    }
}
