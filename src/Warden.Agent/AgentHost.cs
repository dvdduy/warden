using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Warden.Core;

namespace Warden.Agent;

/// <summary>
/// Thin host for a simulated device. All the actual logic lives in Warden.Core.Agent —
/// this class just owns the console loop, matching how a real Windows Service would own
/// the timer/scheduling around the same Agent.RunCycle() call.
/// </summary>
public static class AgentHost
{
    public static void RunLoop(Core.Agent agent, int cycles, TimeSpan delayBetweenCycles, ILogger? logger = null)
    {
        logger ??= NullLogger.Instance;
        agent.Register();

        for (var i = 0; i < cycles; i++)
        {
            var received = agent.RunCycle();

            if (received.Count > 0)
            {
                // CommandId is the correlation id -- logging it here lets an operator
                // trace the same value back to the "issued"/"acked" log lines the
                // control plane emitted for these exact commands.
                logger.LogInformation(
                    "[cycle {Cycle}] received {Count} command(s) {CommandIds}; actual state now: {ActualState}",
                    i, received.Count,
                    string.Join(",", received.Select(c => c.Id)),
                    string.Join(", ", agent.Actual.Settings.Select(kv => $"{kv.Key}={kv.Value}")));
            }

            if (i < cycles - 1)
            {
                Thread.Sleep(delayBetweenCycles);
            }
        }
    }
}