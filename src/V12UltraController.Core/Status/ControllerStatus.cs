using V12UltraController.Core.Control;

namespace V12UltraController.Core.Status;

public sealed record ControllerStatus(
    string ServiceState,
    bool Commissioned,
    string Owner,
    DeviceStatus Device,
    FanStatus Fan,
    ThermalStatus Thermal,
    FailSafeStatus FailSafe,
    DateTimeOffset? LastSuccessfulRead,
    DateTimeOffset? LastSuccessfulWrite,
    HardwareSafetyStatus HardwareSafety,
    CommissioningStatus? Commissioning = null,
    ShutdownStatus? Shutdown = null,
    AuditMutationStatus? AuditMutation = null,
    ForcedUninstallRecoveryStatus? ForcedUninstallRecovery = null,
    SafetyOperationAuditStatus? SafetyOperationAudit = null,
    SoftwareStatus? Software = null,
    ProfileApplicationStatus? ProfileApplication = null,
    SafetyProtectionStatus? SafetyProtection = null);

public sealed record SoftwareStatus(string ServiceVersion, int UiContractVersion, bool LightingSupported,
    bool ProfileApplySupported = false,
    bool SafetyDisableSupported = false,
    bool ManualActionsWhileProtectionDisabledSupported = false,
    bool FanLightingLinkSupported = false);

public static class UiCompatibility
{
    public const int ContractVersion = 3;
}

public sealed record DeviceStatus(
    bool Connected,
    string VendorId,
    string ProductId,
    string Firmware,
    bool ProtocolCompatible);

public sealed record FanStatus(
    ControlMode Mode,
    byte RequestedPercent,
    byte? ReportedControlPercent,
    bool? PoweredOn,
    byte? ConfiguredManualPercent = null,
    bool ManualPowerSupported = false,
    bool OffRequested = false);

public sealed record ThermalStatus(
    double? CpuCelsius,
    IReadOnlyList<double> GpuCelsius,
    double? ControlCelsius,
    bool Healthy,
    DateTimeOffset? LastUpdated);

public sealed record FailSafeStatus(
    bool Active,
    FailSafeReason Reason,
    DateTimeOffset? Since);

public sealed record HardwareSafetyStatus(
    bool Poisoned,
    string? Reason,
    DateTimeOffset? Since);

public sealed record AuditMutationStatus(
    bool Blocked,
    bool Pending,
    string? TransactionId,
    string? EventName,
    string? Stage,
    string? Reason);

public sealed record SafetyOperationAuditStatus(
    bool Blocked,
    bool Pending,
    string? TransactionId,
    string? EventName,
    string? Kind,
    string? Stage,
    string? Outcome,
    bool RequiresOn100Recovery,
    string? Reason);

public sealed record ForcedUninstallRecoveryStatus(
    bool Required,
    bool MarkerValid,
    string? MarkerId,
    string? Reason,
    DateTimeOffset? Since);

public sealed record CommissioningStatus(
    bool Active,
    string? SessionId,
    string? Stage,
    DateTimeOffset? ExpiresAt);

public sealed record ShutdownStatus(
    bool Enabled,
    bool Qualified,
    string Behavior,
    ShutdownQualificationIdentityStatus? QualifiedIdentity,
    DateTimeOffset? QualifiedAt,
    bool MatchesCurrentHandle,
    bool Pending = false,
    string? PendingQualificationId = null,
    DateTimeOffset? PendingExpiresAt = null);

public sealed record ShutdownQualificationIdentityStatus(
    string VendorId,
    string ProductId,
    string Firmware,
    string UsagePage,
    string Usage,
    ushort FeatureReportLength,
    int ProtocolRevision);
