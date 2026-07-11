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
}
