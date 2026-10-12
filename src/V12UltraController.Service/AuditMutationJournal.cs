using System.Text.Json;
using System.Text.Json.Serialization;
using V12UltraController.Core.Configuration;
using V12UltraController.Core.Control;

namespace V12UltraController.Service;

internal enum AuditMutationStage
{
    Prepared,
    MutationInProgress,
    StateApplied,
    MutationNotApplied,
    AuditAppended,
}

internal enum AuditMutationOutcome
{
    Undetermined,
    Applied,
    NotApplied,
}

/// <summary>
/// A single, durable intent for one externally attributable state mutation.
/// The fixed ProgramData file is deliberately separate from the rolling audit
/// log so a crash between business-state persistence and the final JSONL append
/// always leaves a recovery barrier.
/// </summary>
internal sealed record AuditMutationJournal(
    int SchemaVersion,
    Guid TransactionId,
    string EventName,
    string CallerSid,
    string Source,
    DateTimeOffset PreparedAtUtc,
    AuditMutationStage Stage,
    AuditMutationOutcome Outcome,
    JsonElement Previous,
    JsonElement Current)
{
    public const int CurrentSchemaVersion = 1;

    public void Validate()
    {
        if (SchemaVersion != CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Unsupported audit-mutation schema {SchemaVersion}; expected {CurrentSchemaVersion}.");
        }

        if (TransactionId == Guid.Empty)
        {
            throw new InvalidDataException("The audit-mutation transactionId must not be empty.");
        }

        ValidateLabel(EventName, nameof(EventName), 128);
        ValidateLabel(CallerSid, nameof(CallerSid), 256);
        ValidateLabel(Source, nameof(Source), 256);
        if (PreparedAtUtc == default || PreparedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new InvalidDataException("The audit-mutation preparedAtUtc must be a non-default UTC value.");
        }

        if (!Enum.IsDefined(Stage))
        {
            throw new InvalidDataException("The audit-mutation stage is invalid.");
        }

        if (!Enum.IsDefined(Outcome) ||
            (Stage is AuditMutationStage.Prepared or AuditMutationStage.MutationInProgress) &&
            Outcome != AuditMutationOutcome.Undetermined ||
            Stage == AuditMutationStage.StateApplied && Outcome != AuditMutationOutcome.Applied ||
            Stage == AuditMutationStage.MutationNotApplied && Outcome != AuditMutationOutcome.NotApplied ||
            Stage == AuditMutationStage.AuditAppended && Outcome == AuditMutationOutcome.Undetermined)
        {
            throw new InvalidDataException(
                "The audit-mutation outcome does not match its durable stage.");
        }

        AuditSnapshotPolicy.Validate(Previous, "previous");
        AuditSnapshotPolicy.Validate(Current, "current");
    }

    private static void ValidateLabel(string? value, string name, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength || value.Any(char.IsControl))
        {
            throw new InvalidDataException(
                $"The audit-mutation {name} must be a non-empty bounded single-line value.");
        }
    }
}

internal sealed class AuditMutationJournalStore
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    internal static JsonSerializerOptions SerializerOptions => JsonOptions;
    private static readonly HashSet<string> ExpectedProperties = new(StringComparer.Ordinal)
    {
        "schemaVersion",
        "transactionId",
        "eventName",
        "callerSid",
        "source",
        "preparedAtUtc",
        "stage",
        "outcome",
        "previous",
        "current",
    };

    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly IAuditMutationJournalPersistence persistence;
    private readonly TimeProvider timeProvider;

    public AuditMutationJournalStore(ServicePaths paths, TimeProvider timeProvider)
        : this(new FileAuditMutationJournalPersistence(paths), timeProvider)
    {
    }

    internal AuditMutationJournalStore(
        IAuditMutationJournalPersistence persistence,
        TimeProvider timeProvider)
    {
        this.persistence = persistence ?? throw new ArgumentNullException(nameof(persistence));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public AuditMutationJournal Create(
        string eventName,
        string callerSid,
        string source,
        object previous,
        object current)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(current);
        var journal = new AuditMutationJournal(
            AuditMutationJournal.CurrentSchemaVersion,
            Guid.NewGuid(),
            eventName,
            callerSid,
            source,
            timeProvider.GetUtcNow().ToUniversalTime(),
            AuditMutationStage.Prepared,
            AuditMutationOutcome.Undetermined,
            JsonSerializer.SerializeToElement(previous, JsonOptions),
            JsonSerializer.SerializeToElement(current, JsonOptions));
        journal.Validate();
        return journal;
    }

    public async Task PrepareAsync(
        AuditMutationJournal journal,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(journal);
        journal.Validate();
        if (journal.Stage != AuditMutationStage.Prepared)
        {
            throw new InvalidDataException("A new audit mutation must begin at the Prepared stage.");
        }

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (await LoadCoreAsync(cancellationToken).ConfigureAwait(false) is not null)
            {
                throw new InvalidDataException(
                    "An unresolved audit mutation already exists; no later mutation may replace it.");
            }

            await SaveCoreAsync(journal, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<AuditMutationJournal?> LoadAsync(CancellationToken cancellationToken = default)
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

    public async Task<AuditMutationJournal> AdvanceAsync(
        AuditMutationJournal expected,
        AuditMutationStage nextStage,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        expected.Validate();
        if (!IsNextStage(expected.Stage, nextStage))
        {
            throw new InvalidDataException(
                $"Audit mutation cannot advance from {expected.Stage} to {nextStage}.");
        }

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await LoadCoreAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("The expected audit mutation no longer exists.");
            if (!JournalsEqual(current, expected))
            {
                throw new InvalidDataException(
                    "The audit mutation changed unexpectedly; refusing to advance another transaction.");
            }

            var advanced = expected with
            {
                Stage = nextStage,
                Outcome = nextStage switch
                {
                    AuditMutationStage.StateApplied => AuditMutationOutcome.Applied,
                    AuditMutationStage.MutationNotApplied => AuditMutationOutcome.NotApplied,
                    _ => expected.Outcome,
                },
            };
            await SaveCoreAsync(advanced, cancellationToken).ConfigureAwait(false);
            return advanced;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task DeleteExactAsync(
        AuditMutationJournal expected,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        expected.Validate();
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await LoadCoreAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("The expected audit mutation no longer exists.");
            if (!JournalsEqual(current, expected))
            {
                throw new InvalidDataException(
                    "The audit mutation changed unexpectedly; refusing to delete another transaction.");
            }

            await persistence.DeleteAsync(cancellationToken).ConfigureAwait(false);
            if (await persistence.ReadAsync(cancellationToken).ConfigureAwait(false) is not null)
            {
                throw new IOException("The audit-mutation recovery barrier remained after deletion.");
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<AuditMutationJournal?> LoadCoreAsync(CancellationToken cancellationToken)
    {
        var json = await persistence.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (json is null)
        {
            return null;
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
            var journal = document.RootElement.Deserialize<AuditMutationJournal>(JsonOptions)
                ?? throw new InvalidDataException("The audit-mutation journal is empty.");
            journal = journal with
            {
                Previous = journal.Previous.Clone(),
                Current = journal.Current.Clone(),
            };
            journal.Validate();
            return journal;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The audit-mutation journal is not valid JSON.", exception);
        }
    }

    private Task SaveCoreAsync(
        AuditMutationJournal journal,
        CancellationToken cancellationToken)
    {
        journal.Validate();
        return persistence.WriteAsync(JsonSerializer.Serialize(journal, JsonOptions), cancellationToken);
    }

    private static void ValidateDocumentShape(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("The audit-mutation journal must be a JSON object.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (!ExpectedProperties.Contains(property.Name) || !seen.Add(property.Name))
            {
                throw new InvalidDataException(
                    $"The audit-mutation journal contains an unknown, misspelled, or duplicate property: {property.Name}.");
            }
        }

        if (seen.Count != ExpectedProperties.Count)
        {
            throw new InvalidDataException("The audit-mutation journal is missing a required property.");
        }
    }

    private static bool IsNextStage(AuditMutationStage current, AuditMutationStage next) =>
        (current, next) is
            (AuditMutationStage.Prepared, AuditMutationStage.MutationInProgress) or
            (AuditMutationStage.Prepared, AuditMutationStage.MutationNotApplied) or
            (AuditMutationStage.MutationInProgress, AuditMutationStage.StateApplied) or
            (AuditMutationStage.MutationInProgress, AuditMutationStage.MutationNotApplied) or
            (AuditMutationStage.StateApplied, AuditMutationStage.AuditAppended) or
            (AuditMutationStage.MutationNotApplied, AuditMutationStage.AuditAppended);

    private static bool JournalsEqual(AuditMutationJournal left, AuditMutationJournal right) =>
        left.SchemaVersion == right.SchemaVersion &&
        left.TransactionId == right.TransactionId &&
        string.Equals(left.EventName, right.EventName, StringComparison.Ordinal) &&
        string.Equals(left.CallerSid, right.CallerSid, StringComparison.Ordinal) &&
        string.Equals(left.Source, right.Source, StringComparison.Ordinal) &&
        left.PreparedAtUtc == right.PreparedAtUtc &&
        left.Stage == right.Stage &&
        left.Outcome == right.Outcome &&
        JsonElement.DeepEquals(left.Previous, right.Previous) &&
        JsonElement.DeepEquals(left.Current, right.Current);

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}

/// <summary>
/// Complete, privacy-bounded configuration state used by the mutation WAL. It contains every
/// value that can affect fan policy or shutdown eligibility, but no device path, serial number,
/// raw HID payload, task XML, username, or unbounded caller-provided data.
/// </summary>
internal sealed record AuditConfigurationSnapshot(
    int SchemaVersion,
    bool Commissioned,
    ControlMode Mode,
    byte ManualSpeedPercent,
    byte StartupFanPercent,
    AuditFanCurvePointSnapshot[] Curve,
    SystemShutdownFanBehavior SystemShutdownFanBehavior,
    AuditShutdownQualificationSnapshot? ShutdownOffQualification)
{
    private static readonly HashSet<string> ExpectedProperties = new(StringComparer.Ordinal)
    {
        "schemaVersion",
        "commissioned",
        "mode",
        "manualSpeedPercent",
        "startupFanPercent",
        "curve",
        "systemShutdownFanBehavior",
        "shutdownOffQualification",
    };

    public static AuditConfigurationSnapshot FromConfiguration(ControllerConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        configuration.Validate();
        return new AuditConfigurationSnapshot(
            configuration.SchemaVersion,
            configuration.Commissioned,
            configuration.Mode,
            configuration.ManualSpeedPercent,
            configuration.StartupFanPercent,
            configuration.Curve.Points
                .Select(static point => new AuditFanCurvePointSnapshot(
                    point.TemperatureCelsius,
                    point.FanPercent))
                .ToArray(),
            configuration.SystemShutdownFanBehavior,
            configuration.ShutdownOffQualification is null
                ? null
                : AuditShutdownQualificationSnapshot.FromQualification(
                    configuration.ShutdownOffQualification));
    }

    public ControllerConfiguration ToConfiguration()
    {
        if (Curve is null)
        {
            throw new InvalidDataException("The audit configuration snapshot curve is required.");
        }

        if (SchemaVersion is not 2 and not ControllerConfiguration.CurrentSchemaVersion)
        {
            throw new InvalidDataException($"Unsupported audit configuration schema {SchemaVersion}.");
        }

        var configuration = new ControllerConfiguration
        {
            // Audit snapshots from the previous service schema are upgraded in
            // memory; the missing startup kick value is filled below.
            SchemaVersion = ControllerConfiguration.CurrentSchemaVersion,
            Commissioned = Commissioned,
            Mode = Mode,
            ManualSpeedPercent = ManualSpeedPercent,
            StartupFanPercent = StartupFanPercent is >= 10 and <= 100
                ? StartupFanPercent
                : ControllerConfiguration.DefaultStartupFanPercent,
            Curve = new FanCurve(Curve.Select(static point =>
                new FanCurvePoint(point.TemperatureCelsius, point.FanPercent)).ToArray()),
            SystemShutdownFanBehavior = SystemShutdownFanBehavior,
            ShutdownOffQualification = ShutdownOffQualification?.ToQualification(),
        };
        configuration.Validate();
        return configuration;
    }

    public static ControllerConfiguration ParseConfiguration(JsonElement element)
    {
        ValidateObjectShape(element, ExpectedProperties, "configuration");
        var curve = element.GetProperty("curve");
        if (curve.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("The audit configuration snapshot curve must be an array.");
        }

        foreach (var point in curve.EnumerateArray())
        {
            ValidateObjectShape(point, AuditFanCurvePointSnapshot.ExpectedProperties, "curve point");
        }

        var qualification = element.GetProperty("shutdownOffQualification");
        if (qualification.ValueKind != JsonValueKind.Null)
        {
            ValidateObjectShape(
                qualification,
                AuditShutdownQualificationSnapshot.ExpectedProperties,
                "shutdown qualification");
        }

        try
        {
            var snapshot = element.Deserialize<AuditConfigurationSnapshot>(
                    AuditMutationJournalStore.SerializerOptions)
                ?? throw new InvalidDataException("The audit configuration snapshot is empty.");
            return snapshot.ToConfiguration();
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The audit configuration snapshot is invalid.", exception);
        }
        catch (ConfigurationValidationException exception)
        {
            throw new InvalidDataException(
                "The audit configuration snapshot failed semantic validation.", exception);
        }
    }

    private static void ValidateObjectShape(
        JsonElement element,
        IReadOnlySet<string> expected,
        string description)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"The audit {description} snapshot must be an object.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!expected.Contains(property.Name) || !seen.Add(property.Name))
            {
                throw new InvalidDataException(
                    $"The audit {description} snapshot contains an unknown, misspelled, or duplicate property: {property.Name}.");
            }
        }

        // Snapshots written by the schema-2 service predate the optional startup
        // kick setting. Treat that one missing value as the documented 50% default
        // so an interrupted older mutation can still be recovered safely.
        var onlyLegacyStartupValueMissing = expected.Contains("startupFanPercent") &&
            seen.Count == expected.Count - 1 && !seen.Contains("startupFanPercent");
        if (seen.Count != expected.Count && !onlyLegacyStartupValueMissing)
        {
            throw new InvalidDataException($"The audit {description} snapshot is missing a required property.");
        }
    }
}

internal sealed record AuditFanCurvePointSnapshot(double TemperatureCelsius, byte FanPercent)
{
    internal static readonly HashSet<string> ExpectedProperties = new(StringComparer.Ordinal)
    {
        "temperatureCelsius",
        "fanPercent",
    };
}

internal sealed record AuditShutdownQualificationSnapshot(
    int ProtocolRevision,
    ushort VendorId,
    ushort ProductId,
    ushort FirmwareVersion,
    ushort UsagePage,
    ushort Usage,
    ushort FeatureReportLength,
    bool ShutdownOffReadbackConfirmed,
    bool StartupOnAt100ReadbackConfirmed,
    bool LightingStatePreserved,
    DateTimeOffset QualifiedAtUtc)
{
    internal static readonly HashSet<string> ExpectedProperties = new(StringComparer.Ordinal)
    {
        "protocolRevision",
        "vendorId",
        "productId",
        "firmwareVersion",
        "usagePage",
        "usage",
        "featureReportLength",
        "shutdownOffReadbackConfirmed",
        "startupOnAt100ReadbackConfirmed",
        "lightingStatePreserved",
        "qualifiedAtUtc",
    };

    public static AuditShutdownQualificationSnapshot FromQualification(
        SystemShutdownOffQualification qualification) =>
        new(
            qualification.ProtocolRevision,
            qualification.VendorId,
            qualification.ProductId,
            qualification.FirmwareVersion,
            qualification.UsagePage,
            qualification.Usage,
            qualification.FeatureReportLength,
            qualification.ShutdownOffReadbackConfirmed,
            qualification.StartupOnAt100ReadbackConfirmed,
            qualification.LightingStatePreserved,
            qualification.QualifiedAtUtc.ToUniversalTime());

    public SystemShutdownOffQualification ToQualification() => new()
    {
        ProtocolRevision = ProtocolRevision,
        VendorId = VendorId,
        ProductId = ProductId,
        FirmwareVersion = FirmwareVersion,
        UsagePage = UsagePage,
        Usage = Usage,
        FeatureReportLength = FeatureReportLength,
        ShutdownOffReadbackConfirmed = ShutdownOffReadbackConfirmed,
        StartupOnAt100ReadbackConfirmed = StartupOnAt100ReadbackConfirmed,
        LightingStatePreserved = LightingStatePreserved,
        QualifiedAtUtc = QualifiedAtUtc,
    };
}

internal interface IAuditMutationJournalPersistence
{
    Task<string?> ReadAsync(CancellationToken cancellationToken);

    Task WriteAsync(string json, CancellationToken cancellationToken);

    Task DeleteAsync(CancellationToken cancellationToken);
}

internal sealed class FileAuditMutationJournalPersistence : IAuditMutationJournalPersistence
{
    private readonly string path;

    public FileAuditMutationJournalPersistence(ServicePaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        path = paths.AuditMutationPendingPath;
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

    public Task WriteAsync(string json, CancellationToken cancellationToken) =>
        AtomicFile.WriteTextAsync(path, json, cancellationToken);

    public Task DeleteAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        File.Delete(path);
        return Task.CompletedTask;
    }
}

internal static class AuditSnapshotPolicy
{
    public const int MaximumDepth = 16;
    private const int MaximumSnapshotBytes = 64 * 1024;
    private const int MaximumPropertyNameCharacters = 128;
    private const int MaximumStringCharacters = 4096;

    private static readonly string[] SensitiveStringMarkers =
    [
        "<?xml",
        "<Task",
        @"\\?\",
        @"\\.\",
        @"HID\VID_",
        "HID#VID_",
        @"USB\VID_",
        "USB#VID_",
    ];

    public static void Validate(JsonElement snapshot, string name)
    {
        if (snapshot.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"The audit-mutation {name} snapshot must be a JSON object.");
        }

        if (JsonSerializer.SerializeToUtf8Bytes(snapshot).Length > MaximumSnapshotBytes)
        {
            throw new InvalidDataException(
                $"The audit-mutation {name} snapshot exceeds {MaximumSnapshotBytes} bytes.");
        }

        ValidateElement(snapshot, name, depth: 0);
    }

    private static void ValidateElement(JsonElement element, string path, int depth)
    {
        if (depth > MaximumDepth)
        {
            throw new InvalidDataException("The audit-mutation snapshot exceeds the maximum nesting depth.");
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    if (string.IsNullOrWhiteSpace(property.Name) ||
                        property.Name.Length > MaximumPropertyNameCharacters ||
                        property.Name.Any(char.IsControl) ||
                        !seen.Add(property.Name))
                    {
                        throw new InvalidDataException(
                            $"The audit-mutation snapshot contains an invalid or duplicate property at {path}.");
                    }

                    RejectSensitiveProperty(property.Name);
                    ValidateElement(property.Value, $"{path}.{property.Name}", depth + 1);
                }

                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    ValidateElement(item, $"{path}[]", depth + 1);
                }

                break;

            case JsonValueKind.String:
                var value = element.GetString() ?? string.Empty;
                if (value.Length > MaximumStringCharacters || value.Any(char.IsControl))
                {
                    throw new InvalidDataException(
                        $"The audit-mutation snapshot contains an invalid string at {path}.");
                }

                if (SensitiveStringMarkers.Any(marker =>
                        value.Contains(marker, StringComparison.OrdinalIgnoreCase)))
                {
                    throw new InvalidDataException(
                        $"The audit-mutation snapshot contains raw XML, a device path, or a hardware identifier at {path}.");
                }

                break;

            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
            case JsonValueKind.Null:
                break;

            default:
                throw new InvalidDataException(
                    $"The audit-mutation snapshot contains an unsupported JSON value at {path}.");
        }
    }

    private static void RejectSensitiveProperty(string propertyName)
    {
        var normalized = new string(propertyName.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        var xmlMetadataOnly = normalized.EndsWith("xmlsha256", StringComparison.Ordinal) ||
                              normalized.EndsWith("xmlmatch", StringComparison.Ordinal);
        var reportLengthMetadataOnly =
            (normalized.Contains("hidreport", StringComparison.Ordinal) ||
             normalized.Contains("featurereport", StringComparison.Ordinal)) &&
            (normalized.EndsWith("length", StringComparison.Ordinal) ||
             normalized.EndsWith("bytelength", StringComparison.Ordinal));
        if ((normalized.Contains("xml", StringComparison.Ordinal) && !xmlMetadataOnly) ||
            normalized.Contains("serial", StringComparison.Ordinal) ||
            normalized.Contains("devicepath", StringComparison.Ordinal) ||
            normalized.Contains("instancepath", StringComparison.Ordinal) ||
            normalized.Contains("hardwareid", StringComparison.Ordinal) ||
            normalized.Contains("pnpid", StringComparison.Ordinal) ||
            ((normalized.Contains("hidreport", StringComparison.Ordinal) ||
              normalized.Contains("featurereport", StringComparison.Ordinal)) &&
             !reportLengthMetadataOnly) ||
            normalized.Contains("featurebuffer", StringComparison.Ordinal) ||
            normalized.Contains("rawframe", StringComparison.Ordinal) ||
            normalized.Contains("rawpayload", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"The audit-mutation snapshot property '{propertyName}' could expose XML, a serial number, a device path, or a raw HID payload.");
        }
    }
}
