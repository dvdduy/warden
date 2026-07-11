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
    public void Worker_executes_remediation_command_through_existing_delivery_and_ack_lifecycle()
    {
        var client = new RecordingClient();
        var executor = new RecordingCommandExecutor();
        var worker = new ReportingAgentWorker(
            client,
            new StaticActualStateProvider(new ActualState(new Dictionary<string, string>
            {
                [BitLockerPolicy.EnabledKey] = "false"
            })),
            executor,
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
        Assert.Equal(1, client.MarkDeliveredCallCount);
        Assert.Equal(1, client.AckCallCount);
        Assert.Equal(client.IssuedCommand!.Id, executor.ExecutedCommandId);
    }

    [Fact]
    public void Worker_leaves_failed_remediation_unacked_for_control_plane_retry()
    {
        var client = new RecordingClient();
        var worker = new ReportingAgentWorker(
            client,
            new StaticActualStateProvider(new ActualState(new Dictionary<string, string>
            {
                [BitLockerPolicy.EnabledKey] = "false"
            })),
            new FailingCommandExecutor(),
            Options.Create(new AgentServiceOptions
            {
                DeviceId = "dev-bitlocker",
                Hostname = "LAPTOP-BITLOCKER"
            }),
            NullLogger<ReportingAgentWorker>.Instance);

        worker.RunOnce();

        Assert.Equal(1, client.MarkDeliveredCallCount);
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
        public Command? IssuedCommand { get; private set; }
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
            IssuedCommand = new Command(
                CommandId.NewId(),
                id,
                "set:bitlocker.enabled=true",
                CommandStatus.Pending,
                Attempts: 0,
                IssuedAt: DateTimeOffset.UtcNow,
                AckDeadline: null,
                AckedAt: null);

            return new[]
            {
                IssuedCommand
            };
        }

        public Command MarkDelivered(CommandId commandId)
        {
            MarkDeliveredCallCount++;
            return IssuedCommand! with
            {
                Status = CommandStatus.Delivered,
                Attempts = 1,
                AckDeadline = DateTimeOffset.UtcNow.AddSeconds(30)
            };
        }

        public Command Ack(CommandId commandId)
        {
            AckCallCount++;
            return IssuedCommand! with
            {
                Status = CommandStatus.Acked,
                AckedAt = DateTimeOffset.UtcNow
            };
        }
    }

    private sealed class RecordingCommandExecutor : ICommandExecutor
    {
        public CommandId? ExecutedCommandId { get; private set; }

        public void Execute(Command command) => ExecutedCommandId = command.Id;
    }

    private sealed class FailingCommandExecutor : ICommandExecutor
    {
        public void Execute(Command command) => throw new InvalidOperationException("simulated remediation failure");
    }
}
