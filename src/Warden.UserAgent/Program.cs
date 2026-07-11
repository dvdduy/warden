using System.IO.Pipes;
using Warden.Ipc;

using var client = new NamedPipeClientStream(".", PipeNames.Default, PipeDirection.InOut, PipeOptions.Asynchronous);

Console.WriteLine($"Connecting to \\\\.\\pipe\\{PipeNames.Default} ...");
await client.ConnectAsync((int)TimeSpan.FromSeconds(5).TotalMilliseconds);
Console.WriteLine("Connected. Sending Ping.");

await PipeMessageProtocol.WriteAsync(client, PipeMessage.Ping);
var response = await PipeMessageProtocol.ReadAsync(client);

Console.WriteLine(response is null
    ? "Server closed the connection without replying."
    : $"Received: {response.Type}");
