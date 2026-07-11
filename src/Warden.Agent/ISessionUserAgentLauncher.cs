using System.Security.Principal;

namespace Warden.Agent;

/// <summary>
/// A running <c>Warden.UserAgent</c> process launched for one logged-on session, plus what
/// <see cref="SessionAgentManager"/> needs to build that session's ACL'd pipe.
/// </summary>
public interface ISessionUserAgentHandle : IDisposable
{
    int SessionId { get; }

    int ProcessId { get; }

    SecurityIdentifier UserSid { get; }

    Task WaitForExitAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Launches <c>Warden.UserAgent</c> inside a specific logged-on session, as that session's user
/// -- never as whatever account is running the service. The real implementation
/// (<see cref="CreateProcessAsUserLauncher"/>) needs <c>SeTcbPrivilege</c>, which only
/// <c>LocalSystem</c> holds; tests use a fake that skips the Win32 calls entirely.
/// </summary>
public interface ISessionUserAgentLauncher
{
    ISessionUserAgentHandle Launch(int sessionId);
}
