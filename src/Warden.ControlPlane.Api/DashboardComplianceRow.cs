namespace Warden.ControlPlane.Api;

public sealed record DashboardComplianceRow(
    string DeviceId,
    string Hostname,
    string Rule,
    string Expected,
    string? Actual,
    DateTimeOffset LastSeen)
{
    public string Status => Actual is null
        ? "Unknown"
        : string.Equals(Actual, Expected, StringComparison.OrdinalIgnoreCase)
            ? "Compliant"
            : "Non-compliant";
}
