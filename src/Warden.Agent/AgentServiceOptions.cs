namespace Warden.Agent;

public sealed class AgentServiceOptions
{
    public string DeviceId { get; set; } = $"dev-{Environment.MachineName}";

    public string Hostname { get; set; } = Environment.MachineName;

    public Uri ControlPlaneBaseAddress { get; set; } = new("http://localhost:5000");

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(60);

    public string BitLockerVolume { get; set; } = "C:";

    public bool UseFakeBitLocker { get; set; }

    public bool FakeBitLockerEnabled { get; set; }

    /// <summary>
    /// Assumes Warden.UserAgent.exe is deployed next to Warden.Agent's own binaries, which is
    /// true for this single-repo build; an installer/MSIX package would set this explicitly.
    /// </summary>
    public string UserAgentExecutablePath { get; set; } =
        Path.Combine(AppContext.BaseDirectory, "Warden.UserAgent.exe");

    public TimeSpan UserAgentRestartInitialBackoff { get; set; } = TimeSpan.FromSeconds(2);

    public TimeSpan UserAgentRestartMaxBackoff { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan UserAgentCrashLoopWindow { get; set; } = TimeSpan.FromMinutes(1);

    public int UserAgentMaxCrashesInWindow { get; set; } = 3;
}
