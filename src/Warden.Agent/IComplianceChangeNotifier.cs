namespace Warden.Agent;

public interface IComplianceChangeNotifier
{
    Task NotifyComplianceChangedAsync(string rule, string status, CancellationToken cancellationToken = default);
}
