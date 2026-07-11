using System.Diagnostics;

namespace Warden.Agent;

public sealed class ProcessSystemCommandRunner : ISystemCommandRunner
{
    public CommandResult Run(string fileName, IReadOnlyList<string> arguments)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        }.WithArguments(arguments));

        if (process is null)
        {
            throw new InvalidOperationException($"Failed to start {fileName}.");
        }

        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        return new CommandResult(process.ExitCode, output, error);
    }
}
