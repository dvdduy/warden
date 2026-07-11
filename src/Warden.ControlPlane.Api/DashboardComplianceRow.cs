using Warden.Core;

namespace Warden.ControlPlane.Api;

public sealed record DashboardComplianceRow(
    string DeviceId,
    string Hostname,
    string Rule,
    string Expected,
    string? Actual,
    DateTimeOffset LastSeen)
{
    /// <summary>
    /// The one policy this "bare" dashboard knows about (see WARDEN_COURSE_MVP.md Session
    /// 5 -- one rule, no scope creep). A second policy would need this to become
    /// per-device/per-rule rather than a single hardcoded key.
    /// </summary>
    public const string BitLockerRuleKey = "bitlocker.enabled";

    public string Status => Actual is null
        ? "Unknown"
        : string.Equals(Actual, Expected, StringComparison.OrdinalIgnoreCase)
            ? "Compliant"
            : "Non-compliant";

    /// <summary>
    /// Builds a row directly from a Device -- the dashboard has no storage of its own; it
    /// reads through IDeviceRepository, the same source of truth ControlPlane already
    /// writes to. That's what keeps this from becoming a second, hand-synced copy of
    /// device state that can silently drift out of sync with the real one.
    /// </summary>
    public static DashboardComplianceRow From(Device device)
    {
        device.Actual.Settings.TryGetValue(BitLockerRuleKey, out var actual);

        return new DashboardComplianceRow(
            DeviceId: device.Id.Value,
            Hostname: device.Hostname,
            Rule: BitLockerRuleKey,
            Expected: "true",
            Actual: actual,
            LastSeen: device.LastSeen);
    }
}
