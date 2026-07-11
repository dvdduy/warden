using System.Security.Principal;
using Microsoft.Extensions.Options;
using Warden.Agent.Interop;

namespace Warden.Agent;

public sealed class CreateProcessAsUserLauncher : ISessionUserAgentLauncher
{
    private readonly string _executablePath;

    public CreateProcessAsUserLauncher(IOptions<AgentServiceOptions> options)
    {
        _executablePath = options.Value.UserAgentExecutablePath;
    }

    public ISessionUserAgentHandle Launch(int sessionId)
    {
        var userToken = Wtsapi32.QueryUserToken(sessionId);
        try
        {
            using var identity = new WindowsIdentity(userToken.DangerousGetHandle());
            var userSid = identity.User
                ?? throw new InvalidOperationException($"Could not resolve the user SID for session {sessionId}.");

            using var environmentBlock = UserEnvironmentBlock.CreateFor(userToken);

            var processInfo = Advapi32.CreateProcessAsCurrentUser(
                userToken,
                commandLine: $"\"{_executablePath}\"",
                currentDirectory: Path.GetDirectoryName(_executablePath),
                environmentBlock.Handle);

            Kernel32.CloseHandle(processInfo.Thread);

            return new SessionUserAgentHandle(sessionId, processInfo.ProcessId, processInfo.Process, userSid, userToken);
        }
        catch
        {
            userToken.Dispose();
            throw;
        }
    }

    private sealed class SessionUserAgentHandle : ISessionUserAgentHandle
    {
        private readonly nint _processHandle;
        private readonly Microsoft.Win32.SafeHandles.SafeAccessTokenHandle _userToken;

        public SessionUserAgentHandle(
            int sessionId,
            int processId,
            nint processHandle,
            SecurityIdentifier userSid,
            Microsoft.Win32.SafeHandles.SafeAccessTokenHandle userToken)
        {
            SessionId = sessionId;
            ProcessId = processId;
            UserSid = userSid;
            _processHandle = processHandle;
            _userToken = userToken;
        }

        public int SessionId { get; }

        public int ProcessId { get; }

        public SecurityIdentifier UserSid { get; }

        public void Dispose()
        {
            // A real session logoff already tears down that session's processes at the OS
            // level; closing our handles here just releases what we hold without needing to
            // force-terminate a process that -- in the logoff case -- is usually already gone.
            Kernel32.CloseHandle(_processHandle);
            _userToken.Dispose();
        }
    }
}
