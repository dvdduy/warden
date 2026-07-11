using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Warden.Agent;
using Warden.Core;
using Xunit;

namespace Warden.Rest.Tests;

public class BitLockerAgentTests
{
    [Fact]
    public void Parser_reads_protection_on_from_manage_bde_output()
    {
        var output = """
            BitLocker Drive Encryption: Configuration Tool version 10.0.22621

            Volume C: [Windows]
                Conversion Status:    Fully Encrypted
                Protection Status:    Protection On
            """;

        Assert.True(BitLockerStatusParser.IsProtectionEnabled(output));
    }

    [Fact]
    public void Parser_reads_protection_off_from_manage_bde_output()
    {
        var output = """
            Volume C: [Windows]
                Conversion Status:    Fully Decrypted
                Protection Status:    Protection Off
            """;

        Assert.False(BitLockerStatusParser.IsProtectionEnabled(output));
    }

    [Fact]
    public void Read_only_worker_reports_actual_state_but_does_not_mark_commands_delivered_or_acked()
    {
        var client = new RecordingClient();
        var worker = new ReportingAgentWorker(
            client,
            new StaticActualStateProvider(new ActualState(new Dictionary<string, string>
            {
                [BitLockerPolicy.EnabledKey] = "false"
            })),
            Options.Create(new AgentServiceOptions
            {
                DeviceId = "dev-bitlocker",
                Hostname = "LAPTOP-BITLOCKER"
            }),
            NullLogger<ReportingAgentWorker>.Instance);

        worker.RunOnce();

        Assert.Equal(new DeviceId("dev-bitlocker"), client.RegisteredDeviceId);
        Assert.Equal("LAPTOP-BITLOCKER", client.RegisteredHostname);
        Assert.Equal("false", client.LastReportedActual!.Settings[BitLockerPolicy.EnabledKey]);
        Assert.Equal(0, client.MarkDeliveredCallCount);
        Assert.Equal(0, client.AckCallCount);
    }

    private sealed class StaticActualStateProvider : IActualStateProvider
    {
        private readonly ActualState _actual;

        public StaticActualStateProvider(ActualState actual) => _actual = actual;

        public ActualState GetActualState() => _actual;
    }

    private sealed class RecordingClient : IControlPlaneClient
    {
        public DeviceId? RegisteredDeviceId { get; private set; }
        public string? RegisteredHostname { get; private set; }
        public ActualState? LastReportedActual { get; private set; }
        public int MarkDeliveredCallCount { get; private set; }
        public int AckCallCount { get; private set; }

        public Device Register(DeviceId id, string hostname)
        {
            RegisteredDeviceId = id;
            RegisteredHostname = hostname;
            return new Device(id, hostname, ActualState.Empty, DateTimeOffset.UtcNow);
        }

        public IReadOnlyList<Command> ReportState(DeviceId id, ActualState actual)
        {
            LastReportedActual = actual;
            return new[]
            {
                new Command(
                    CommandId.NewId(),
                    id,
                    "set:bitlocker.enabled=true",
                    CommandStatus.Pending,
                    Attempts: 0,
                    IssuedAt: DateTimeOffset.UtcNow,
                    AckDeadline: null,
                    AckedAt: null)
            };
        }

        public Command MarkDelivered(CommandId commandId)
        {
            MarkDeliveredCallCount++;
            throw new NotSupportedException();
        }

        public Command Ack(CommandId commandId)
        {
            AckCallCount++;
            throw new NotSupportedException();
        }
    }
}
