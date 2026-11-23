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

        var backup = configuration["Legacy:SimulatorStateBackup"];
        var stateFile = configuration["Legacy:SimulatorStateFile"];
        if (!string.IsNullOrWhiteSpace(backup) && !string.IsNullOrWhiteSpace(stateFile))
        {
            // Возвращаем выровненное состояние: симулятор сохраняет дрейф в этом файле.
            await RunAsync($"-n cp {backup} {stateFile}", ct).ConfigureAwait(false);
            await RunAsync($"-n chown root:root {stateFile}", ct).ConfigureAwait(false);
        }

        await RunAsync($"-n systemctl restart {_unit}", ct).ConfigureAwait(false);
        await Task.Delay(TimeSpan.FromSeconds(25), ct).ConfigureAwait(false);
    }

    private async Task RunAsync(string arguments, CancellationToken ct)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "sudo",
            Arguments = arguments,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        });

        if (process is null)
        {
            return;
        }

        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            logger.LogWarning(
                "Команда sudo {Arguments} завершилась с кодом {Code}: {Error}",
                arguments,
                process.ExitCode,
                await process.StandardError.ReadToEndAsync(ct).ConfigureAwait(false));
        }
    }
}
