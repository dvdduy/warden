using Warden.Core;

namespace Warden.Agent;

public interface IActualStateProvider
{
    ActualState GetActualState();
}
