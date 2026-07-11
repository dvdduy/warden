namespace Warden.Agent;

public sealed record CommandResult(int ExitCode, string StandardOutput, string StandardError);
