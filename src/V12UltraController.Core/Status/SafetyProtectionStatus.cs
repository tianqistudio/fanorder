namespace V12UltraController.Core.Status;

public sealed record SafetyProtectionStatus(bool Disabled, bool Persisted, bool AutomaticOutputStopped,
    bool HasInFlightOperation, string? PersistenceError, DateTimeOffset? DisabledAt);
