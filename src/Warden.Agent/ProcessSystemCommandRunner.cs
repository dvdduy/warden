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

        // Start draining both streams concurrently before waiting for exit. Reading them
        // fully one after another (stdout then stderr) can deadlock if the child writes
        // enough to *both* streams to fill an OS pipe buffer: the child blocks writing to
        // whichever stream isn't being read yet, and the parent blocks waiting for the
        // stream it's on to reach EOF -- which never happens because the child never gets
        // to exit.
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();

        process.WaitForExit();
        Task.WaitAll(outputTask, errorTask);

        return new CommandResult(process.ExitCode, outputTask.Result, errorTask.Result);
    }
}
