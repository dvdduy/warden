using System.Diagnostics;
using System.IO.Pipes;
using Warden.Ipc;

var sessionId = Process.GetCurrentProcess().SessionId;
var pipeName = PipeNames.ForSession(sessionId);

using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

Console.WriteLine($"Connecting to \\\\.\\pipe\\{pipeName} (session {sessionId}) ...");
await client.ConnectAsync((int)TimeSpan.FromSeconds(5).TotalMilliseconds);
Console.WriteLine("Connected. Sending Ping.");

await PipeMessageProtocol.WriteAsync(client, PipeMessage.Ping);
var response = await PipeMessageProtocol.ReadAsync(client);

Console.WriteLine(response is null
    ? "Server closed the connection without replying."
    : $"Received: {response.Type}");
