namespace Warden.Core;

/// <summary>
/// Strongly-typed command identifier. Stable, unique per issued command — this is the
/// identity the agent's applied-command dedup set keys on, and what makes redelivery
/// distinguishable from "new command."
/// </summary>
public sealed record CommandId(string Value)
{
    public override string ToString() => Value;

    public static CommandId NewId() => new(Guid.NewGuid().ToString("N"));
}