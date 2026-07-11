using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Warden.Agent.Interop;

/// <summary>
/// A process launched via CreateProcessAsUser doesn't inherit a sane environment block for the
/// target user by default (PATH, TEMP, USERPROFILE, etc. would leak from LocalSystem's own
/// environment instead) -- CreateEnvironmentBlock builds the correct one for that user's token.
/// </summary>
internal sealed class UserEnvironmentBlock : IDisposable
{
    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool CreateEnvironmentBlock(out nint environment, SafeAccessTokenHandle token, bool inherit);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool DestroyEnvironmentBlock(nint environment);

    public nint Handle { get; }

    private UserEnvironmentBlock(nint handle) => Handle = handle;

    public static UserEnvironmentBlock CreateFor(SafeAccessTokenHandle userToken)
    {
        if (!CreateEnvironmentBlock(out var environment, userToken, inherit: false))
        {
            throw new Win32Exception("CreateEnvironmentBlock failed");
        }

        return new UserEnvironmentBlock(environment);
    }

    public void Dispose() => DestroyEnvironmentBlock(Handle);
}
