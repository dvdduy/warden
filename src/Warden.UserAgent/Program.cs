using System.Diagnostics;
using System.IO.Pipes;
using System.Security;
using Warden.Ipc;

var sessionId = Process.GetCurrentProcess().SessionId;
var pipeName = PipeNames.ForSession(sessionId);

using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

Console.WriteLine($"Connecting to \\\\.\\pipe\\{pipeName} (session {sessionId}) ...");
await client.ConnectAsync((int)TimeSpan.FromSeconds(5).TotalMilliseconds);
Console.WriteLine("Connected. Sending Ping.");

await PipeMessageProtocol.WriteAsync(client, PipeMessage.Ping);
var response = await PipeMessageProtocol.ReadAsync(client);

if (response is null)
{
    Console.WriteLine("Server closed the connection without replying.");
    return;
}

Console.WriteLine($"Received: {response.Type}");
Console.WriteLine("Waiting for service notifications.");

while (true)
{
    var message = await PipeMessageProtocol.ReadAsync(client);
    if (message is null)
    {
        Console.WriteLine("Server closed the connection.");
        return;
    }

    var complianceChanged = message.TryGetComplianceChangedPayload();
    if (complianceChanged is not null)
    {
        ShowComplianceToast(complianceChanged);
        continue;
    }

    Console.WriteLine($"Ignored unsupported message: {message.Type}");
}

static void ShowComplianceToast(ComplianceChangedPayload payload)
{
    var title = "Warden compliance updated";
    var body = $"{payload.Rule}: {payload.Status}";

    try
    {
        var script = $"""
            [Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime] > $null
            [Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom.XmlDocument, ContentType = WindowsRuntime] > $null
            $template = @'
            <toast><visual><binding template="ToastGeneric"><text>{SecurityElement.Escape(title)}</text><text>{SecurityElement.Escape(body)}</text></binding></visual></toast>
            '@
            $xml = New-Object Windows.Data.Xml.Dom.XmlDocument
            $xml.LoadXml($template)
            $toast = [Windows.UI.Notifications.ToastNotification]::new($xml)
            [Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier('Warden').Show($toast)
            """;

        Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            ArgumentList =
            {
                "-NoProfile",
                "-ExecutionPolicy",
                "Bypass",
                "-Command",
                script
            },
            CreateNoWindow = true,
            UseShellExecute = false
        });
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Toast failed: {ex.Message}");
    }

    Console.WriteLine($"{title}: {body}");
}
