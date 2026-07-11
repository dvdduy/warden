using Warden.Core;

namespace Warden.Agent;

public interface ICommandExecutor
{
    void Execute(Command command);
}
