using System.Globalization;
using System.Text.Json;

namespace V12UltraController.Core.Security;

/// <summary>
/// A durable, non-secret indication that an explicit forced uninstall skipped
/// the verified Myth.Cool rollback. Its presence is a safety barrier, not proof
/// that rollback is necessary or that it has been completed.
/// </summary>
public sealed record ForcedUninstallRecoveryMarker(
    int SchemaVersion,
    Guid MarkerId,
    string Reason,
    DateTimeOffset CreatedAtUtc)
{
    public const int CurrentSchemaVersion = 1;
    public const string ForcedUninstallReason = "forcedUninstallSkippedVerifiedRollback";

    public static ForcedUninstallRecoveryMarker CreateNew() =>
        new(
            CurrentSchemaVersion,
            Guid.NewGuid(),
            ForcedUninstallReason,
            DateTimeOffset.UtcNow);

    internal void Validate()
    {
        if (SchemaVersion != CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Unsupported forced-uninstall marker schema {SchemaVersion}.");
        }

        if (MarkerId == Guid.Empty)
        {
            throw new InvalidDataException("The forced-uninstall marker id must not be empty.");
        }

        if (!string.Equals(Reason, ForcedUninstallReason, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The forced-uninstall marker reason is invalid.");
        }

        if (CreatedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new InvalidDataException("The forced-uninstall marker timestamp must be UTC.");
        }
    }
}

public interface IForcedUninstallRecoveryMarkerStore
{
    Task<ForcedUninstallRecoveryMarker?> LoadAsync(CancellationToken cancellationToken);

    Task<ForcedUninstallRecoveryMarker> CreateAsync(
        ForcedUninstallRecoveryMarker marker,
        CancellationToken cancellationToken);

    Task DeleteExactAsync(
        ForcedUninstallRecoveryMarker expected,
        CancellationToken cancellationToken);
}

/// <summary>
/// Owns the one fixed recovery-marker path. The production constructor accepts
/// no path; only the test assembly can inject an isolated path.
/// </summary>
public sealed class ForcedUninstallRecoveryMarkerStore : IForcedUninstallRecoveryMarkerStore, IDisposable
{
    public const string FixedPath =
        @"C:\ProgramData\TianqiStudio\V12UltraController\forced-uninstall-recovery.json";

    private const long MaximumDocumentBytes = 4096;
    private readonly string path;
    private readonly SemaphoreSlim gate = new(1, 1);
    private bool disposed;

    public ForcedUninstallRecoveryMarkerStore()
        : this(FixedPath)
    {
    }

    internal ForcedUninstallRecoveryMarkerStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        this.path = Path.GetFullPath(path);
    }

    public async Task<ForcedUninstallRecoveryMarker?> LoadAsync(
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await LoadWithoutLockAsync(path, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<ForcedUninstallRecoveryMarker> CreateAsync(
        ForcedUninstallRecoveryMarker marker,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(marker);
        marker.Validate();

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? temporaryPath = null;
        try
        {
            var existingBeforeCreate = await LoadWithoutLockAsync(path, cancellationToken)
                .ConfigureAwait(false);
            if (existingBeforeCreate is not null)
            {
                if (existingBeforeCreate == marker)
                {
                    return existingBeforeCreate;
                }

                throw new InvalidDataException(
                    "A different forced-uninstall recovery marker already exists; it was not overwritten.");
            }

            var directory = Path.GetDirectoryName(path)
                ?? throw new InvalidOperationException(
                    "The forced-uninstall recovery marker path has no parent directory.");
            Directory.CreateDirectory(directory);
            temporaryPath = Path.Combine(
                directory,
                $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");

            var document = Serialize(marker);
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             4096,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(document, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            try
            {
                File.Move(temporaryPath, path);
                temporaryPath = null;
            }
            catch (IOException)
            {
                // Another writer won the no-overwrite race. Only an exact,
                // already-durable copy makes this operation idempotent.
                var existing = await LoadWithoutLockAsync(path, cancellationToken)
                    .ConfigureAwait(false);
                if (existing is not null && existing == marker)
                {
                    return existing;
                }

                if (existing is null)
                {
                    throw;
                }

                throw new InvalidDataException(
                    "A different forced-uninstall recovery marker won the create race; it was not overwritten.");
            }

            var readBack = await LoadRequiredWithoutLockAsync(path, cancellationToken)
                .ConfigureAwait(false);
            if (readBack != marker)
            {
                throw new InvalidDataException(
                    "The forced-uninstall recovery marker did not match its durable readback.");
            }

            return readBack;
        }
        finally
        {
            if (temporaryPath is not null)
            {
                TryDeleteTemporaryFile(temporaryPath);
            }

            gate.Release();
        }
    }

    public async Task DeleteExactAsync(
        ForcedUninstallRecoveryMarker expected,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(expected);
        expected.Validate();

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var existing = await LoadRequiredWithoutLockAsync(path, cancellationToken)
                .ConfigureAwait(false);
            if (existing != expected)
            {
                throw new InvalidDataException(
                    "The forced-uninstall recovery marker changed; exact deletion was refused.");
            }

            // The protected ProgramData ACL and this process-local gate define
            // the writer boundary. Once the exact comparison succeeds, use a
            // single non-cancellable delete: renaming through a tombstone would
            // create a crash window where the fixed-path safety barrier vanished
            // even though deletion had not completed.
            cancellationToken.ThrowIfCancellationRequested();
            File.Delete(path);

            if (await LoadWithoutLockAsync(path, CancellationToken.None).ConfigureAwait(false)
                is not null)
            {
                throw new InvalidDataException(
                    "A forced-uninstall recovery marker is still present after exact deletion.");
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        gate.Dispose();
    }

    private static async Task<ForcedUninstallRecoveryMarker> LoadRequiredWithoutLockAsync(
        string markerPath,
        CancellationToken cancellationToken) =>
        await LoadWithoutLockAsync(markerPath, cancellationToken).ConfigureAwait(false)
        ?? throw new InvalidDataException("The forced-uninstall recovery marker is missing.");

    private static async Task<ForcedUninstallRecoveryMarker?> LoadWithoutLockAsync(
        string markerPath,
        CancellationToken cancellationToken)
    {
        FileStream stream;
        try
        {
            stream = new FileStream(
                markerPath,
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
                    "The forced-uninstall recovery marker has an invalid document length.");
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
                return Parse(document.RootElement);
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException(
                    "The forced-uninstall recovery marker is not valid strict JSON.",
                    exception);
            }
        }
    }

    private static ForcedUninstallRecoveryMarker Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException(
                "The forced-uninstall recovery marker must be a JSON object.");
        }

        JsonElement schemaVersion = default;
        JsonElement markerId = default;
        JsonElement reason = default;
        JsonElement createdAtUtc = default;
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var property in root.EnumerateObject())
        {
            if (!seen.Add(property.Name))
            {
                throw new InvalidDataException(
                    $"The forced-uninstall recovery marker property '{property.Name}' is duplicated.");
            }

            switch (property.Name)
            {
                case "schemaVersion":
                    schemaVersion = property.Value;
                    break;
                case "markerId":
                    markerId = property.Value;
                    break;
                case "reason":
                    reason = property.Value;
                    break;
                case "createdAtUtc":
                    createdAtUtc = property.Value;
                    break;
                default:
                    throw new InvalidDataException(
                        $"The forced-uninstall recovery marker contains unknown property '{property.Name}'.");
            }
        }

        if (seen.Count != 4 ||
            !seen.Contains("schemaVersion") ||
            !seen.Contains("markerId") ||
            !seen.Contains("reason") ||
            !seen.Contains("createdAtUtc"))
        {
            throw new InvalidDataException(
                "The forced-uninstall recovery marker does not have the exact required shape.");
        }

        if (schemaVersion.ValueKind != JsonValueKind.Number ||
            !schemaVersion.TryGetInt32(out var parsedSchemaVersion))
        {
            throw new InvalidDataException(
                "The forced-uninstall recovery marker schemaVersion must be an integer.");
        }

        var markerIdText = RequireString(markerId, "markerId");
        if (!Guid.TryParseExact(markerIdText, "D", out var parsedMarkerId) ||
            !string.Equals(markerIdText, parsedMarkerId.ToString("D"), StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The forced-uninstall recovery marker id must be a canonical lowercase GUID.");
        }

        var reasonText = RequireString(reason, "reason");
        var createdAtText = RequireString(createdAtUtc, "createdAtUtc");
        if (!DateTimeOffset.TryParseExact(
                createdAtText,
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsedCreatedAt) ||
            parsedCreatedAt.Offset != TimeSpan.Zero ||
            !string.Equals(
                createdAtText,
                parsedCreatedAt.ToString("O", CultureInfo.InvariantCulture),
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The forced-uninstall recovery marker createdAtUtc must be canonical UTC ISO-8601.");
        }

        var marker = new ForcedUninstallRecoveryMarker(
            parsedSchemaVersion,
            parsedMarkerId,
            reasonText,
            parsedCreatedAt);
        marker.Validate();
        return marker;
    }

    private static byte[] Serialize(ForcedUninstallRecoveryMarker marker)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(
                   buffer,
                   new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", marker.SchemaVersion);
            writer.WriteString("markerId", marker.MarkerId.ToString("D"));
            writer.WriteString("reason", marker.Reason);
            writer.WriteString(
                "createdAtUtc",
                marker.CreatedAtUtc.ToString("O", CultureInfo.InvariantCulture));
            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }

    private static string RequireString(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.String || element.GetString() is not { } value)
        {
            throw new InvalidDataException(
                $"The forced-uninstall recovery marker {propertyName} must be a string.");
        }

        return value;
    }

    private static void TryDeleteTemporaryFile(string temporaryPath)
    {
        try
        {
            File.Delete(temporaryPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The canonical marker was never overwritten. A uniquely named
            // temporary file is not interpreted as recovery state.
        }
    }
}
