using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using V12UltraController.Service;

namespace V12UltraController.Tests.Service;

public sealed class RollingJsonFileLoggerTests
{
    [Fact]
    public async Task Provider_WritesEveryRealLogLevelAndKeepsAuditFileSeparate()
    {
        using var directory = new TemporaryDirectory();
        var paths = new ServicePaths(directory.Path);
        using var provider = CreateProvider(paths);
        var logger = provider.CreateLogger("V12UltraController.Tests.Levels");

        foreach (var level in Enum.GetValues<LogLevel>())
        {
            logger.Log(level, new EventId((int)level, level.ToString()), level.ToString(), null,
                static (state, _) => state);
        }

        await DrainAsync(provider);
        var entries = ReadEntries(paths.ServiceLogPath);
        Assert.Equal(6, entries.Count);
        Assert.Equal(
            ["Trace", "Debug", "Information", "Warning", "Error", "Critical"],
            entries.Select(entry => entry.GetProperty("level").GetString()!).ToArray());
        Assert.NotEqual(paths.AuditLogPath, paths.ServiceLogPath);
        Assert.False(File.Exists(paths.AuditLogPath));
    }

    [Fact]
    public async Task Provider_RotatesBySizeAndBoundsRetainedFiles()
    {
        using var directory = new TemporaryDirectory();
        var paths = new ServicePaths(directory.Path);
        const long maxFileBytes = 700;
        using var provider = CreateProvider(paths, new RollingJsonFileLoggerOptions
        {
            MaxFileBytes = maxFileBytes,
            RetainedFiles = 2,
        });
        var logger = provider.CreateLogger("V12UltraController.Tests.Rotation");

        for (var index = 0; index < 30; index++)
        {
            logger.LogInformation("Rotation record {Index}: {Payload}", index, new string('x', 120));
        }

        await DrainAsync(provider);
        string[] expectedFiles =
        [
            paths.ServiceLogPath,
            $"{paths.ServiceLogPath}.1",
            $"{paths.ServiceLogPath}.2",
        ];
        Assert.All(expectedFiles, path => Assert.True(File.Exists(path), path));
        Assert.False(File.Exists($"{paths.ServiceLogPath}.3"));
        foreach (var file in expectedFiles)
        {
            Assert.InRange(new FileInfo(file).Length, 1, maxFileBytes);
            Assert.NotEmpty(ReadEntries(file));
        }
    }

    [Fact]
    public async Task Provider_SerializesConcurrentWritersWithoutLostOrCorruptRecords()
    {
        using var directory = new TemporaryDirectory();
        var paths = new ServicePaths(directory.Path);
        using var provider = CreateProvider(paths, new RollingJsonFileLoggerOptions
        {
            MaxFileBytes = 10 * 1024 * 1024,
            RetainedFiles = 2,
        });
        var logger = provider.CreateLogger("V12UltraController.Tests.Concurrency");

        Parallel.For(0, 256, index =>
            logger.LogInformation("Concurrent record {Sequence}", index));

        await DrainAsync(provider);
        var entries = ReadEntries(paths.ServiceLogPath);
        Assert.Equal(256, entries.Count);
        Assert.Equal(
            Enumerable.Range(0, 256),
            entries.Select(entry => entry.GetProperty("properties").GetProperty("Sequence").GetInt32())
                .Order());
    }

    [Fact]
    public async Task Provider_EmitsBoundedStructuredJsonWithoutExceptionStackOrSensitivePaths()
    {
        using var directory = new TemporaryDirectory();
        var paths = new ServicePaths(directory.Path);
        using var provider = CreateProvider(paths, new RollingJsonFileLoggerOptions
        {
            MaxFileBytes = 1024 * 1024,
            RetainedFiles = 2,
            MaxMessageCharacters = 96,
            MaxPropertyCharacters = 48,
            MaxCategoryCharacters = 64,
        });
        var logger = provider.CreateLogger(new string('c', 100));
        const string devicePath = @"\\?\hid#vid_374a&pid_b101#SERIAL-SECRET#{deadbeef}";
        using var scope = logger.BeginScope("scope\r\n{DevicePath}", devicePath);
        var exception = new InvalidOperationException(
            $"native failure\0\r\nfor {devicePath}; {new string('e', 200)}");

        logger.LogError(
            exception,
            "Failed {DevicePath} with {SerialNumber}; attempt {Attempt}",
            devicePath,
            "SERIAL-SECRET",
            7);

        await DrainAsync(provider);
        var raw = File.ReadAllText(paths.ServiceLogPath);
        Assert.DoesNotContain(devicePath, raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SERIAL-SECRET", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("stackTrace", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(" at System.", raw, StringComparison.Ordinal);
        Assert.DoesNotContain('\r', raw);
        Assert.Equal(1, raw.Count(character => character == '\n'));

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement;
        Assert.Equal("Error", root.GetProperty("level").GetString());
        Assert.Equal(64, root.GetProperty("category").GetString()!.Length);
        Assert.InRange(root.GetProperty("message").GetString()!.Length, 1, 96);
        Assert.Equal("[REDACTED]",
            root.GetProperty("properties").GetProperty("DevicePath").GetString());
        Assert.Equal("[REDACTED]",
            root.GetProperty("properties").GetProperty("SerialNumber").GetString());
        Assert.Equal(7, root.GetProperty("properties").GetProperty("Attempt").GetInt32());
        var loggedException = root.GetProperty("exception");
        Assert.Equal(typeof(InvalidOperationException).FullName,
            loggedException.GetProperty("type").GetString());
        var exceptionMessage = loggedException.GetProperty("message").GetString()!;
        Assert.InRange(exceptionMessage.Length, 1, 96);
        Assert.DoesNotContain('\0', exceptionMessage);
        Assert.DoesNotContain('\r', exceptionMessage);
        Assert.DoesNotContain('\n', exceptionMessage);
        Assert.Contains("[REDACTED_DEVICE_PATH]", exceptionMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Registration_AddsProviderAtTraceLevelAndProgramRetainsWindowsServiceLogging()
    {
        using var directory = new TemporaryDirectory();
        var paths = new ServicePaths(directory.Path);
        var services = new ServiceCollection();
        services.AddSingleton(paths);
        services.AddSingleton(TimeProvider.System);
        services.AddLogging(logging => logging.AddRollingJsonFile());

        using (var serviceProvider = services.BuildServiceProvider())
        {
            Assert.Contains(serviceProvider.GetServices<ILoggerProvider>(),
                provider => provider is RollingJsonFileLoggerProvider);
            var logger = serviceProvider.GetRequiredService<ILoggerFactory>()
                .CreateLogger("V12UltraController.Tests.Registration");
            logger.LogTrace("Trace registration proof");
            await DrainAsync(serviceProvider.GetServices<ILoggerProvider>()
                .OfType<RollingJsonFileLoggerProvider>().Single());
        }

        Assert.Single(ReadEntries(paths.ServiceLogPath));
        var program = File.ReadAllText(ProjectPath("src", "V12UltraController.Service", "Program.cs"));
        Assert.Contains("builder.Services.AddWindowsService", program, StringComparison.Ordinal);
        Assert.Contains("builder.Logging.AddRollingJsonFile", program, StringComparison.Ordinal);
        Assert.DoesNotContain("ClearProviders", program, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Provider_BlockedDiskDoesNotBlockLogCallsOrBoundedDispose()
    {
        using var sink = new BlockingSink();
        using var provider = CreateProvider(sink, new RollingJsonFileLoggerOptions
        {
            QueueCapacity = 2,
            ShutdownDrainTimeout = TimeSpan.FromMilliseconds(50),
        });
        var logger = provider.CreateLogger("BlockedDisk");
        try
        {
            var firstCall = Task.Run(() => logger.LogError("Original control failure"));
            await sink.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await firstCall.WaitAsync(TimeSpan.FromSeconds(2));

            // Mirrors the watchdog's log call while the first writer is stuck on disk.
            await Task.Run(() => logger.LogCritical("Watchdog must remain able to proceed"))
                .WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(await provider.DrainAsync(TimeSpan.FromMilliseconds(50)));
            await Task.Run(provider.Dispose).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(provider.WriterCompletion.IsCompleted);
            Assert.True(sink.UsedBackgroundThread);
        }
        finally
        {
            sink.Release();
            provider.Dispose();
            await provider.WriterCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.Equal(2, sink.Records.Count);
        Assert.Equal(0, provider.FailedWriteCount);
    }

    [Fact]
    public async Task Provider_FullQueueDropsNewestRecordsAndReportsExactCountAfterRecovery()
    {
        using var sink = new BlockingSink();
        using var provider = CreateProvider(sink, new RollingJsonFileLoggerOptions { QueueCapacity = 2 });
        var logger = provider.CreateLogger("Overflow");
        try
        {
            var firstCall = Task.Run(() => logger.LogInformation("In flight"));
            await sink.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await firstCall.WaitAsync(TimeSpan.FromSeconds(2));
            await Task.Run(() =>
            {
                for (var index = 0; index < 10; index++) logger.LogWarning("Queued {Index}", index);
            }).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(8, provider.DroppedRecordCount);
            Assert.Equal(0, provider.FailedWriteCount);
        }
        finally
        {
            sink.Release();
            provider.Dispose();
            await provider.WriterCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        }

        var entries = sink.Records.Select(bytes => ParseEntry(System.Text.Encoding.UTF8.GetString(bytes))).ToArray();
        Assert.Equal(4, entries.Length); // One in flight, two retained, one loss summary.
        var summary = Assert.Single(entries, entry =>
            entry.GetProperty("eventId").GetProperty("name").GetString() == "RecordsDropped");
        Assert.Contains("8 total", summary.GetProperty("message").GetString()!, StringComparison.Ordinal);
        Assert.Equal([0, 1], entries.Where(entry => entry.GetProperty("category").GetString() == "Overflow" &&
            entry.GetProperty("properties").ValueKind == JsonValueKind.Object)
            .Select(entry => entry.GetProperty("properties").GetProperty("Index").GetInt32()).ToArray());
    }

    [Fact]
    public async Task Provider_SinkFailureIsCountedAndLaterRecordsContinue()
    {
        var sink = new FailFirstSink();
        using var provider = CreateProvider(sink);
        var logger = provider.CreateLogger("RecoveringDisk");
        logger.LogWarning("Write that fails");
        logger.LogInformation("Write after recovery");

        Assert.True(await provider.DrainAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, provider.FailedWriteCount);
        Assert.Equal(0, provider.DroppedRecordCount);
        var entry = ParseEntry(System.Text.Encoding.UTF8.GetString(Assert.Single(sink.Records)));
        Assert.Equal("Write after recovery", entry.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Provider_SnapshotsMutableStateAndScopesBeforeReturningToCaller()
    {
        using var sink = new BlockingSink();
        using var provider = CreateProvider(sink);
        var logger = provider.CreateLogger("Snapshot");
        try
        {
            var firstCall = Task.Run(() => logger.LogInformation("In flight"));
            await sink.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await firstCall.WaitAsync(TimeSpan.FromSeconds(2));
            var state = new Dictionary<string, object?> { ["Value"] = "original" };
            using (logger.BeginScope("original scope"))
                logger.Log(LogLevel.Information, new EventId(2), state, null,
                    static (values, _) => values["Value"]!.ToString()!);
            state["Value"] = "changed after log returned";
        }
        finally
        {
            sink.Release();
            provider.Dispose();
            await provider.WriterCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        }

        var entry = ParseEntry(System.Text.Encoding.UTF8.GetString(sink.Records.Last()));
        Assert.Equal("original", entry.GetProperty("message").GetString());
        Assert.Equal("original", entry.GetProperty("properties").GetProperty("Value").GetString());
        Assert.Equal("original scope", Assert.Single(entry.GetProperty("scopes").EnumerateArray()).GetString());
    }

    [Fact]
    public async Task Provider_DroppedSummaryFormatterFailureDoesNotKillWriter()
    {
        using var sink = new BlockingSink();
        using var provider = new RollingJsonFileLoggerProvider(new ServicePaths(Path.GetTempPath()),
            new FailingWriterClock(), new RollingJsonFileLoggerOptions { QueueCapacity = 1 }, sink);
        var logger = provider.CreateLogger("SummaryFailure");
        try
        {
            var firstCall = Task.Run(() => logger.LogWarning("In flight"));
            await sink.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await firstCall.WaitAsync(TimeSpan.FromSeconds(2));
            logger.LogWarning("Retained");
            logger.LogWarning("Dropped");
            Assert.Equal(1, provider.DroppedRecordCount);
        }
        finally
        {
            sink.Release();
            provider.Dispose();
            await provider.WriterCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.Equal(2, sink.Records.Count);
        Assert.True(provider.FailedWriteCount > 0);
    }

    private static async Task DrainAsync(RollingJsonFileLoggerProvider provider)
    {
        Assert.True(await provider.DrainAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.Equal(0, provider.FailedWriteCount);
        Assert.Equal(0, provider.DroppedRecordCount);
    }

    private static RollingJsonFileLoggerProvider CreateProvider(
        IRollingJsonFileSink sink,
        RollingJsonFileLoggerOptions? options = null) =>
        new(new ServicePaths(Path.GetTempPath()), new FixedTimeProvider(),
            options ?? new RollingJsonFileLoggerOptions(), sink);

    private static RollingJsonFileLoggerProvider CreateProvider(
        ServicePaths paths,
        RollingJsonFileLoggerOptions? options = null) =>
        new(paths, new FixedTimeProvider(), options ?? new RollingJsonFileLoggerOptions());

    private static List<JsonElement> ReadEntries(string path) =>
        File.ReadLines(path)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(ParseEntry)
            .ToList();

    private static JsonElement ParseEntry(string line)
    {
        using var document = JsonDocument.Parse(line);
        return document.RootElement.Clone();
    }

    private static string ProjectPath(params string[] segments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "V12UltraController.slnx")))
        {
            directory = directory.Parent;
        }

        if (directory is null)
        {
            throw new DirectoryNotFoundException("Could not locate the V12UltraController project root.");
        }

        return segments.Aggregate(directory.FullName, Path.Combine);
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() =>
            new(2026, 9, 5, 0, 0, 0, TimeSpan.Zero);
    }

    private sealed class FailingWriterClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Thread.CurrentThread.Name == "V12UltraController.ServiceLog"
            ? throw new InvalidOperationException("Injected summary clock failure.")
            : DateTimeOffset.UtcNow;
    }

    private sealed class BlockingSink : IRollingJsonFileSink, IDisposable
    {
        private readonly ManualResetEventSlim released = new();
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentQueue<byte[]> Records { get; } = new();
        public bool UsedBackgroundThread { get; private set; }

        public void Write(byte[] bytes)
        {
            UsedBackgroundThread = Thread.CurrentThread.IsBackground &&
                Thread.CurrentThread.Name == "V12UltraController.ServiceLog";
            Entered.TrySetResult();
            released.Wait();
            Records.Enqueue(bytes);
        }

        public void Release() => released.Set();
        public void Dispose() => released.Dispose();
    }

    private sealed class FailFirstSink : IRollingJsonFileSink
    {
        private int calls;
        public ConcurrentQueue<byte[]> Records { get; } = new();
        public void Write(byte[] bytes)
        {
            if (Interlocked.Increment(ref calls) == 1)
                throw new InvalidOperationException("Injected sink failure.");
            Records.Enqueue(bytes);
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"V12UltraController.LoggerTests.{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
