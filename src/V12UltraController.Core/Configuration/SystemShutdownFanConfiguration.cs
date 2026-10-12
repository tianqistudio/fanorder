using V12UltraController.Core.Hardware;

namespace V12UltraController.Core.Configuration;

public enum SystemShutdownFanBehavior
{
    KeepAt100,
    TurnOffOnSystemShutdown,
}

/// <summary>
/// Records the exact, attended protocol checks that must have succeeded before the
/// narrow internal system-shutdown terminal path may request fan power-off. This
/// type deliberately has no factory: the default product does not mint a
/// qualification, and merely selecting a shutdown behavior cannot create one.
/// </summary>
public sealed record SystemShutdownOffQualification
{
    public const int CurrentProtocolRevision = 1;

    public int ProtocolRevision { get; init; }

    public ushort VendorId { get; init; }

    public ushort ProductId { get; init; }

    public ushort FirmwareVersion { get; init; }

    public ushort UsagePage { get; init; }

    public ushort Usage { get; init; }

    public ushort FeatureReportLength { get; init; }

    public bool ShutdownOffReadbackConfirmed { get; init; }

    public bool StartupOnAt100ReadbackConfirmed { get; init; }

    public bool LightingStatePreserved { get; init; }

    public DateTimeOffset QualifiedAtUtc { get; init; }

    public void Validate()
    {
        if (ProtocolRevision != CurrentProtocolRevision)
        {
            throw new ConfigurationValidationException(
                $"Unsupported shutdown qualification protocol revision {ProtocolRevision}; expected {CurrentProtocolRevision}.");
        }

        var identity = new DeviceIdentity(
            VendorId,
            ProductId,
            FirmwareVersion,
            UsagePage,
            Usage,
            FeatureReportLength);
        if (!DeviceAllowList.IsSupported(identity))
        {
            throw new ConfigurationValidationException(
                "The system-shutdown fan qualification does not match the exact supported V12 Ultra identity.");
        }

        if (!ShutdownOffReadbackConfirmed ||
            !StartupOnAt100ReadbackConfirmed ||
            !LightingStatePreserved)
        {
            throw new ConfigurationValidationException(
                "The system-shutdown fan qualification requires attended OFF readback, ON-at-100 readback, and lighting preservation checks.");
        }

        if (QualifiedAtUtc == default || QualifiedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ConfigurationValidationException(
                "The system-shutdown fan qualification must record when its attended checks completed using UTC.");
        }
    }
}
