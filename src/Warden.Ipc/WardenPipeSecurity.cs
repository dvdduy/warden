using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Warden.Ipc;

/// <summary>
/// Builds the ACL for the service-side named pipe: only <c>LocalSystem</c> and the one user
/// SID this pipe was created for may connect. Everyone else gets the OS's default
/// <c>AccessDenied</c> -- there is deliberately no explicit "deny Everyone" rule added on top
/// of that allow-list.
///
/// That's not an oversight: Windows canonicalizes a DACL with all explicit Deny ACEs ordered
/// before all explicit Allow ACEs, and access checks stop at the first ACE that matches any
/// SID in the caller's token. The allowed user's token still contains the "Everyone" SID, so a
/// "Deny Everyone" ACE would be evaluated first and silently cancel out their own "Allow" rule
/// further down the list -- the exact kind of ACL misconfiguration this session exists to avoid,
/// not reintroduce. Omitting Everyone/Authenticated Users from the DACL entirely already denies
/// them by default; that's the correct way to express "nobody else," not a redundant Deny ACE.
/// </summary>
public static class WardenPipeSecurity
{
    public static PipeSecurity Create(SecurityIdentifier allowedUserSid)
    {
        var localSystemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);

        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(localSystemSid, PipeAccessRights.ReadWrite, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(allowedUserSid, PipeAccessRights.ReadWrite, AccessControlType.Allow));

        return security;
    }
}
