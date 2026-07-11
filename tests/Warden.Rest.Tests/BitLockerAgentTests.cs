using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Warden.Agent;
using Warden.ControlPlane;
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
        var notifier = new RecordingComplianceChangeNotifier();
        var worker = new ReportingAgentWorker(
            client,
            new StaticActualStateProvider(new ActualState(new Dictionary<string, string>
            {
                [BitLockerPolicy.EnabledKey] = "false"
            })),
            executor,
            notifier,
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
        Assert.Equal([("bitlocker.enabled", "Compliant")], notifier.Notifications);
    }

    [Fact]
    public void Worker_leaves_failed_remediation_unacked_for_control_plane_retry()
    {
        var client = new RecordingClient();
        var notifier = new RecordingComplianceChangeNotifier();
        var worker = new ReportingAgentWorker(
            client,
            new StaticActualStateProvider(new ActualState(new Dictionary<string, string>
            {
                [BitLockerPolicy.EnabledKey] = "false"
            })),
            new FailingCommandExecutor(),
            notifier,
            Options.Create(new AgentServiceOptions
            {
                DeviceId = "dev-bitlocker",
                Hostname = "LAPTOP-BITLOCKER"
            }),
            NullLogger<ReportingAgentWorker>.Instance);

        worker.RunOnce();

        Assert.Equal(1, client.MarkDeliveredCallCount);
        Assert.Equal(0, client.AckCallCount);
        Assert.Empty(notifier.Notifications);
    }

    // ---- BitLockerActualStateProvider / BitLockerCommandExecutor, against a fake
    // ISystemCommandRunner instead of the real manage-bde -- these previously had zero
    // coverage of their own; only the classes that bypass them (fakes, or
    // ReportingAgentWorker with a recording double) were tested. No OperatingSystem
    // check guards these classes, so this runs on any OS the tests happen to execute on.

    [Fact]
    public void ActualStateProvider_reports_true_when_manage_bde_reports_protection_on()
    {
        var runner = new FakeSystemCommandRunner();
        runner.WhenRun("manage-bde", new[] { "-status", "C:" },
            new CommandResult(0, "Protection Status:    Protection On", ""));

        var provider = new BitLockerActualStateProvider(runner, Options.Create(new AgentServiceOptions()));

        var actual = provider.GetActualState();

        Assert.Equal("true", actual.Settings[BitLockerPolicy.EnabledKey]);
    }

    [Fact]
    public void ActualStateProvider_reports_false_when_manage_bde_reports_protection_off()
    {
        var runner = new FakeSystemCommandRunner();
        runner.WhenRun("manage-bde", new[] { "-status", "C:" },
            new CommandResult(0, "Protection Status:    Protection Off", ""));

        var provider = new BitLockerActualStateProvider(runner, Options.Create(new AgentServiceOptions()));

        var actual = provider.GetActualState();

        Assert.Equal("false", actual.Settings[BitLockerPolicy.EnabledKey]);
    }

    [Fact]
    public void ActualStateProvider_uses_the_configured_volume()
    {
        var runner = new FakeSystemCommandRunner();
        runner.WhenRun("manage-bde", new[] { "-status", "D:" },
            new CommandResult(0, "Protection Status:    Protection On", ""));

        var provider = new BitLockerActualStateProvider(
            runner, Options.Create(new AgentServiceOptions { BitLockerVolume = "D:" }));

        Assert.Equal("true", provider.GetActualState().Settings[BitLockerPolicy.EnabledKey]);
    }

    [Fact]
    public void ActualStateProvider_throws_when_manage_bde_exits_nonzero()
    {
        var runner = new FakeSystemCommandRunner();
        runner.WhenRun("manage-bde", new[] { "-status", "C:" },
            new CommandResult(1, "", "access denied"));

        var provider = new BitLockerActualStateProvider(runner, Options.Create(new AgentServiceOptions()));

        var ex = Assert.Throws<InvalidOperationException>(() => provider.GetActualState());
        Assert.Contains("access denied", ex.Message);
    }

    [Theory]
    [InlineData("set:bitlocker.enabled=true")]
    [InlineData("enable-bitlocker")]
    public void CommandExecutor_runs_manage_bde_on_for_either_enable_action(string action)
    {
        var runner = new FakeSystemCommandRunner();
        runner.WhenRun("manage-bde", new[] { "-on", "C:" }, new CommandResult(0, "", ""));
        var executor = new BitLockerCommandExecutor(runner, Options.Create(new AgentServiceOptions()));

        executor.Execute(new Command(
            CommandId.NewId(), new DeviceId("dev-1"), action,
            CommandStatus.Delivered, 1, DateTimeOffset.UtcNow, null, null));

        Assert.Equal(1, runner.CallCount("manage-bde", new[] { "-on", "C:" }));
    }

    [Fact]
    public void CommandExecutor_throws_for_an_unrecognized_action_without_running_anything()
    {
        var runner = new FakeSystemCommandRunner();
        var executor = new BitLockerCommandExecutor(runner, Options.Create(new AgentServiceOptions()));

        var command = new Command(
            CommandId.NewId(), new DeviceId("dev-1"), "set:unknown.setting=true",
            CommandStatus.Delivered, 1, DateTimeOffset.UtcNow, null, null);

        Assert.Throws<InvalidOperationException>(() => executor.Execute(command));
        Assert.Equal(0, runner.TotalCallCount);
    }

    [Fact]
    public void CommandExecutor_throws_when_manage_bde_exits_nonzero()
    {
        var runner = new FakeSystemCommandRunner();
        runner.WhenRun("manage-bde", new[] { "-on", "C:" }, new CommandResult(1, "", "elevation required"));
        var executor = new BitLockerCommandExecutor(runner, Options.Create(new AgentServiceOptions()));

        var command = new Command(
            CommandId.NewId(), new DeviceId("dev-1"), "enable-bitlocker",
            CommandStatus.Delivered, 1, DateTimeOffset.UtcNow, null, null);

        var ex = Assert.Throws<InvalidOperationException>(() => executor.Execute(command));
        Assert.Contains("elevation required", ex.Message);
    }

    [Fact]
    public void Fake_bitlocker_mode_drifts_then_remediates_to_compliant_without_manage_bde()
    {
        var deviceId = new DeviceId("dev-fake-bitlocker");
        var clock = new SystemClock();
        var devices = new InMemoryDeviceRepository();
        var commands = new InMemoryCommandStore();
        var controlPlane = new Warden.ControlPlane.ControlPlane(devices, commands, clock);
        controlPlane.RegisterDevice(deviceId, "LAPTOP-FAKE");
        controlPlane.SetDesiredState(deviceId, new DesiredState(new Dictionary<string, string>
        {
            [BitLockerPolicy.EnabledKey] = "true"
        }));

        var fakeBitLocker = new FakeBitLockerState(Options.Create(new AgentServiceOptions
        {
            DeviceId = deviceId.Value,
            Hostname = "LAPTOP-FAKE",
            UseFakeBitLocker = true,
            FakeBitLockerEnabled = false
        }));
        var worker = new ReportingAgentWorker(
            new InProcessControlPlaneClient(controlPlane),
            fakeBitLocker,
            fakeBitLocker,
            Options.Create(new AgentServiceOptions
            {
                DeviceId = deviceId.Value,
                Hostname = "LAPTOP-FAKE"
            }),
            NullLogger<ReportingAgentWorker>.Instance);

        worker.RunOnce();

        Assert.True(fakeBitLocker.Enabled);
        Assert.Empty(controlPlane.GetInFlightCommands(deviceId));
        Assert.Equal("false", devices.Get(deviceId)!.Actual.Settings[BitLockerPolicy.EnabledKey]);

        worker.RunOnce();

        Assert.Empty(controlPlane.GetInFlightCommands(deviceId));
        Assert.Equal("true", devices.Get(deviceId)!.Actual.Settings[BitLockerPolicy.EnabledKey]);
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

    private sealed class RecordingComplianceChangeNotifier : IComplianceChangeNotifier
    {
        public List<(string Rule, string Status)> Notifications { get; } = [];

        public Task NotifyComplianceChangedAsync(
            string rule,
            string status,
            CancellationToken cancellationToken = default)
        {
            Notifications.Add((rule, status));
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// A fake ISystemCommandRunner: returns canned CommandResults for known
    /// (fileName, arguments) pairs and records every call, instead of ever starting a
    /// real process. This is what makes BitLockerActualStateProvider/
    /// BitLockerCommandExecutor testable without a Windows machine or manage-bde.
    /// </summary>
    private sealed class FakeSystemCommandRunner : ISystemCommandRunner
    {
        private readonly Dictionary<string, CommandResult> _results = new();
        private readonly Dictionary<string, int> _callCounts = new();

        public int TotalCallCount => _callCounts.Values.Sum();

        public void WhenRun(string fileName, IReadOnlyList<string> arguments, CommandResult result) =>
            _results[Key(fileName, arguments)] = result;

        public int CallCount(string fileName, IReadOnlyList<string> arguments) =>
            _callCounts.GetValueOrDefault(Key(fileName, arguments));

        public CommandResult Run(string fileName, IReadOnlyList<string> arguments)
        {
            var key = Key(fileName, arguments);
            _callCounts[key] = _callCounts.GetValueOrDefault(key) + 1;

            if (!_results.TryGetValue(key, out var result))
            {
                throw new InvalidOperationException(
                    $"FakeSystemCommandRunner has no configured result for '{fileName} {string.Join(' ', arguments)}'.");
            }

            return result;
        }

        private static string Key(string fileName, IReadOnlyList<string> arguments) =>
            $"{fileName} {string.Join(' ', arguments)}";
    }
}
