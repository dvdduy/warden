namespace Warden.Agent;

/// <summary>
/// A session-change subscription only sees logons that happen after the service starts. If the
/// service restarts (crash, update, manual bounce) while someone is already logged in, that
/// session's logon event already fired and is never coming again -- the naive version would
/// leave that user without a user-agent until their next logon. Enumerating already-active
/// sessions at startup closes that gap.
/// </summary>
public interface ISessionEnumerator
{
    IReadOnlyList<int> GetActiveSessionIds();
}
