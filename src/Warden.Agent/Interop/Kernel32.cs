using System.Runtime.InteropServices;

namespace Warden.Agent.Interop;

internal static class Kernel32
{
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(nint handle);
}
