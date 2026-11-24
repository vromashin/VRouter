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

        // Сначала останавливаем сервис: при остановке симулятор сохраняет текущее (дрейфующее)
        // состояние в файл, поэтому копию нужно класть уже после остановки, иначе она затирается.
        await RunAsync($"-n systemctl stop {_unit}", ct).ConfigureAwait(false);
        await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(backup) && !string.IsNullOrWhiteSpace(stateFile))
        {
            await RunAsync($"-n cp {backup} {stateFile}", ct).ConfigureAwait(false);
            await RunAsync($"-n chown root:root {stateFile}", ct).ConfigureAwait(false);
        }

        await RunAsync($"-n systemctl start {_unit}", ct).ConfigureAwait(false);
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
