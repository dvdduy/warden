namespace Warden.ControlPlane;

/// <summary>
/// A point-in-time tally of every command in the store by status, across the whole
/// fleet. The simplest possible health signal: PendingCommands + DeliveredCommands is
/// "work still in flight"; a persistently non-zero FailedCommands count is what an
/// operator (or a dashboard, in a later milestone) would alert on.
/// </summary>
public sealed record FleetHealth(
    int PendingCommands,
    int DeliveredCommands,
    int AckedCommands,
    int FailedCommands)
{
    public int TotalCommands => PendingCommands + DeliveredCommands + AckedCommands + FailedCommands;

    /// <summary>Commands not yet in a terminal state — the closest thing to "work outstanding."</summary>
    public int InFlightCommands => PendingCommands + DeliveredCommands;
}
