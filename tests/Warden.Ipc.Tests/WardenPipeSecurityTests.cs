using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using Warden.Ipc;

namespace Warden.Ipc.Tests;

public class WardenPipeSecurityTests
{
    /// <summary>
    /// Spinning up a second local Windows account to prove "an unauthorized SID is refused"
    /// end to end isn't practical for a single-machine automated test run, so this asserts the
    /// DACL <see cref="WardenPipeSecurity"/> builds directly: only LocalSystem and the one
    /// allowed user SID are granted access. Nothing else -- notably not Everyone or
    /// Authenticated Users -- appears at all, which is exactly what makes every other SID on
    /// the box get the OS's default AccessDenied without needing an explicit Deny rule (see the
    /// comment on <see cref="WardenPipeSecurity"/> for why an explicit Deny would actually be
    /// wrong here).
    /// </summary>
    [Fact]
    public void Only_LocalSystem_and_the_allowed_user_are_granted_access()
    {
        var allowedUserSid = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        var localSystemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);

        var security = WardenPipeSecurity.Create(allowedUserSid);

        var rules = security.GetAccessRules(true, false, typeof(SecurityIdentifier))
            .Cast<PipeAccessRule>()
            .ToList();

        Assert.All(rules, rule => Assert.Equal(AccessControlType.Allow, rule.AccessControlType));

        var grantedSids = rules.Select(r => (SecurityIdentifier)r.IdentityReference).ToList();
        Assert.Contains(localSystemSid, grantedSids);
        Assert.Contains(allowedUserSid, grantedSids);
        Assert.Equal(2, grantedSids.Count);
        Assert.DoesNotContain(grantedSids, sid => sid.IsWellKnown(WellKnownSidType.WorldSid));
        Assert.DoesNotContain(grantedSids, sid => sid.IsWellKnown(WellKnownSidType.AuthenticatedUserSid));
    }
}
