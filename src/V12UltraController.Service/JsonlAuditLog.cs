using System.Text.Json;
using System.Text;

namespace V12UltraController.Service;

internal class JsonlAuditLog(ServicePaths paths, TimeProvider timeProvider)
{
    private const long DefaultMaxFileBytes = 10 * 1024 * 1024;
    private const int DefaultRetainedFiles = 5;
    private const long DefaultMaxTransactionSearchBytes = 64 * 1024 * 1024;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web);
    private readonly long maxFileBytes = DefaultMaxFileBytes;
    private readonly int retainedFiles = DefaultRetainedFiles;
    private readonly long maxTransactionSearchBytes = DefaultMaxTransactionSearchBytes;

    internal JsonlAuditLog(
        ServicePaths paths,
        TimeProvider timeProvider,
        long maxFileBytes,
        int retainedFiles = DefaultRetainedFiles,
        long maxTransactionSearchBytes = DefaultMaxTransactionSearchBytes)
        : this(paths, timeProvider)
    {
        if (maxFileBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxFileBytes));
        }

        if (retainedFiles <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(retainedFiles));
        }

        if (maxTransactionSearchBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxTransactionSearchBytes));
        }

        this.maxFileBytes = maxFileBytes;
        this.retainedFiles = retainedFiles;
        this.maxTransactionSearchBytes = maxTransactionSearchBytes;
    }

    public virtual async Task WriteAsync(
        string eventName,
        string result,
        string? callerSid = null,
        string? source = null,
        object? details = null,
        CancellationToken cancellationToken = default,
        Guid? transactionId = null)
    {
        if (transactionId == Guid.Empty)
        {
            throw new ArgumentException("An audit transactionId must not be empty.", nameof(transactionId));
        }

        paths.EnsureDirectories();
        var record = new
        {
            timestamp = timeProvider.GetUtcNow(),
            transactionId,
            eventName,
            result,
            callerSid,
            source,
            details,
        };

        var line = JsonSerializer.Serialize(record, jsonOptions) + Environment.NewLine;
        var encodedLine = Encoding.UTF8.GetBytes(line);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RotateIfNeeded(encodedLine.Length);
            await using var stream = new FileStream(
                paths.AuditLogPath,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
            await stream.WriteAsync(encodedLine, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }
        finally
        {
            gate.Release();
        }
    }

    public virtual async Task<bool> ContainsTransactionAsync(
        Guid transactionId,
        CancellationToken cancellationToken = default)
    {
        if (transactionId == Guid.Empty)
        {
            throw new ArgumentException("An audit transactionId must not be empty.", nameof(transactionId));
        }

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            long searchedBytes = 0;
            for (var index = 0; index <= retainedFiles; index++)
            {
                var path = index == 0 ? paths.AuditLogPath : $"{paths.AuditLogPath}.{index}";
                byte[] content;
                try
                {
                    var file = new FileInfo(path);
                    if (!file.Exists)
                    {
                        continue;
                    }

                    searchedBytes = checked(searchedBytes + file.Length);
                    if (searchedBytes > maxTransactionSearchBytes)
                    {
                        throw new InvalidDataException(
                            "The bounded audit transaction search budget was exceeded.");
                    }

                    content = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
                }
                catch (FileNotFoundException)
                {
                    // A pre-existing interrupted rotation may leave a numbered gap. Missing files
                    // do not prove absence, so continue across the complete bounded retention set.
                    continue;
                }
                catch (DirectoryNotFoundException)
                {
                    continue;
                }

                if (ContainsTransaction(content, transactionId, allowIncompleteTrailingLine: index == 0))
                {
                    return true;
                }
            }

            return false;
        }
        finally
        {
            gate.Release();
        }
    }

    private static bool ContainsTransaction(
        ReadOnlySpan<byte> content,
        Guid transactionId,
        bool allowIncompleteTrailingLine)
    {
        var offset = 0;
        while (offset < content.Length)
        {
            var remaining = content[offset..];
            var newline = remaining.IndexOf((byte)'\n');
            var completeLine = newline >= 0;
            var lineLength = completeLine ? newline : remaining.Length;
            var line = remaining[..lineLength];
            if (!line.IsEmpty && line[^1] == (byte)'\r')
            {
                line = line[..^1];
            }

            if (!line.IsEmpty)
            {
                try
                {
                    using var document = JsonDocument.Parse(line.ToArray());
                    if (RecordHasTransaction(document.RootElement, transactionId))
                    {
                        return true;
                    }
                }
                catch (JsonException) when (!completeLine && allowIncompleteTrailingLine)
                {
                    // A process crash can tear only the final append of the active file. The
                    // durable mutation journal remains authoritative, so ignore that one partial
                    // tail and let recovery append a complete idempotent record.
                    return false;
                }
                catch (JsonException exception)
                {
                    throw new InvalidDataException(
                        "A retained audit log contains an invalid complete JSONL record.", exception);
                }
            }

            if (!completeLine)
            {
                break;
            }

            offset += lineLength + 1;
        }

        return false;
    }

    private static bool RecordHasTransaction(JsonElement root, Guid expected)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("An audit JSONL record must be an object.");
        }

        JsonElement transaction = default;
        var matches = 0;
        foreach (var property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, "transactionId", StringComparison.Ordinal))
            {
                matches++;
                transaction = property.Value;
            }
            else if (string.Equals(property.Name, "transactionId", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "An audit JSONL transactionId property has non-canonical casing.");
            }
        }

        if (matches > 1)
        {
            throw new InvalidDataException("An audit JSONL record contains duplicate transactionId properties.");
        }

        if (matches == 0 || transaction.ValueKind == JsonValueKind.Null)
        {
            return false;
        }

        if (transaction.ValueKind != JsonValueKind.String || !transaction.TryGetGuid(out var actual))
        {
            throw new InvalidDataException("An audit JSONL transactionId is not a valid UUID.");
        }

        return actual == expected;
    }

    private void RotateIfNeeded(int incomingBytes)
    {
        var file = new FileInfo(paths.AuditLogPath);
        if (!file.Exists || file.Length + incomingBytes <= maxFileBytes)
        {
            return;
        }

        var oldest = $"{paths.AuditLogPath}.{retainedFiles}";
        if (File.Exists(oldest))
        {
            File.Delete(oldest);
        }

        for (var index = retainedFiles - 1; index >= 1; index--)
        {
            var source = $"{paths.AuditLogPath}.{index}";
            if (File.Exists(source))
            {
                File.Move(source, $"{paths.AuditLogPath}.{index + 1}", overwrite: true);
            }
        }

        File.Move(paths.AuditLogPath, $"{paths.AuditLogPath}.1", overwrite: true);
    }
}
