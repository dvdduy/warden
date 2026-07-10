using Microsoft.Extensions.Logging;
using Warden.Core;
using Warden.ControlPlane;
using Warden.Demo;

using var loggerFactory = LoggerFactory.Create(builder => builder
    .AddSimpleConsole(o =>
    {
        o.SingleLine = true;
        o.TimestampFormat = "HH:mm:ss.fff ";
    })
    .SetMinimumLevel(LogLevel.Information));

Header("WARDEN — self-healing endpoint management (v0.1-core demo)");
Console.WriteLine("Four hard behaviors, driven live: idempotent apply, offline reconcile,");
Console.WriteLine("duplicate delivery/acks, and no-ack -> bounded retry -> Failed.");

DuplicateDeliveryAppliesOnce();
NoAckRedeliversThenFails();
OfflineDeviceReconcilesOnReturn();
FleetConvergence(deviceCount: 40);

Header("Done.");

// ---------------------------------------------------------------------------
// Scenario 1 — duplicate delivery applies exactly once
// ---------------------------------------------------------------------------
void DuplicateDeliveryAppliesOnce()
{
    Section("Scenario 1 — duplicate delivery applies exactly once");

    var deviceId = new DeviceId("laptop-01");
    var client = new AlwaysAcceptingClient(deviceId);
    var agent = new Agent(deviceId, "LAPTOP-01", client);

    var command = new Command(
        CommandId.NewId(), deviceId, "set:BitLocker=on",
        CommandStatus.Pending, 0, DateTimeOffset.UtcNow, null, null);

    Info($"Control plane delivers {command.Id} ({command.Action}) to {deviceId}...");
    agent.Apply(command);
    Info($"  -> actual state: BitLocker={agent.Actual.Settings["BitLocker"]}");

    Info("Network duplicates the exact same delivery, twice more...");
    agent.Apply(command);
    agent.Apply(command);

    Success($"Delivered {client.MarkDeliveredCallCount} times, acked {client.AckCallCount} times, " +
        "mutated the device's state exactly once.");
}

// ---------------------------------------------------------------------------
// Scenario 2 — no ack -> bounded retry -> Failed -> fresh command
// ---------------------------------------------------------------------------
void NoAckRedeliversThenFails()
{
    Section("Scenario 2 — no ack -> bounded retry -> Failed, then a fresh command");

    var clock = new DemoClock();
    var deviceRepo = new InMemoryDeviceRepository();
    var commandStore = new InMemoryCommandStore();
    const int maxAttempts = 2;
    var controlPlane = new ControlPlane(
        deviceRepo, commandStore, clock, maxAttempts,
        loggerFactory.CreateLogger<ControlPlane>(), loggerFactory);

    var deviceId = new DeviceId("laptop-02");
    controlPlane.RegisterDevice(deviceId, "LAPTOP-02");
    controlPlane.SetDesiredState(deviceId, new DesiredState(
        new Dictionary<string, string> { ["Firewall"] = "on" }));

    var issued = controlPlane.ReportStateAndGetNewCommands(deviceId, ActualState.Empty).Single();
    Info($"Issued {issued.Id} ({issued.Action}); device never acks it (dropped ack / crash mid-apply)...");

    for (var attempt = 1; attempt <= maxAttempts; attempt++)
    {
        controlPlane.MarkDelivered(issued.Id, TimeSpan.FromSeconds(30));
        clock.Advance(TimeSpan.FromSeconds(31));
        var swept = controlPlane.SweepAckTimeouts().Single();
        Info($"  attempt {attempt}/{maxAttempts} timed out -> status now {swept.Status}");
    }

    Warn($"{issued.Id} is terminal (Failed) — it will never be resurrected.");

    var client = new InProcessControlPlaneClient(controlPlane);
    var agent = new Agent(deviceId, "LAPTOP-02", client);
    var fresh = agent.RunCycle().Single();

    Success($"Next cycle issued a brand-new command {fresh.Id} for the same still-open gap. " +
        $"Actual state: Firewall={agent.Actual.Settings["Firewall"]}");
}

// ---------------------------------------------------------------------------
// Scenario 3 — offline -> reconnect converges to *current* desired state
// ---------------------------------------------------------------------------
void OfflineDeviceReconcilesOnReturn()
{
    Section("Scenario 3 — offline -> reconnect converges to current desired state");

    var clock = new DemoClock();
    var deviceRepo = new InMemoryDeviceRepository();
    var commandStore = new InMemoryCommandStore();
    var controlPlane = new ControlPlane(
        deviceRepo, commandStore, clock,
        logger: loggerFactory.CreateLogger<ControlPlane>(), loggerFactory: loggerFactory);
    var client = new InProcessControlPlaneClient(controlPlane);

    var deviceId = new DeviceId("laptop-03");
    var agent = new Agent(deviceId, "LAPTOP-03", client);
    agent.Register();

    controlPlane.SetDesiredState(deviceId, new DesiredState(
        new Dictionary<string, string> { ["Encryption"] = "on" }));
    Info("Device goes offline before its first cycle ever runs...");

    Info("...policy changes while it's gone: Encryption 'on' -> 'off'...");
    controlPlane.SetDesiredState(deviceId, new DesiredState(
        new Dictionary<string, string> { ["Encryption"] = "off" }));

    Info("Device reconnects and runs its first cycle ever.");
    var command = agent.RunCycle().Single();

    Success($"Reconciled to the CURRENT desired state ({command.Action}), not the stale value from " +
        $"before it went offline. Actual: Encryption={agent.Actual.Settings["Encryption"]}");
}

// ---------------------------------------------------------------------------
// Scenario 4 — a live fleet converging, no human intervention
// ---------------------------------------------------------------------------
void FleetConvergence(int deviceCount)
{
    Section($"Scenario 4 — {deviceCount} agents converging live against one control plane");

    var clock = new SystemClock();
    var deviceRepo = new InMemoryDeviceRepository();
    var commandStore = new InMemoryCommandStore();
    var controlPlane = new ControlPlane(deviceRepo, commandStore, clock, maxDeliveryAttempts: 3);
    var client = new InProcessControlPlaneClient(controlPlane, ackDeadline: TimeSpan.FromMilliseconds(500));

    var desired = new DesiredState(new Dictionary<string, string>
    {
        ["BitLocker"] = "on",
        ["Firewall"] = "on",
    });

    var deviceIds = Enumerable.Range(0, deviceCount).Select(i => new DeviceId($"fleet-{i:D3}")).ToList();
    foreach (var id in deviceIds)
    {
        controlPlane.SetDesiredState(id, desired);
    }

    var agents = deviceIds.ToDictionary(id => id, id => new Agent(id, id.Value, client));
    foreach (var agent in agents.Values)
    {
        agent.Register();
    }

    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
    var random = new Random();
    using var cts = new CancellationTokenSource();

    // Fire-and-forget: this is a demo, so the sweeper just runs until cts is cancelled
    // below rather than being awaited or observed for exceptions.
    _ = Task.Run(async () =>
    {
        while (!cts.IsCancellationRequested)
        {
            controlPlane.SweepAckTimeouts();
            try { await Task.Delay(100, cts.Token); } catch (TaskCanceledException) { }
        }
    });

    var agentTasks = agents.Values.Select(agent => Task.Run(async () =>
    {
        while (!IsCompliant(agent, desired))
        {
            agent.RunCycle();
            await Task.Delay(random.Next(20, 80));
        }
    })).ToArray();

    var allDone = Task.WhenAll(agentTasks);
    // A redrawing live board only makes sense on a real console; piped/redirected
    // output (CI, a captured demo run) just gets the final snapshot instead of a
    // wall of intermediate frames.
    if (!Console.IsOutputRedirected)
    {
        while (!allDone.IsCompleted && stopwatch.Elapsed < TimeSpan.FromSeconds(15))
        {
            RenderBoard(agents.Values, desired, stopwatch.Elapsed);
            Thread.Sleep(200);
        }
    }

    Task.WaitAll(agentTasks);
    cts.Cancel();
    RenderBoard(agents.Values, desired, stopwatch.Elapsed);

    var health = controlPlane.GetHealthSnapshot();
    Success($"All {deviceCount} devices compliant in {stopwatch.Elapsed.TotalSeconds:F1}s. " +
        $"Fleet health: {health.AckedCommands} acked, {health.InFlightCommands} in flight, " +
        $"{health.FailedCommands} failed.");
}

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------
static bool IsCompliant(Agent agent, DesiredState desired) =>
    desired.Settings.All(kv =>
        agent.Actual.Settings.TryGetValue(kv.Key, out var value) && value == kv.Value);

static void RenderBoard(IEnumerable<Agent> agents, DesiredState desired, TimeSpan elapsed)
{
    // Console.Clear() throws when output isn't a real console (piped/redirected, e.g.
    // CI or a captured demo run) -- fall back to plain scrolling output there instead
    // of crashing the live board.
    if (!Console.IsOutputRedirected)
    {
        Console.Clear();
        Header("WARDEN — self-healing endpoint management (v0.1-core demo)");
    }

    Section($"Scenario 4 — fleet convergence (t={elapsed.TotalSeconds:F1}s)");

    var agentList = agents.ToList();
    var compliantCount = agentList.Count(a => IsCompliant(a, desired));

    Console.WriteLine($"{compliantCount}/{agentList.Count} compliant");
    Console.WriteLine();

    const int columns = 20;
    for (var i = 0; i < agentList.Count; i++)
    {
        Console.ForegroundColor = IsCompliant(agentList[i], desired) ? ConsoleColor.Green : ConsoleColor.Red;
        Console.Write('#');
        Console.ResetColor();

        if ((i + 1) % columns == 0)
        {
            Console.WriteLine();
        }
    }

    Console.WriteLine();
}

static void Header(string text)
{
    Console.WriteLine();
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine(new string('=', text.Length));
    Console.WriteLine(text);
    Console.WriteLine(new string('=', text.Length));
    Console.ResetColor();
}

static void Section(string text)
{
    Console.WriteLine();
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine($"-- {text} --");
    Console.ResetColor();
}

static void Info(string text) => Console.WriteLine($"  {text}");

static void Warn(string text)
{
    Console.ForegroundColor = ConsoleColor.DarkYellow;
    Console.WriteLine($"  ! {text}");
    Console.ResetColor();
}

static void Success(string text)
{
    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine($"  OK {text}");
    Console.ResetColor();
}
