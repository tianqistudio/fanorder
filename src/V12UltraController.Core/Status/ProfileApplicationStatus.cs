namespace V12UltraController.Core.Status;

public enum ProfileApplyStage { SavingConfiguration, WaitingForFan, ApplyingLighting, Finished }
public enum ProfileApplyOutcome { Applying, Completed, Partial, Rejected, Superseded, Interrupted }
public enum ProfileConfigurationState { Pending, Saved, Unchanged, Unknown }
public enum ProfileFanState { Pending, Confirmed, SafetyOverride, Unknown }
public enum ProfileLightingState { NotRequested, Pending, Confirmed, NotAttempted, Unknown }

/// <summary>Separate configuration and hardware outcomes; this is not a combined atomic transaction.</summary>
public sealed record ProfileApplicationStatus(
    string OperationId,
    string ProfileId,
    string ProfileName,
    ProfileApplyStage Stage,
    ProfileApplyOutcome Outcome,
    ProfileConfigurationState Configuration,
    ProfileFanState Fan,
    ProfileLightingState Lighting,
    string? Reason,
    DateTimeOffset StartedAt,
    DateTimeOffset UpdatedAt);
