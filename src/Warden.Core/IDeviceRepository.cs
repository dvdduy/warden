namespace Warden.Core;

/// <summary>
/// The authority on device registration, last-reported actual state, and each device's
/// assigned desired state. Kept separate from ICommandStore (Session 3) because devices
/// and commands have different lifecycles and different concurrency shapes — a device
/// is upserted roughly once per cycle; commands transition many times.
///
/// Desired state is stored per-device here (rather than as a single global policy)
/// so v0.1-core can support "device A wants featureX=on, device B doesn't" without
/// any change later when policy assignment gets more sophisticated (smart groups, etc).
/// </summary>
public interface IDeviceRepository
{
    /// <summary>
    /// Registers a device if it doesn't exist yet, or updates LastSeen if it does.
    /// Idempotent — calling Register repeatedly for the same DeviceId is always safe
    /// and never resets DesiredState.
    /// </summary>
    Device Register(DeviceId id, string hostname, IClock clock);

    /// <summary>The device's desired state, or DesiredState.Empty if never assigned.</summary>
    DesiredState GetDesiredState(DeviceId id);

    /// <summary>
    /// Sets the desired state for a device. Overwrites any previous assignment —
    /// callers (e.g. an admin action, or a test) own the decision of what "desired"
    /// means; this is just storage.
    /// </summary>
    void SetDesiredState(DeviceId id, DesiredState desired);

    /// <summary>Records the device's self-reported actual state and bumps LastSeen.</summary>
    Device ReportActualState(DeviceId id, ActualState actual, IClock clock);

    /// <summary>The device's last-known record, or null if it has never registered.</summary>
    Device? Get(DeviceId id);

    /// <summary>
    /// Every registered device. Used for fleet-wide reads (e.g. a compliance dashboard)
    /// that need to list devices rather than look one up — not on any hot path, so a full
    /// scan is an acceptable cost at v0.1-core/v0.2-mvp's scale. Mirrors
    /// ICommandStore.GetAll().
    /// </summary>
    IReadOnlyList<Device> GetAll();
}