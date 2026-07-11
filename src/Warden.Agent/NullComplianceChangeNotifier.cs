namespace Warden.Agent;

public sealed class NullComplianceChangeNotifier : IComplianceChangeNotifier
{
    public static readonly NullComplianceChangeNotifier Instance = new();

    private NullComplianceChangeNotifier()
    {
    }

    public Task NotifyComplianceChangedAsync(string rule, string status, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
