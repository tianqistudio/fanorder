using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace V12UltraController.Service;

internal enum SafetyOperationKind
{
    SessionOnly,
    HidWithOn100Recovery,
}

internal enum SafetyOperationAuditStage
{
    Prepared,
    EffectInProgress,
    OutcomeResolved,
    AuditAppended,
}

internal enum SafetyOperationAuditOutcome
{
    Undetermined,
    Success,
    NotApplied,
    Compensated,
    Indeterminate,
}

/// <summary>
/// Durable, privacy-bounded audit intent for one safety-sensitive operation. This type is an
/// independent persistence primitive: runtime recovery code must explicitly interpret the
/// event-specific snapshots before it advances or deletes a journal.
/// </summary>
internal sealed record SafetyOperationAuditJournal(
    int SchemaVersion,
    Guid TransactionId,
    string EventName,
    string CallerSid,
    string Source,
    SafetyOperationKind Kind,
    DateTimeOffset PreparedAtUtc,
    JsonElement Previous,
    JsonElement Intended,
    JsonElement Observed,
    bool RequiresOn100Recovery,
    SafetyOperationAuditStage Stage,
    SafetyOperationAuditOutcome Outcome)
{
    public const int CurrentSchemaVersion = 1;

    public void Validate()
    {
        if (SchemaVersion != CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Unsupported safety-operation audit schema {SchemaVersion}; expected {CurrentSchemaVersion}.");
        }

        if (TransactionId == Guid.Empty)
        {
            throw new InvalidDataException(
                "The safety-operation audit transactionId must not be empty.");
        }

        ValidateLabel(EventName, nameof(EventName), 128, requireSid: false);
        ValidateLabel(CallerSid, nameof(CallerSid), 256, requireSid: true);
        ValidateLabel(Source, nameof(Source), 256, requireSid: false);
        if (!Enum.IsDefined(Kind))
        {
            throw new InvalidDataException("The safety-operation audit kind is invalid.");
        }

        var expectedRecovery = Kind == SafetyOperationKind.HidWithOn100Recovery;
        if (RequiresOn100Recovery != expectedRecovery)
        {
            throw new InvalidDataException(
                "The safety-operation audit recovery requirement does not match its kind.");
        }

        if (PreparedAtUtc == default || PreparedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new InvalidDataException(
                "The safety-operation audit preparedAtUtc must be a non-default UTC value.");
        }

        if (!Enum.IsDefined(Stage) || !Enum.IsDefined(Outcome))
        {
            throw new InvalidDataException(
                "The safety-operation audit stage or outcome is invalid.");
        }

        var resolved = Stage is SafetyOperationAuditStage.OutcomeResolved or
            SafetyOperationAuditStage.AuditAppended;
        if (resolved)
        {
            if (Outcome == SafetyOperationAuditOutcome.Undetermined ||
                Observed.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException(
                    "A resolved safety-operation audit requires a terminal outcome and observed snapshot.");
            }
        }
        else if (Outcome != SafetyOperationAuditOutcome.Undetermined ||
                 Observed.ValueKind != JsonValueKind.Null)
        {
            throw new InvalidDataException(
                "An unresolved safety-operation audit cannot claim an outcome or observed snapshot.");
        }

        AuditSnapshotPolicy.Validate(Previous, "safety operation previous");
        AuditSnapshotPolicy.Validate(Intended, "safety operation intended");
        if (resolved)
        {
            AuditSnapshotPolicy.Validate(Observed, "safety operation observed");
        }
    }

    private static void ValidateLabel(
        string? value,
        string name,
        int maximumLength,
        bool requireSid)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > maximumLength ||
            value.Any(char.IsControl) ||
            requireSid && !value.StartsWith("S-", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"The safety-operation audit {name} must be a valid bounded single-line value.");
        }
    }
}

internal sealed class SafetyOperationAuditJournalStore
{
    internal const int MaximumDocumentBytes = 256 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private static readonly HashSet<string> ExpectedProperties = new(StringComparer.Ordinal)
    {
        "schemaVersion",
        "transactionId",
        "eventName",
        "callerSid",
        "source",
        "kind",
        "preparedAtUtc",
        "previous",
        "intended",
        "observed",
        "requiresOn100Recovery",
        "stage",
        "outcome",
    };
    private static readonly HashSet<string> CanonicalKinds = new(StringComparer.Ordinal)
    {
        "sessionOnly",
        "hidWithOn100Recovery",
    };
    private static readonly HashSet<string> CanonicalStages = new(StringComparer.Ordinal)
    {
        "prepared",
        "effectInProgress",
        "outcomeResolved",
        "auditAppended",
    };
    private static readonly HashSet<string> CanonicalOutcomes = new(StringComparer.Ordinal)
    {
        "undetermined",
        "success",
        "notApplied",
        "compensated",
        "indeterminate",
    };

    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly ISafetyOperationAuditJournalPersistence persistence;
    private readonly TimeProvider timeProvider;

    public SafetyOperationAuditJournalStore(string path, TimeProvider timeProvider)
        : this(new FileSafetyOperationAuditJournalPersistence(path), timeProvider)
    {
    }

    public SafetyOperationAuditJournalStore(ServicePaths paths, TimeProvider timeProvider)
        : this(
            (paths ?? throw new ArgumentNullException(nameof(paths))).SafetyOperationAuditPendingPath,
            timeProvider)
    {
    }

    internal SafetyOperationAuditJournalStore(
        ISafetyOperationAuditJournalPersistence persistence,
        TimeProvider timeProvider)
    {
        this.persistence = persistence ?? throw new ArgumentNullException(nameof(persistence));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public SafetyOperationAuditJournal Create(
        string eventName,
        string callerSid,
        string source,
        SafetyOperationKind kind,
        object previous,
        object intended,
        bool requiresOn100Recovery)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(intended);
        var journal = new SafetyOperationAuditJournal(
            SafetyOperationAuditJournal.CurrentSchemaVersion,
            Guid.NewGuid(),
            eventName,
            callerSid,
            source,
            kind,
            timeProvider.GetUtcNow().ToUniversalTime(),
            SerializeSnapshot(previous),
            SerializeSnapshot(intended),
            JsonSerializer.SerializeToElement<object?>(null, JsonOptions),
            requiresOn100Recovery,
            SafetyOperationAuditStage.Prepared,
            SafetyOperationAuditOutcome.Undetermined);
        journal.Validate();
        return journal;
    }

    public async Task PrepareAsync(
        SafetyOperationAuditJournal journal,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(journal);
        journal.Validate();
        if (journal.Stage != SafetyOperationAuditStage.Prepared)
        {
            throw new InvalidDataException(
                "A new safety-operation audit must begin at the Prepared stage.");
        }

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (await LoadCoreAsync(cancellationToken).ConfigureAwait(false) is not null)
            {
                throw new InvalidDataException(
                    "An unresolved safety-operation audit already exists; it cannot be overwritten.");
            }

            await SaveAndVerifyCoreAsync(journal, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<SafetyOperationAuditJournal?> LoadAsync(
        CancellationToken cancellationToken = default)
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

    public Task<SafetyOperationAuditJournal> MarkEffectInProgressAsync(
        SafetyOperationAuditJournal expected,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        return AdvanceAsync(
            expected,
            expected with { Stage = SafetyOperationAuditStage.EffectInProgress },
            SafetyOperationAuditStage.Prepared,
            cancellationToken);
    }

    public Task<SafetyOperationAuditJournal> ResolveAsync(
        SafetyOperationAuditJournal expected,
        SafetyOperationAuditOutcome outcome,
        object observed,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(observed);
        if (outcome == SafetyOperationAuditOutcome.Undetermined || !Enum.IsDefined(outcome))
        {
            throw new InvalidDataException(
                "A safety-operation audit can resolve only to a terminal outcome.");
        }

        return AdvanceAsync(
            expected,
            expected with
            {
                Stage = SafetyOperationAuditStage.OutcomeResolved,
                Outcome = outcome,
                Observed = SerializeSnapshot(observed),
            },
            SafetyOperationAuditStage.EffectInProgress,
            cancellationToken);
    }

    public Task<SafetyOperationAuditJournal> ResolvePreparedAsNotAppliedAsync(
        SafetyOperationAuditJournal expected,
        object observed,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(observed);
        return AdvanceAsync(
            expected,
            expected with
            {
                Stage = SafetyOperationAuditStage.OutcomeResolved,
                Outcome = SafetyOperationAuditOutcome.NotApplied,
                Observed = SerializeSnapshot(observed),
            },
            SafetyOperationAuditStage.Prepared,
            cancellationToken,
            allowPreparedNotAppliedResolution: true);
    }

    public Task<SafetyOperationAuditJournal> MarkAuditAppendedAsync(
        SafetyOperationAuditJournal expected,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        return AdvanceAsync(
            expected,
            expected with { Stage = SafetyOperationAuditStage.AuditAppended },
            SafetyOperationAuditStage.OutcomeResolved,
            cancellationToken);
    }

    public async Task DeleteExactAsync(
        SafetyOperationAuditJournal expected,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        expected.Validate();
        if (expected.Stage != SafetyOperationAuditStage.AuditAppended)
        {
            throw new InvalidDataException(
                "A safety-operation audit can be deleted only after its audit was durably appended.");
        }

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await LoadCoreAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException(
                    "The expected safety-operation audit no longer exists.");
            if (!Equivalent(current, expected))
            {
                throw new InvalidDataException(
                    "The safety-operation audit changed unexpectedly; refusing to delete another transaction.");
            }

            await persistence.DeleteAsync(cancellationToken).ConfigureAwait(false);
            if (await persistence.ReadAsync(cancellationToken).ConfigureAwait(false) is not null)
            {
                throw new IOException(
                    "The safety-operation audit recovery barrier remained after deletion.");
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<SafetyOperationAuditJournal> AdvanceAsync(
        SafetyOperationAuditJournal expected,
        SafetyOperationAuditJournal updated,
        SafetyOperationAuditStage requiredStage,
        CancellationToken cancellationToken,
        bool allowPreparedNotAppliedResolution = false)
    {
        expected.Validate();
        updated.Validate();
        var preparedNotAppliedResolution = allowPreparedNotAppliedResolution &&
            expected.Stage == SafetyOperationAuditStage.Prepared &&
            updated.Stage == SafetyOperationAuditStage.OutcomeResolved &&
            updated.Outcome == SafetyOperationAuditOutcome.NotApplied;
        if (expected.Stage != requiredStage ||
            !IsNextStage(expected.Stage, updated.Stage) && !preparedNotAppliedResolution)
        {
            throw new InvalidDataException(
                $"Safety-operation audit cannot advance from {expected.Stage} to {updated.Stage}.");
        }

        ValidateImmutableIntent(expected, updated);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await LoadCoreAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException(
                    "The expected safety-operation audit no longer exists.");
            if (!Equivalent(current, expected))
            {
                throw new InvalidDataException(
                    "The safety-operation audit changed unexpectedly; refusing to advance another transaction.");
            }

            await SaveAndVerifyCoreAsync(updated, cancellationToken).ConfigureAwait(false);
            return updated;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<SafetyOperationAuditJournal?> LoadCoreAsync(
        CancellationToken cancellationToken)
    {
        var json = await persistence.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (json is null)
        {
            return null;
        }

        if (Encoding.UTF8.GetByteCount(json) is <= 0 or > MaximumDocumentBytes)
        {
            throw new InvalidDataException(
                "The safety-operation audit journal has an invalid document length.");
        }

        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = AuditSnapshotPolicy.MaximumDepth + 4,
            });
            ValidateDocumentShape(document.RootElement);
            var journal = document.RootElement.Deserialize<SafetyOperationAuditJournal>(JsonOptions)
                ?? throw new InvalidDataException(
                    "The safety-operation audit journal is empty.");
            journal = journal with
            {
                Previous = journal.Previous.Clone(),
                Intended = journal.Intended.Clone(),
                Observed = journal.Observed.Clone(),
            };
            journal.Validate();
            return journal;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "The safety-operation audit journal is not valid strict JSON.",
                exception);
        }
    }

    private async Task SaveAndVerifyCoreAsync(
        SafetyOperationAuditJournal journal,
        CancellationToken cancellationToken)
    {
        journal.Validate();
        var json = JsonSerializer.Serialize(journal, JsonOptions);
        if (Encoding.UTF8.GetByteCount(json) > MaximumDocumentBytes)
        {
            throw new InvalidDataException(
                "The safety-operation audit journal exceeds its maximum document length.");
        }

        await persistence.WriteAsync(json, cancellationToken).ConfigureAwait(false);
        var readBack = await LoadCoreAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new IOException(
                "The safety-operation audit journal was missing after persistence.");
        if (!Equivalent(readBack, journal))
        {
            throw new InvalidDataException(
                "The safety-operation audit journal did not match its durable readback.");
        }
    }

    private static JsonElement SerializeSnapshot(object value) =>
        JsonSerializer.SerializeToElement(value, value.GetType(), JsonOptions).Clone();

    private static void ValidateDocumentShape(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException(
                "The safety-operation audit journal must be a JSON object.");
        }

        var remaining = new HashSet<string>(ExpectedProperties, StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (!ExpectedProperties.Contains(property.Name))
            {
                throw new InvalidDataException(
                    $"The safety-operation audit journal contains unknown or incorrectly-cased property '{property.Name}'.");
            }

            if (!remaining.Remove(property.Name))
            {
                throw new InvalidDataException(
                    $"The safety-operation audit journal contains duplicate property '{property.Name}'.");
            }
        }

        if (remaining.Count > 0)
        {
            throw new InvalidDataException(
                $"The safety-operation audit journal is missing required properties: {string.Join(", ", remaining.Order())}.");
        }

        RequireKind(root, "schemaVersion", JsonValueKind.Number);
        RequireKind(root, "transactionId", JsonValueKind.String);
        RequireKind(root, "eventName", JsonValueKind.String);
        RequireKind(root, "callerSid", JsonValueKind.String);
        RequireKind(root, "source", JsonValueKind.String);
        RequireKind(root, "kind", JsonValueKind.String);
        RequireKind(root, "preparedAtUtc", JsonValueKind.String);
        RequireKind(root, "previous", JsonValueKind.Object);
        RequireKind(root, "intended", JsonValueKind.Object);
        RequireKind(root, "observed", JsonValueKind.Null, JsonValueKind.Object);
        RequireKind(root, "requiresOn100Recovery", JsonValueKind.True, JsonValueKind.False);
        RequireKind(root, "stage", JsonValueKind.String);
        RequireKind(root, "outcome", JsonValueKind.String);

        var transactionIdText = root.GetProperty("transactionId").GetString();
        if (!Guid.TryParseExact(transactionIdText, "D", out var transactionId) ||
            !string.Equals(transactionIdText, transactionId.ToString("D"), StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The safety-operation audit transactionId is not canonical.");
        }

        RequireCanonicalToken(root, "kind", CanonicalKinds);
        RequireCanonicalToken(root, "stage", CanonicalStages);
        RequireCanonicalToken(root, "outcome", CanonicalOutcomes);
    }

    private static void RequireKind(
        JsonElement parent,
        string propertyName,
        params JsonValueKind[] allowedKinds)
    {
        if (!allowedKinds.Contains(parent.GetProperty(propertyName).ValueKind))
        {
            throw new InvalidDataException(
                $"The safety-operation audit property '{propertyName}' has an invalid JSON type.");
        }
    }

    private static void RequireCanonicalToken(
        JsonElement parent,
        string propertyName,
        IReadOnlySet<string> allowedTokens)
    {
        var value = parent.GetProperty(propertyName).GetString();
        if (value is null || !allowedTokens.Contains(value))
        {
            throw new InvalidDataException(
                $"The safety-operation audit property '{propertyName}' is not a canonical value.");
        }
    }

    private static bool IsNextStage(
        SafetyOperationAuditStage current,
        SafetyOperationAuditStage next) =>
        (current, next) is
            (SafetyOperationAuditStage.Prepared, SafetyOperationAuditStage.EffectInProgress) or
            (SafetyOperationAuditStage.EffectInProgress, SafetyOperationAuditStage.OutcomeResolved) or
            (SafetyOperationAuditStage.OutcomeResolved, SafetyOperationAuditStage.AuditAppended);

    private static void ValidateImmutableIntent(
        SafetyOperationAuditJournal expected,
        SafetyOperationAuditJournal updated)
    {
        if (expected.SchemaVersion != updated.SchemaVersion ||
            expected.TransactionId != updated.TransactionId ||
            !string.Equals(expected.EventName, updated.EventName, StringComparison.Ordinal) ||
            !string.Equals(expected.CallerSid, updated.CallerSid, StringComparison.Ordinal) ||
            !string.Equals(expected.Source, updated.Source, StringComparison.Ordinal) ||
            expected.Kind != updated.Kind ||
            expected.PreparedAtUtc != updated.PreparedAtUtc ||
            expected.RequiresOn100Recovery != updated.RequiresOn100Recovery ||
            !JsonElement.DeepEquals(expected.Previous, updated.Previous) ||
            !JsonElement.DeepEquals(expected.Intended, updated.Intended))
        {
            throw new InvalidDataException(
                "The immutable safety-operation audit identity or intent changed during advancement.");
        }

        if (expected.Stage == SafetyOperationAuditStage.OutcomeResolved &&
            (expected.Outcome != updated.Outcome ||
             !JsonElement.DeepEquals(expected.Observed, updated.Observed)))
        {
            throw new InvalidDataException(
                "A resolved safety-operation audit cannot change while its audit is appended.");
        }
    }

    private static bool Equivalent(
        SafetyOperationAuditJournal left,
        SafetyOperationAuditJournal right) =>
        left.SchemaVersion == right.SchemaVersion &&
        left.TransactionId == right.TransactionId &&
        string.Equals(left.EventName, right.EventName, StringComparison.Ordinal) &&
        string.Equals(left.CallerSid, right.CallerSid, StringComparison.Ordinal) &&
        string.Equals(left.Source, right.Source, StringComparison.Ordinal) &&
        left.Kind == right.Kind &&
        left.PreparedAtUtc == right.PreparedAtUtc &&
        JsonElement.DeepEquals(left.Previous, right.Previous) &&
        JsonElement.DeepEquals(left.Intended, right.Intended) &&
        JsonElement.DeepEquals(left.Observed, right.Observed) &&
        left.RequiresOn100Recovery == right.RequiresOn100Recovery &&
        left.Stage == right.Stage &&
        left.Outcome == right.Outcome;

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(
            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        return options;
    }
}

internal interface ISafetyOperationAuditJournalPersistence
{
    Task<string?> ReadAsync(CancellationToken cancellationToken);

    Task WriteAsync(string json, CancellationToken cancellationToken);

    Task DeleteAsync(CancellationToken cancellationToken);
}

internal sealed class FileSafetyOperationAuditJournalPersistence :
    ISafetyOperationAuditJournalPersistence
{
    private readonly string path;

    public FileSafetyOperationAuditJournalPersistence(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        this.path = Path.GetFullPath(path);
    }

    public async Task<string?> ReadAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length is <= 0 or > SafetyOperationAuditJournalStore.MaximumDocumentBytes)
            {
                throw new InvalidDataException(
                    "The safety-operation audit journal has an invalid document length.");
            }

            using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
            return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    public Task WriteAsync(string json, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(json);
        return AtomicFile.WriteTextAsync(path, json, cancellationToken);
    }

    public Task DeleteAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        File.Delete(path);
        return Task.CompletedTask;
    }
}
