using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace V12UltraController.Service;

internal sealed record RollingJsonFileLoggerOptions
{
    public long MaxFileBytes { get; init; } = 10 * 1024 * 1024;

    public int RetainedFiles { get; init; } = 5;

    public int MaxMessageCharacters { get; init; } = 4096;

    public int MaxPropertyCharacters { get; init; } = 1024;

    public int MaxCategoryCharacters { get; init; } = 256;

    public int QueueCapacity { get; init; } = 1024;

    public TimeSpan ShutdownDrainTimeout { get; init; } = TimeSpan.FromMilliseconds(250);

    public void Validate()
    {
        if (MaxFileBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxFileBytes));
        }

        if (RetainedFiles <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(RetainedFiles));
        }

        if (MaxMessageCharacters <= 0 || MaxPropertyCharacters <= 0 || MaxCategoryCharacters <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxMessageCharacters),
                "Log field limits must all be positive.");
        }

        if (QueueCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(QueueCapacity));
        }

        if (ShutdownDrainTimeout <= TimeSpan.Zero || ShutdownDrainTimeout > TimeSpan.FromSeconds(5))
        {
            throw new ArgumentOutOfRangeException(nameof(ShutdownDrainTimeout));
        }
    }
}

internal static class RollingJsonFileLoggingExtensions
{
    public static ILoggingBuilder AddRollingJsonFile(this ILoggingBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<ILoggerProvider, RollingJsonFileLoggerProvider>());
        builder.AddFilter<RollingJsonFileLoggerProvider>(
            static (_, level) => level is not LogLevel.None);
        return builder;
    }
}

internal sealed class RollingJsonFileLoggerProvider : ILoggerProvider, ISupportExternalScope
{
    private readonly ConcurrentDictionary<string, RollingJsonFileLogger> loggers =
        new(StringComparer.Ordinal);
    private readonly IRollingJsonFileSink sink;
    private readonly RollingJsonLogFormatter formatter;
    private readonly RollingJsonFileLoggerOptions options;
    private readonly BlockingCollection<QueuedLog> queue;
    private readonly Thread writer;
    private readonly TaskCompletionSource writerStopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IExternalScopeProvider scopeProvider = new LoggerExternalScopeProvider();
    private int disposed;
    private long droppedRecordCount;
    private long failedWriteCount;
    private long reportedDroppedRecordCount;

    public RollingJsonFileLoggerProvider(ServicePaths paths, TimeProvider timeProvider)
        : this(paths, timeProvider, new RollingJsonFileLoggerOptions())
    {
    }

    internal RollingJsonFileLoggerProvider(
        ServicePaths paths,
        TimeProvider timeProvider,
        RollingJsonFileLoggerOptions options)
        : this(paths, timeProvider, options, null)
    {
    }

    internal RollingJsonFileLoggerProvider(
        ServicePaths paths,
        TimeProvider timeProvider,
        RollingJsonFileLoggerOptions options,
        IRollingJsonFileSink? sink)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        this.options = options;
        this.sink = sink ?? new RollingJsonFileSink(paths, options);
        formatter = new RollingJsonLogFormatter(timeProvider, options);
        queue = new BlockingCollection<QueuedLog>(options.QueueCapacity);
        writer = new Thread(WriteQueuedRecords)
        {
            IsBackground = true,
            Name = "V12UltraController.ServiceLog",
        };
        writer.Start();
    }

    internal long DroppedRecordCount => Interlocked.Read(ref droppedRecordCount);

    internal long FailedWriteCount => Interlocked.Read(ref failedWriteCount);

    internal Task WriterCompletion => writerStopped.Task;

    public ILogger CreateLogger(string categoryName)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(categoryName);
        return loggers.GetOrAdd(categoryName, static (name, provider) =>
            new RollingJsonFileLogger(name, provider), this);
    }

    public void SetScopeProvider(IExternalScopeProvider scopeProvider)
    {
        ArgumentNullException.ThrowIfNull(scopeProvider);
        this.scopeProvider = scopeProvider;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        loggers.Clear();
        queue.CompleteAdding();
        if (Thread.CurrentThread != writer)
        {
            // A filesystem call cannot be cancelled reliably. Leave its dedicated thread
            // background-only; shutdown and the safety path must never wait for the disk.
            _ = writer.Join(options.ShutdownDrainTimeout);
        }
    }

    /// <summary>
    /// Bounded diagnostic/test barrier for previously accepted records. True means each
    /// prior write was attempted, not that a failing disk persisted it; inspect FailedWriteCount.
    /// This is never called by the control loop, watchdog, or fatal-exit path.
    /// </summary>
    internal async Task<bool> DrainAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!TryEnqueue(new QueuedLog(null, completion)))
        {
            return false;
        }

        try
        {
            return await completion.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    internal IDisposable? PushScope<TState>(TState state) where TState : notnull =>
        scopeProvider.Push(state);

    internal void Write<TState>(
        string category,
        LogLevel level,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (Volatile.Read(ref disposed) != 0 || level is LogLevel.None)
        {
            return;
        }

        try
        {
            // Snapshot scopes and mutable state on the calling thread. Only sanitized,
            // owned bytes enter the bounded queue; no filesystem call occurs here.
            var bytes = this.formatter.Format(category, level, eventId, state, exception, formatter, scopeProvider);
            if (!TryEnqueue(new QueuedLog(bytes, null)))
            {
                Interlocked.Increment(ref droppedRecordCount);
            }
        }
        catch (Exception writeException) when (writeException is not OutOfMemoryException)
        {
            // Invalid formatting must not terminate the safety service either.
            Interlocked.Increment(ref droppedRecordCount);
        }
    }

    private bool TryEnqueue(QueuedLog item)
    {
        if (Volatile.Read(ref disposed) != 0)
        {
            return false;
        }

        try { return queue.TryAdd(item); }
        catch (InvalidOperationException) { return false; } // Completion raced with this producer.
    }

    private void WriteQueuedRecords()
    {
        try
        {
            foreach (var item in queue.GetConsumingEnumerable())
            {
                ReportDroppedRecords();
                if (item.Bytes is { } bytes)
                {
                    _ = TryWrite(bytes);
                }
                else
                {
                    item.Completion!.TrySetResult(true);
                }
            }

            ReportDroppedRecords();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Ordinary diagnostics must not bring down the service through an unhandled
            // background-thread exception. Close this provider and resolve queued barriers.
            Interlocked.Increment(ref failedWriteCount);
            Interlocked.Exchange(ref disposed, 1);
            queue.CompleteAdding();
            while (queue.TryTake(out var abandoned))
            {
                if (abandoned.Bytes is not null) Interlocked.Increment(ref droppedRecordCount);
                else abandoned.Completion!.TrySetResult(false);
            }
        }
        finally
        {
            // Do not dispose queue synchronization objects while a producer might still be
            // returning from TryAdd. They are reclaimed with the provider after this thread exits.
            writerStopped.TrySetResult();
        }
    }

    private bool TryWrite(byte[] bytes)
    {
        try
        {
            sink.Write(bytes);
            return true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Interlocked.Increment(ref failedWriteCount);
            return false;
        }
    }

    private void ReportDroppedRecords()
    {
        var count = DroppedRecordCount;
        if (count == reportedDroppedRecordCount)
        {
            return;
        }

        try
        {
            var summary = formatter.Format(
                "V12UltraController.ServiceLog", LogLevel.Warning, new EventId(1, "RecordsDropped"),
                count, null,
                static (value, _) => $"Ordinary service log records dropped: {value} total; the bounded queue or formatting rejected records.",
                new LoggerExternalScopeProvider());
            if (TryWrite(summary))
            {
                reportedDroppedRecordCount = count;
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Even a failing clock/formatter in this internal message remains advisory.
            Interlocked.Increment(ref failedWriteCount);
        }
    }

    private sealed record QueuedLog(byte[]? Bytes, TaskCompletionSource<bool>? Completion);
}

internal sealed class RollingJsonFileLogger(
    string category,
    RollingJsonFileLoggerProvider provider) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull =>
        provider.PushScope(state);

    public bool IsEnabled(LogLevel logLevel) => logLevel is not LogLevel.None;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);
        if (IsEnabled(logLevel))
        {
            provider.Write(category, logLevel, eventId, state, exception, formatter);
        }
    }
}

internal sealed class RollingJsonLogFormatter
{
    private const string Redacted = "[REDACTED]";
    private const string RedactedDevicePath = "[REDACTED_DEVICE_PATH]";
    private static readonly string[] SensitiveDeviceMarkers =
    [
        @"\\?\",
        @"\\.\",
        @"HID\VID_",
        "HID#VID_",
        @"USB\VID_",
        "USB#VID_",
        "SERIAL=",
        "SERIAL:",
        "SERIAL-",
    ];

    private readonly JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web);
    private readonly RollingJsonFileLoggerOptions options;
    private readonly TimeProvider timeProvider;

    public RollingJsonLogFormatter(
        TimeProvider timeProvider,
        RollingJsonFileLoggerOptions options)
    {
        this.timeProvider = timeProvider;
        this.options = options;
    }

    public byte[] Format<TState>(
        string category,
        LogLevel level,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter,
        IExternalScopeProvider scopeProvider)
    {
        var (properties, messageTemplate, sensitiveValues) = CaptureProperties(state);
        var entry = new ServiceLogEntry(
            timeProvider.GetUtcNow(),
            level.ToString(),
            Sanitize(category, options.MaxCategoryCharacters),
            new ServiceLogEventId(eventId.Id,
                SanitizeNullable(eventId.Name, options.MaxPropertyCharacters)),
            SafeFormat(state, formatter, sensitiveValues),
            messageTemplate,
            properties,
            CaptureScopes(scopeProvider),
            CaptureException(exception));
        var line = JsonSerializer.Serialize(entry, jsonOptions) + "\n";
        return Encoding.UTF8.GetBytes(line);
    }

    private string SafeFormat<TState>(
        TState state,
        Func<TState, Exception?, string> formatter,
        IReadOnlyList<string>? sensitiveValues)
    {
        if (state is Exception stateException)
        {
            return Sanitize(ReadExceptionMessage(stateException), options.MaxMessageCharacters);
        }

        try
        {
            // The exception is deliberately excluded from the formatter. Its type and bounded,
            // sanitized message are recorded separately; no stack trace enters the JSONL file.
            var message = formatter(state, null);
            if (sensitiveValues is not null)
            {
                foreach (var sensitiveValue in sensitiveValues)
                {
                    message = message.Replace(sensitiveValue, Redacted, StringComparison.Ordinal);
                }
            }

            return Sanitize(message, options.MaxMessageCharacters);
        }
        catch (Exception formatException) when (formatException is not OutOfMemoryException)
        {
            return Sanitize(
                $"[message formatter failed: {formatException.GetType().Name}]",
                options.MaxMessageCharacters);
        }
    }

    private (Dictionary<string, object?>? Properties, string? MessageTemplate,
        IReadOnlyList<string>? SensitiveValues) CaptureProperties<TState>(
        TState state)
    {
        if (state is not IEnumerable<KeyValuePair<string, object?>> values)
        {
            return (null, null, null);
        }

        Dictionary<string, object?>? properties = null;
        List<string>? sensitiveValues = null;
        string? messageTemplate = null;
        try
        {
            foreach (var pair in values)
            {
                if (string.Equals(pair.Key, "{OriginalFormat}", StringComparison.Ordinal))
                {
                    messageTemplate = SanitizeNullable(
                        pair.Value as string,
                        options.MaxMessageCharacters);
                    continue;
                }

                var key = Sanitize(pair.Key, options.MaxPropertyCharacters);
                properties ??= new Dictionary<string, object?>(StringComparer.Ordinal);
                if (IsSensitiveKey(key))
                {
                    properties[key] = Redacted;
                    var rawSensitiveValue = pair.Value as string;
                    if (!string.IsNullOrEmpty(rawSensitiveValue))
                    {
                        sensitiveValues ??= [];
                        sensitiveValues.Add(rawSensitiveValue);
                    }
                }
                else
                {
                    properties[key] = SanitizeValue(pair.Value);
                }
            }
        }
        catch (Exception enumerationException) when (enumerationException is not OutOfMemoryException)
        {
            properties ??= new Dictionary<string, object?>(StringComparer.Ordinal);
            properties["stateCaptureError"] = enumerationException.GetType().Name;
        }

        return (properties, messageTemplate, sensitiveValues);
    }

    private IReadOnlyList<string>? CaptureScopes(IExternalScopeProvider scopeProvider)
    {
        List<string>? scopes = null;
        try
        {
            scopeProvider.ForEachScope((scope, _) =>
            {
                scopes ??= [];
                scopes.Add(SanitizeValue(scope)?.ToString() ?? string.Empty);
            }, state: 0);
        }
        catch (Exception enumerationException) when (enumerationException is not OutOfMemoryException)
        {
            scopes ??= [];
            scopes.Add($"[scope capture failed: {enumerationException.GetType().Name}]");
        }

        return scopes;
    }

    private ServiceLogException? CaptureException(Exception? exception)
    {
        if (exception is null)
        {
            return null;
        }

        return new ServiceLogException(
            Sanitize(exception.GetType().FullName ?? exception.GetType().Name,
                options.MaxCategoryCharacters),
            Sanitize(ReadExceptionMessage(exception), options.MaxMessageCharacters));
    }

    private object? SanitizeValue(object? value) => value switch
    {
        null => null,
        string text => Sanitize(text, options.MaxPropertyCharacters),
        char character => Sanitize(character.ToString(), options.MaxPropertyCharacters),
        bool or byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal => value,
        DateTime or DateTimeOffset or Guid => value,
        TimeSpan timeSpan => timeSpan.ToString("c", CultureInfo.InvariantCulture),
        Enum enumValue => Sanitize(enumValue.ToString(), options.MaxPropertyCharacters),
        Exception exception => new ServiceLogException(
            Sanitize(exception.GetType().FullName ?? exception.GetType().Name,
                options.MaxCategoryCharacters),
            Sanitize(ReadExceptionMessage(exception), options.MaxPropertyCharacters)),
        byte[] => "[binary omitted]",
        _ => Sanitize(SafeToString(value), options.MaxPropertyCharacters),
    };

    private static string SafeToString(object value)
    {
        try
        {
            return value.ToString() ?? string.Empty;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return $"[value formatting failed: {exception.GetType().Name}]";
        }
    }

    private static string ReadExceptionMessage(Exception exception)
    {
        try
        {
            return exception.Message;
        }
        catch (Exception messageException) when (messageException is not OutOfMemoryException)
        {
            return $"[exception message unavailable: {messageException.GetType().Name}]";
        }
    }

    private static bool IsSensitiveKey(string key)
    {
        var normalized = key.Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal);
        return normalized.Contains("path", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("serial", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("hardwareid", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("deviceinstance", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("pnpid", StringComparison.OrdinalIgnoreCase);
    }

    private static string? SanitizeNullable(string? value, int maxCharacters) =>
        value is null ? null : Sanitize(value, maxCharacters);

    private static string Sanitize(string? value, int maxCharacters)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var normalized = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            normalized.Append(char.IsControl(character) ? ' ' : character);
        }

        var redacted = RedactDevicePaths(normalized.ToString());
        return redacted.Length <= maxCharacters
            ? redacted
            : string.Concat(redacted.AsSpan(0, maxCharacters - 1), "…");
    }

    private static string RedactDevicePaths(string value)
    {
        foreach (var marker in SensitiveDeviceMarkers)
        {
            var searchStart = 0;
            while (searchStart < value.Length)
            {
                var markerIndex = value.IndexOf(marker, searchStart, StringComparison.OrdinalIgnoreCase);
                if (markerIndex < 0)
                {
                    break;
                }

                var end = markerIndex + marker.Length;
                while (end < value.Length && !IsSensitiveTokenTerminator(value[end]))
                {
                    end++;
                }

                value = string.Concat(value.AsSpan(0, markerIndex), RedactedDevicePath,
                    value.AsSpan(end));
                searchStart = markerIndex + RedactedDevicePath.Length;
            }
        }

        return value;
    }

    private static bool IsSensitiveTokenTerminator(char character) =>
        char.IsWhiteSpace(character) || character is '"' or '\'' or ',' or ';' or ']' or ')' or '}';

    private sealed record ServiceLogEntry(
        DateTimeOffset Timestamp,
        string Level,
        string Category,
        ServiceLogEventId EventId,
        string Message,
        string? MessageTemplate,
        IReadOnlyDictionary<string, object?>? Properties,
        IReadOnlyList<string>? Scopes,
        ServiceLogException? Exception);

    private sealed record ServiceLogEventId(int Id, string? Name);

    private sealed record ServiceLogException(string Type, string Message);
}

internal interface IRollingJsonFileSink
{
    void Write(byte[] bytes);
}

/// <summary>Accessed only by the provider's dedicated background writer.</summary>
internal sealed class RollingJsonFileSink(ServicePaths paths, RollingJsonFileLoggerOptions options) : IRollingJsonFileSink
{
    public void Write(byte[] bytes)
    {
        paths.EnsureDirectories();
        RotateIfNeeded(bytes.Length);
        using var stream = new FileStream(
            paths.ServiceLogPath,
            FileMode.Append,
            FileAccess.Write,
            FileShare.Read,
            4096,
            FileOptions.WriteThrough);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private void RotateIfNeeded(int incomingBytes)
    {
        var current = new FileInfo(paths.ServiceLogPath);
        if (!current.Exists || current.Length == 0 || current.Length + incomingBytes <= options.MaxFileBytes)
        {
            return;
        }

        var oldest = $"{paths.ServiceLogPath}.{options.RetainedFiles}";
        if (File.Exists(oldest))
        {
            File.Delete(oldest);
        }

        for (var index = options.RetainedFiles - 1; index >= 1; index--)
        {
            var source = $"{paths.ServiceLogPath}.{index}";
            if (File.Exists(source))
            {
                File.Move(source, $"{paths.ServiceLogPath}.{index + 1}");
            }
        }

        File.Move(paths.ServiceLogPath, $"{paths.ServiceLogPath}.1");
    }

}
