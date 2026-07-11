using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Warden.Agent.Interop;

[Flags]
internal enum ProcessCreationFlags : uint
{
    CreateUnicodeEnvironment = 0x00000400,
    CreateNoWindow = 0x08000000,
    CreateNewConsole = 0x00000010,
}

[StructLayout(LayoutKind.Sequential)]
internal struct SecurityAttributes
{
    public int Length;
    public nint SecurityDescriptor;
    public bool InheritHandle;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct StartupInfo
{
    public int Cb;
    public string? Reserved;
    public string? Desktop;
    public string? Title;
    public int X;
    public int Y;
    public int XSize;
    public int YSize;
    public int XCountChars;
    public int YCountChars;
    public int FillAttribute;
    public int Flags;
    public short ShowWindow;
    public short Reserved2;
    public nint Reserved3;
    public nint StdInput;
    public nint StdOutput;
    public nint StdError;
}

[StructLayout(LayoutKind.Sequential)]
internal struct ProcessInformation
{
    public nint Process;
    public nint Thread;
    public int ProcessId;
    public int ThreadId;
}

internal static class Advapi32
{
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessAsUser(
        SafeAccessTokenHandle hToken,
        string? applicationName,
        string commandLine,
        ref SecurityAttributes processAttributes,
        ref SecurityAttributes threadAttributes,
        bool inheritHandles,
        ProcessCreationFlags creationFlags,
        nint environment,
        string? currentDirectory,
        ref StartupInfo startupInfo,
        out ProcessInformation processInformation);

    /// <summary>
    /// Launches <paramref name="commandLine"/> as the user represented by
    /// <paramref name="userToken"/>, inside that user's session -- not as whatever account is
    /// running the calling process. This, like <see cref="Wtsapi32.QueryUserToken"/>, requires
    /// <c>SeTcbPrivilege</c>, so it only actually works when the caller is <c>LocalSystem</c>.
    /// </summary>
    internal static ProcessInformation CreateProcessAsCurrentUser(
        SafeAccessTokenHandle userToken,
        string commandLine,
        string? currentDirectory,
        nint environmentBlock)
    {
        var processAttributes = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>() };
        var threadAttributes = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>() };
        var startupInfo = new StartupInfo { Cb = Marshal.SizeOf<StartupInfo>(), Desktop = "winsta0\\default" };

        var created = CreateProcessAsUser(
            userToken,
            applicationName: null,
            commandLine: commandLine,
            processAttributes: ref processAttributes,
            threadAttributes: ref threadAttributes,
            inheritHandles: false,
            creationFlags: ProcessCreationFlags.CreateUnicodeEnvironment | ProcessCreationFlags.CreateNoWindow,
            environment: environmentBlock,
            currentDirectory: currentDirectory,
            startupInfo: ref startupInfo,
            processInformation: out var processInformation);

        if (!created)
        {
            throw new Win32Exception("CreateProcessAsUser failed");
        }

        return processInformation;
    }
}
