using V12UltraController.Core.Hardware;

namespace V12UltraController.Service;

internal enum CommissioningStage
{
    Probed,
    IdempotentVerified,
    TestVerified,
}

internal sealed class CommissioningSession(
    Guid id,
    string ownerSid,
    DateTimeOffset expiresAtUtc,
    DeviceIdentity probedIdentity)
{
    public Guid Id { get; } = id;
    public string OwnerSid { get; } = ownerSid;
    public DateTimeOffset ExpiresAtUtc { get; } = expiresAtUtc;
    public DeviceIdentity ProbedIdentity { get; } = probedIdentity;
    public IV12Device? Device { get; private set; }
    public V12DeviceState? OriginalState { get; private set; }
    public CommissioningStage Stage { get; set; } = CommissioningStage.Probed;

    public void TakeDevice(IV12Device device)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (Device is not null || OriginalState is not null)
        {
            throw new InvalidOperationException("The commissioning session already owns a device.");
        }

        Device = device;
    }

    public void RecordOriginalState(V12DeviceState originalState)
    {
        ArgumentNullException.ThrowIfNull(originalState);
        if (Device is null)
        {
            throw new InvalidOperationException(
                "The commissioning session must own the device before recording its state.");
        }

        if (OriginalState is not null)
        {
            throw new InvalidOperationException("The commissioning session already recorded device state.");
        }

        if (Device.Identity != ProbedIdentity)
        {
            throw new InvalidOperationException("The opened device identity differs from the probed identity.");
        }

        OriginalState = originalState;
    }

    public (IV12Device Device, V12DeviceState OriginalState) RequireAttachedDevice()
    {
        if (Device is null || OriginalState is null)
        {
            throw new InvalidOperationException(
                "The commissioning session has not completed its first verified device read.");
        }

        return (Device, OriginalState);
    }
}
