using System.IO.Pipes;
using Warden.Ipc;

namespace Warden.Ipc.Tests;

public class PipeMessageProtocolTests
{
    [Fact]
    public void ComplianceChanged_payload_round_trips()
    {
        var message = PipeMessage.ComplianceChanged("bitlocker.enabled", "Compliant");

        var payload = message.TryGetComplianceChangedPayload();

        Assert.Equal("ComplianceChanged", message.Type);
        Assert.Equal("bitlocker.enabled", payload?.Rule);
        Assert.Equal("Compliant", payload?.Status);
    }

    [Fact]
    public async Task Ping_round_trips_to_Pong_over_a_real_named_pipe()
    {
        var pipeName = $"WardenIpcTest-{Guid.NewGuid():N}";

        await using var server = new NamedPipeServerStream(
            pipeName, PipeDirection.InOut, maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        await using var client = new NamedPipeClientStream(
            ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

        var acceptTask = server.WaitForConnectionAsync();
        await client.ConnectAsync((int)TimeSpan.FromSeconds(5).TotalMilliseconds);
        await acceptTask;

        var serverTask = Task.Run(async () =>
        {
            var received = await PipeMessageProtocol.ReadAsync(server);
            Assert.Equal(PipeMessage.Ping.Type, received?.Type);
            await PipeMessageProtocol.WriteAsync(server, PipeMessage.Pong);
        });

        await PipeMessageProtocol.WriteAsync(client, PipeMessage.Ping);
        var response = await PipeMessageProtocol.ReadAsync(client);

        await serverTask;

        Assert.Equal(PipeMessage.Pong.Type, response?.Type);
    }
}
