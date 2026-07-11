namespace Warden.Agent;

public interface ISystemCommandRunner
{
    CommandResult Run(string fileName, IReadOnlyList<string> arguments);
}
