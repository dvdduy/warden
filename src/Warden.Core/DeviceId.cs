namespace Warden.Core;

public sealed record DeviceId(string Value)
{
    public override string ToString() => Value;
}