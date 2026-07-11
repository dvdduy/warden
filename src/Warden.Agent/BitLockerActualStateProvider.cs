using System.Diagnostics;
using Microsoft.Extensions.Options;
using Warden.Core;

namespace Warden.Agent;

public sealed class BitLockerActualStateProvider : IActualStateProvider
{
    private readonly string _volume;

    public BitLockerActualStateProvider(IOptions<AgentServiceOptions> options) =>
        _volume = options.Value.BitLockerVolume;

    public ActualState GetActualState()
    {
        var output = RunManageBdeStatus();
        var enabled = BitLockerStatusParser.IsProtectionEnabled(output);

        return new ActualState(new Dictionary<string, string>
        {
            [BitLockerPolicy.EnabledKey] = enabled ? "true" : "false"
        });
    }

    private string RunManageBdeStatus()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("BitLocker status can only be queried on Windows.");
        }

        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "manage-bde",
            ArgumentList = { "-status", _volume },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        });

        if (process is null)
        {
            throw new InvalidOperationException("Failed to start manage-bde.");
        }

        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"manage-bde -status {_volume} failed with exit code {process.ExitCode}: {error}");
        }

        return output;
    }
}
