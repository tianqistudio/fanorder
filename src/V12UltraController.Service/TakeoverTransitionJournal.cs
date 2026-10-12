using System.Text.Json;
using System.Text.Json.Serialization;

namespace V12UltraController.Service;

internal enum TakeoverTransitionOperation
{
    Commit,
    Rollback,
}

internal enum TakeoverTransitionStage
{
    Prepared,
    TaskDisabled,
    RuntimeStatePersisted,
    ConfigurationPersisted,
    ControllerStateDecommissioned,
    TaskRestored,
    TaskDisableInProgress,
    TaskRestoreInProgress,
    OutcomeResolved,
    AuditAppended,
}

internal enum TakeoverTransitionAuditResult
{
    Pending,
    Success,
    NotApplied,
    Compensated,
}

internal enum TakeoverTransitionAuditAction
{
    Pending,
    CommitCompleted,
    CommitAbandonedOriginalState,
    CommitFailedRolledBack,
    RollbackCompleted,
    RollbackCompensatedToController,
    ForcedUninstallRollbackCompleted,
}

internal sealed record TakeoverTransitionAudit(
    string EventName,
    string CallerSid,
    string Source,
    TakeoverTransitionAuditResult Result,
    TakeoverTransitionAuditAction Action,
    JsonElement Previous,
    JsonElement Current,
    string? ResolvedByCallerSid,
    string? ResolvedBySource)
{
    private static readonly HashSet<string> AllowedEventNames = new(StringComparer.Ordinal)
    {
        "commission.commit",
        "commission.rollback",
        "commission.rollback.forced-uninstall-recovery",
    };

    public void Validate(TakeoverTransitionStage stage)
    {
        if (!AllowedEventNames.Contains(EventName))
        {
            throw new InvalidDataException("The takeover-transition audit event name is invalid.");
        }

        ValidateLabel(CallerSid, nameof(CallerSid), 256, requireSid: true);
        ValidateLabel(Source, nameof(Source), 256, requireSid: false);
        if (ResolvedByCallerSid is not null)
        {
            ValidateLabel(ResolvedByCallerSid, nameof(ResolvedByCallerSid), 256, requireSid: true);
        }

        if (ResolvedBySource is not null)
        {
            ValidateLabel(ResolvedBySource, nameof(ResolvedBySource), 256, requireSid: false);
        }

        if ((ResolvedByCallerSid is null) != (ResolvedBySource is null))
        {
            throw new InvalidDataException(
                "The takeover-transition audit resolver SID and source must be present together.");
        }

        if (!Enum.IsDefined(Result) || !Enum.IsDefined(Action))
        {
            throw new InvalidDataException("The takeover-transition audit result or action is invalid.");
        }

        var terminal = stage is TakeoverTransitionStage.OutcomeResolved or
            TakeoverTransitionStage.AuditAppended;
        if (terminal &&
            (Result == TakeoverTransitionAuditResult.Pending ||
             Action == TakeoverTransitionAuditAction.Pending))
        {
            throw new InvalidDataException(
                "A resolved takeover transition must contain a terminal audit result and action.");
        }

        if (!terminal &&
            (Result != TakeoverTransitionAuditResult.Pending ||
             Action != TakeoverTransitionAuditAction.Pending ||
             ResolvedByCallerSid is not null ||
             ResolvedBySource is not null ||
             !JsonElement.DeepEquals(Previous, Current)))
        {
            throw new InvalidDataException(
                "An unresolved takeover transition cannot claim a terminal audit result.");
        }

        var resultMatchesAction = Action switch
        {
            TakeoverTransitionAuditAction.Pending => Result == TakeoverTransitionAuditResult.Pending,
            TakeoverTransitionAuditAction.CommitCompleted or
                TakeoverTransitionAuditAction.RollbackCompleted or
                TakeoverTransitionAuditAction.ForcedUninstallRollbackCompleted =>
                Result == TakeoverTransitionAuditResult.Success,
            TakeoverTransitionAuditAction.CommitAbandonedOriginalState =>
                Result == TakeoverTransitionAuditResult.NotApplied,
            TakeoverTransitionAuditAction.CommitFailedRolledBack or
                TakeoverTransitionAuditAction.RollbackCompensatedToController =>
                Result == TakeoverTransitionAuditResult.Compensated,
            _ => false,
        };
        if (!resultMatchesAction)
        {
            throw new InvalidDataException(
                "The takeover-transition audit result does not match its terminal action.");
        }

        var eventMatchesAction = EventName switch
        {
            "commission.commit" => Action is
                TakeoverTransitionAuditAction.Pending or
                TakeoverTransitionAuditAction.CommitCompleted or
                TakeoverTransitionAuditAction.CommitAbandonedOriginalState or
                TakeoverTransitionAuditAction.CommitFailedRolledBack,
            "commission.rollback" => Action is
                TakeoverTransitionAuditAction.Pending or
                TakeoverTransitionAuditAction.RollbackCompleted or
                TakeoverTransitionAuditAction.RollbackCompensatedToController,
            "commission.rollback.forced-uninstall-recovery" => Action is
                TakeoverTransitionAuditAction.Pending or
                TakeoverTransitionAuditAction.ForcedUninstallRollbackCompleted,
            _ => false,
        };
        if (!eventMatchesAction)
        {
            throw new InvalidDataException(
                "The takeover-transition audit event does not match its terminal action.");
        }

        AuditSnapshotPolicy.Validate(Previous, "takeover previous");
        AuditSnapshotPolicy.Validate(Current, "takeover current");
    }

    private static void ValidateLabel(
        string? value,
        string name,
        int maximumLength,
        bool requireSid)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength || value.Any(char.IsControl) ||
            requireSid && !value.StartsWith("S-", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"The takeover-transition audit {name} must be a valid bounded single-line value.");
        }
    }
}

internal sealed record TakeoverTransitionJournal(
    int SchemaVersion,
    Guid TransitionId,
    TakeoverTransitionOperation Operation,
    TakeoverTransitionStage Stage,
    string OwnerSid,
    string TaskBackupSha256,
    bool TaskOriginallyEnabled,
    bool StartMythCoolRequested,
    DateTimeOffset StartedAtUtc,
    TakeoverTransitionAudit Audit)
{
    public const int CurrentSchemaVersion = 3;

    public void Validate()
    {
        if (SchemaVersion != CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Unsupported takeover-transition schema {SchemaVersion}; expected {CurrentSchemaVersion}.");
        }

        if (TransitionId == Guid.Empty ||
            !Enum.IsDefined(Operation) ||
            !Enum.IsDefined(Stage) ||
            string.IsNullOrWhiteSpace(OwnerSid) ||
            OwnerSid.Length > 256 ||
            OwnerSid.Any(char.IsControl) ||
            !OwnerSid.StartsWith("S-", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(TaskBackupSha256) ||
            TaskBackupSha256.Length != 64 ||
            !TaskBackupSha256.All(Uri.IsHexDigit) ||
            StartedAtUtc == default ||
            StartedAtUtc.Offset != TimeSpan.Zero ||
            Audit is null)
        {
            throw new InvalidDataException("The takeover-transition journal is invalid.");
        }

        var stageAllowed = Operation switch
        {
            TakeoverTransitionOperation.Commit =>
                Stage is TakeoverTransitionStage.Prepared
                    or TakeoverTransitionStage.TaskDisableInProgress
                    or TakeoverTransitionStage.TaskDisabled
                    or TakeoverTransitionStage.RuntimeStatePersisted
                    or TakeoverTransitionStage.ConfigurationPersisted
                    or TakeoverTransitionStage.OutcomeResolved
                    or TakeoverTransitionStage.AuditAppended,
            TakeoverTransitionOperation.Rollback =>
                Stage is TakeoverTransitionStage.Prepared
                    or TakeoverTransitionStage.ControllerStateDecommissioned
                    or TakeoverTransitionStage.TaskRestoreInProgress
                    or TakeoverTransitionStage.TaskRestored
                    or TakeoverTransitionStage.OutcomeResolved
                    or TakeoverTransitionStage.AuditAppended,
            _ => false,
        };
        if (!stageAllowed)
        {
            throw new InvalidDataException("The takeover-transition stage does not match its operation.");
        }


        Audit.Validate(Stage);
    }
}

internal sealed class TakeoverTransitionJournalStore(ServicePaths paths)
{
    private const long MaximumDocumentBytes = 16 * 1024;
    private static readonly HashSet<string> ExpectedProperties = new(StringComparer.Ordinal)
    {
        "schemaVersion",
        "transitionId",
        "operation",
        "stage",
        "ownerSid",
        "taskBackupSha256",
        "taskOriginallyEnabled",
        "startMythCoolRequested",
        "startedAtUtc",
        "audit",
    };

    private static readonly HashSet<string> ExpectedAuditProperties = new(StringComparer.Ordinal)
    {
        "eventName",
        "callerSid",
        "source",
        "result",
        "action",
        "previous",
        "current",
        "resolvedByCallerSid",
        "resolvedBySource",
    };

    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        NumberHandling = JsonNumberHandling.Strict,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public async Task<bool> ExistsAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            try
            {
                _ = File.GetAttributes(paths.TransitionJournalPath);
                return true;
            }
            catch (FileNotFoundException)
            {
                return false;
            }
            catch (DirectoryNotFoundException)
            {
                return false;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<TakeoverTransitionJournal?> LoadAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task SaveAsync(
        TakeoverTransitionJournal journal,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(journal);
        journal.Validate();
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SaveAndVerifyCoreAsync(journal, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task PrepareAsync(
        TakeoverTransitionJournal journal,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(journal);
        journal.Validate();
        if (journal.Stage != TakeoverTransitionStage.Prepared)
        {
            throw new InvalidDataException("A takeover transition must begin at the Prepared stage.");
        }

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (await LoadCoreAsync(cancellationToken).ConfigureAwait(false) is not null)
            {
                throw new InvalidDataException(
                    "An unresolved takeover transition already exists; it was not overwritten.");
            }

            await SaveAndVerifyCoreAsync(journal, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<TakeoverTransitionJournal> AdvanceAsync(
        TakeoverTransitionJournal expected,
        TakeoverTransitionJournal updated,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(updated);
        expected.Validate();
        updated.Validate();
        ValidateAdvance(expected, updated);

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await LoadCoreAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("The expected takeover transition is missing.");
            if (!Equivalent(current, expected))
            {
                throw new InvalidDataException(
                    "The takeover transition changed; exact advancement was refused.");
            }

            await SaveAndVerifyCoreAsync(updated, cancellationToken).ConfigureAwait(false);
            return updated;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task DeleteAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            File.Delete(paths.TransitionJournalPath);
            if (await LoadCoreAsync(CancellationToken.None).ConfigureAwait(false) is not null)
            {
                throw new IOException("The takeover-transition journal remained after deletion.");
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task DeleteExactAsync(
        TakeoverTransitionJournal expected,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        expected.Validate();

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await LoadCoreAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("The expected takeover-transition journal is missing.");
            if (!Equivalent(current, expected))
            {
                throw new InvalidDataException(
                    "The takeover-transition journal changed; exact deletion was refused.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Delete(paths.TransitionJournalPath);
            if (await LoadCoreAsync(CancellationToken.None).ConfigureAwait(false) is not null)
            {
                throw new IOException("The takeover-transition journal remained after exact deletion.");
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<TakeoverTransitionJournal?> LoadCoreAsync(CancellationToken cancellationToken)
    {
        FileStream stream;
        try
        {
            stream = new FileStream(
                paths.TransitionJournalPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }

        await using (stream)
        {
            if (stream.Length is <= 0 or > MaximumDocumentBytes)
            {
                throw new InvalidDataException(
                    "The takeover-transition journal has an invalid document length.");
            }

            try
            {
                using var document = await JsonDocument.ParseAsync(
                        stream,
                        new JsonDocumentOptions
                        {
                            AllowTrailingCommas = false,
                            CommentHandling = JsonCommentHandling.Disallow,
                            MaxDepth = 8,
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
                ValidateDocumentShape(document.RootElement);
                var journal = document.RootElement.Deserialize<TakeoverTransitionJournal>(jsonOptions)
                    ?? throw new InvalidDataException("The takeover-transition journal is empty.");
                if (journal.SchemaVersion is 1 or 2)
                {
                    throw new InvalidDataException(
                        $"A schema-{journal.SchemaVersion} takeover journal predates durable audit " +
                        "metadata. Its outcome requires explicit attended recovery and must not be " +
                        "automatically queried, changed, or deleted.");
                }

                if (journal.Audit is null)
                {
                    throw new InvalidDataException(
                        "The takeover-transition journal audit envelope is required.");
                }

                journal = journal with
                {
                    Audit = journal.Audit with
                    {
                        Previous = journal.Audit.Previous.Clone(),
                        Current = journal.Audit.Current.Clone(),
                    },
                };
                journal.Validate();
                return journal;
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException(
                    "The takeover-transition journal is not valid strict JSON.",
                    exception);
            }
        }
    }

    private static void ValidateDocumentShape(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("The takeover-transition journal must be a JSON object.");
        }

        ValidateExactObjectShape(root, ExpectedProperties, "takeover-transition journal");

        var audit = root.GetProperty("audit");
        ValidateExactObjectShape(audit, ExpectedAuditProperties, "takeover-transition audit envelope");
        RequireKind(audit, "eventName", "takeover-transition audit envelope", JsonValueKind.String);
        RequireKind(audit, "callerSid", "takeover-transition audit envelope", JsonValueKind.String);
        RequireKind(audit, "source", "takeover-transition audit envelope", JsonValueKind.String);
        RequireKind(audit, "result", "takeover-transition audit envelope", JsonValueKind.Number);
        RequireKind(audit, "action", "takeover-transition audit envelope", JsonValueKind.Number);
        RequireKind(audit, "previous", "takeover-transition audit envelope", JsonValueKind.Object);
        RequireKind(audit, "current", "takeover-transition audit envelope", JsonValueKind.Object);
        RequireKind(
            audit,
            "resolvedByCallerSid",
            "takeover-transition audit envelope",
            JsonValueKind.String,
            JsonValueKind.Null);
        RequireKind(
            audit,
            "resolvedBySource",
            "takeover-transition audit envelope",
            JsonValueKind.String,
            JsonValueKind.Null);
    }

    private static void ValidateExactObjectShape(
        JsonElement element,
        IReadOnlySet<string> expectedProperties,
        string description)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"The {description} must be a JSON object.");
        }

        var remaining = new HashSet<string>(expectedProperties, StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!expectedProperties.Contains(property.Name))
            {
                throw new InvalidDataException(
                    $"The {description} contains unknown or incorrectly-cased property '{property.Name}'.");
            }

            if (!remaining.Remove(property.Name))
            {
                throw new InvalidDataException(
                    $"The {description} contains duplicate property '{property.Name}'.");
            }
        }

        if (remaining.Count > 0)
        {
            throw new InvalidDataException(
                $"The {description} is missing required properties: {string.Join(", ", remaining.Order())}.");
        }
    }

    private static void RequireKind(
        JsonElement parent,
        string propertyName,
        string description,
        params JsonValueKind[] allowedKinds)
    {
        if (!allowedKinds.Contains(parent.GetProperty(propertyName).ValueKind))
        {
            throw new InvalidDataException(
                $"The {description} property '{propertyName}' has an invalid JSON type.");
        }
    }

    private async Task SaveAndVerifyCoreAsync(
        TakeoverTransitionJournal journal,
        CancellationToken cancellationToken)
    {
        await AtomicFile.WriteJsonAsync(paths.TransitionJournalPath, journal, cancellationToken)
            .ConfigureAwait(false);
        var readBack = await LoadCoreAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new IOException("The takeover-transition journal was missing after persistence.");
        if (!Equivalent(readBack, journal))
        {
            throw new InvalidDataException(
                "The takeover-transition journal did not match its durable readback.");
        }
    }

    private static void ValidateAdvance(
        TakeoverTransitionJournal expected,
        TakeoverTransitionJournal updated)
    {
        if (expected.SchemaVersion != updated.SchemaVersion ||
            expected.TransitionId != updated.TransitionId ||
            !string.Equals(expected.OwnerSid, updated.OwnerSid, StringComparison.Ordinal) ||
            !string.Equals(expected.TaskBackupSha256, updated.TaskBackupSha256, StringComparison.Ordinal) ||
            expected.TaskOriginallyEnabled != updated.TaskOriginallyEnabled ||
            expected.StartMythCoolRequested != updated.StartMythCoolRequested ||
            expected.StartedAtUtc != updated.StartedAtUtc ||
            !string.Equals(expected.Audit.EventName, updated.Audit.EventName, StringComparison.Ordinal) ||
            !string.Equals(expected.Audit.CallerSid, updated.Audit.CallerSid, StringComparison.Ordinal) ||
            !string.Equals(expected.Audit.Source, updated.Audit.Source, StringComparison.Ordinal) ||
            !JsonElement.DeepEquals(expected.Audit.Previous, updated.Audit.Previous))
        {
            throw new InvalidDataException(
                "Immutable takeover-transition identity or audit intent changed during advancement.");
        }

        var validStageAdvance = (expected.Operation, expected.Stage, updated.Operation, updated.Stage) switch
        {
            (TakeoverTransitionOperation.Commit, TakeoverTransitionStage.Prepared,
                TakeoverTransitionOperation.Commit, TakeoverTransitionStage.TaskDisableInProgress or
                    TakeoverTransitionStage.TaskDisabled or TakeoverTransitionStage.OutcomeResolved) => true,
            (TakeoverTransitionOperation.Commit, TakeoverTransitionStage.TaskDisableInProgress,
                TakeoverTransitionOperation.Commit,
                TakeoverTransitionStage.TaskDisabled or TakeoverTransitionStage.OutcomeResolved) => true,
            (TakeoverTransitionOperation.Commit, TakeoverTransitionStage.TaskDisabled,
                TakeoverTransitionOperation.Commit, TakeoverTransitionStage.RuntimeStatePersisted) => true,
            (TakeoverTransitionOperation.Commit, TakeoverTransitionStage.RuntimeStatePersisted,
                TakeoverTransitionOperation.Commit, TakeoverTransitionStage.ConfigurationPersisted) => true,
            (TakeoverTransitionOperation.Commit, TakeoverTransitionStage.ConfigurationPersisted,
                TakeoverTransitionOperation.Commit, TakeoverTransitionStage.OutcomeResolved) => true,
            (TakeoverTransitionOperation.Commit,
                TakeoverTransitionStage.TaskDisableInProgress or
                    TakeoverTransitionStage.TaskDisabled or
                    TakeoverTransitionStage.RuntimeStatePersisted or
                    TakeoverTransitionStage.ConfigurationPersisted,
                TakeoverTransitionOperation.Rollback, TakeoverTransitionStage.Prepared) => true,
            (TakeoverTransitionOperation.Rollback, TakeoverTransitionStage.Prepared,
                TakeoverTransitionOperation.Rollback,
                TakeoverTransitionStage.ControllerStateDecommissioned) => true,
            (TakeoverTransitionOperation.Rollback, TakeoverTransitionStage.ControllerStateDecommissioned,
                TakeoverTransitionOperation.Rollback,
                TakeoverTransitionStage.TaskRestoreInProgress or TakeoverTransitionStage.TaskRestored) => true,
            (TakeoverTransitionOperation.Rollback, TakeoverTransitionStage.TaskRestoreInProgress,
                TakeoverTransitionOperation.Rollback, TakeoverTransitionStage.TaskRestored) => true,
            (TakeoverTransitionOperation.Rollback, TakeoverTransitionStage.TaskRestoreInProgress,
                TakeoverTransitionOperation.Commit, TakeoverTransitionStage.TaskDisabled) => true,
            (TakeoverTransitionOperation.Rollback, TakeoverTransitionStage.TaskRestored,
                TakeoverTransitionOperation.Rollback, TakeoverTransitionStage.OutcomeResolved) => true,
            (TakeoverTransitionOperation.Commit, TakeoverTransitionStage.OutcomeResolved,
                TakeoverTransitionOperation.Commit, TakeoverTransitionStage.AuditAppended) => true,
            (TakeoverTransitionOperation.Rollback, TakeoverTransitionStage.OutcomeResolved,
                TakeoverTransitionOperation.Rollback, TakeoverTransitionStage.AuditAppended) => true,
            _ => false,
        };
        if (!validStageAdvance)
        {
            throw new InvalidDataException("The takeover-transition stage advancement is invalid.");
        }

        if (expected.Stage == TakeoverTransitionStage.AuditAppended ||
            expected.Stage == TakeoverTransitionStage.OutcomeResolved &&
            updated.Stage != TakeoverTransitionStage.AuditAppended ||
            expected.Stage != TakeoverTransitionStage.OutcomeResolved &&
            updated.Stage == TakeoverTransitionStage.AuditAppended)
        {
            throw new InvalidDataException("The takeover-transition audit stage advancement is invalid.");
        }

        if (expected.Stage == TakeoverTransitionStage.OutcomeResolved &&
            (!JsonElement.DeepEquals(expected.Audit.Current, updated.Audit.Current) ||
             expected.Audit.Result != updated.Audit.Result ||
             expected.Audit.Action != updated.Audit.Action ||
             !string.Equals(expected.Audit.ResolvedByCallerSid,
                 updated.Audit.ResolvedByCallerSid, StringComparison.Ordinal) ||
             !string.Equals(expected.Audit.ResolvedBySource,
                 updated.Audit.ResolvedBySource, StringComparison.Ordinal)))
        {
            throw new InvalidDataException(
                "A resolved takeover-transition audit envelope cannot change while being appended.");
        }
    }

    internal static bool Equivalent(
        TakeoverTransitionJournal left,
        TakeoverTransitionJournal right) =>
        left.SchemaVersion == right.SchemaVersion &&
        left.TransitionId == right.TransitionId &&
        left.Operation == right.Operation &&
        left.Stage == right.Stage &&
        string.Equals(left.OwnerSid, right.OwnerSid, StringComparison.Ordinal) &&
        string.Equals(left.TaskBackupSha256, right.TaskBackupSha256, StringComparison.Ordinal) &&
        left.TaskOriginallyEnabled == right.TaskOriginallyEnabled &&
        left.StartMythCoolRequested == right.StartMythCoolRequested &&
        left.StartedAtUtc == right.StartedAtUtc &&
        string.Equals(left.Audit.EventName, right.Audit.EventName, StringComparison.Ordinal) &&
        string.Equals(left.Audit.CallerSid, right.Audit.CallerSid, StringComparison.Ordinal) &&
        string.Equals(left.Audit.Source, right.Audit.Source, StringComparison.Ordinal) &&
        left.Audit.Result == right.Audit.Result &&
        left.Audit.Action == right.Audit.Action &&
        JsonElement.DeepEquals(left.Audit.Previous, right.Audit.Previous) &&
        JsonElement.DeepEquals(left.Audit.Current, right.Audit.Current) &&
        string.Equals(
            left.Audit.ResolvedByCallerSid,
            right.Audit.ResolvedByCallerSid,
            StringComparison.Ordinal) &&
        string.Equals(
            left.Audit.ResolvedBySource,
            right.Audit.ResolvedBySource,
            StringComparison.Ordinal);
}
