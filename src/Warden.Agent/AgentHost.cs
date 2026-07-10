using Warden.Core;

namespace Warden.Agent;

/// <summary>
/// Thin host for a simulated device. All the actual logic lives in Warden.Core.Agent —
/// this class just owns the console loop, matching how a real Windows Service would own
/// the timer/scheduling around the same Agent.RunCycle() call.
/// </summary>
public static class AgentHost
{
    public static void RunLoop(Core.Agent agent, int cycles, TimeSpan delayBetweenCycles)
    {
        agent.Register();

        for (var i = 0; i < cycles; i++)
        {
            var received = agent.RunCycle();

            if (received.Count > 0)
            {
                Console.WriteLine($"[cycle {i}] received {received.Count} command(s), actual state now: " +
                    string.Join(", ", agent.Actual.Settings.Select(kv => $"{kv.Key}={kv.Value}")));
            }

            if (i < cycles - 1)
            {
                Thread.Sleep(delayBetweenCycles);
            }
        }
    }
}