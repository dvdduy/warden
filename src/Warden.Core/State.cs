namespace Warden.Core;

/// <summary>
/// What a device should look like, as defined by the control plane. A simple settings
/// map is deliberately the entire model — the domain is not the point, the delivery
/// guarantees around reconciling toward it are.
/// </summary>
public sealed record DesiredState(IReadOnlyDictionary<string, string> Settings)
{
    public static DesiredState Empty { get; } = new(new Dictionary<string, string>());
}

/// <summary>
/// What a device actually looks like, as last reported (or locally held, on the agent side).
/// </summary>
public sealed record ActualState(IReadOnlyDictionary<string, string> Settings)
{
    public static ActualState Empty { get; } = new(new Dictionary<string, string>());
}