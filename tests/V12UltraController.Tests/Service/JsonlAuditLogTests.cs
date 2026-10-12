using System.Text;
using System.Text.Json;
using V12UltraController.Service;

namespace V12UltraController.Tests.Service;

public sealed class JsonlAuditLogTests
{
    [Fact]
    public async Task WriteAsync_PreservesFullCallerSidAndWritesBomlessUtf8()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"v12-audit-{Guid.NewGuid():N}");
        try
        {
            var paths = new ServicePaths(directory);
            var audit = new JsonlAuditLog(paths, new FixedTimeProvider());
            const string callerSid = "S-1-5-21-111111111-222222222-333333333-1001";

            await audit.WriteAsync(
                "测试事件",
                "success",
                callerSid,
                "settings",
                new { value = "风扇" });

            var bytes = await File.ReadAllBytesAsync(paths.AuditLogPath);
            var text = await File.ReadAllTextAsync(paths.AuditLogPath, Encoding.UTF8);
            Assert.Equal(Encoding.UTF8.GetByteCount(text), bytes.Length);
            Assert.False(bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble));
            using var document = JsonDocument.Parse(text);
            Assert.Equal(callerSid, document.RootElement.GetProperty("callerSid").GetString());
            Assert.Equal("测试事件", document.RootElement.GetProperty("eventName").GetString());
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Rotation_UsesExactEncodedUtf8ByteCount()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"v12-audit-rotation-{Guid.NewGuid():N}");
        var sampleDirectory = Path.Combine(Path.GetTempPath(), $"v12-audit-sample-{Guid.NewGuid():N}");
        try
        {
            var time = new FixedTimeProvider();
            var paths = new ServicePaths(directory);
            var firstWriter = new JsonlAuditLog(paths, time, long.MaxValue);
            await firstWriter.WriteAsync("first", "success", "S-1-5-18", "service", new { text = "一" });
            var firstBytes = new FileInfo(paths.AuditLogPath).Length;

            var samplePaths = new ServicePaths(sampleDirectory);
            var sample = new JsonlAuditLog(samplePaths, time, long.MaxValue);
            await sample.WriteAsync("second", "success", "S-1-5-18", "service", new { text = "温度风扇" });
            var secondBytes = new FileInfo(samplePaths.AuditLogPath).Length;

            var exactBoundaryWriter = new JsonlAuditLog(paths, time, firstBytes + secondBytes);
            await exactBoundaryWriter.WriteAsync(
                "second", "success", "S-1-5-18", "service", new { text = "温度风扇" });

            Assert.Equal(firstBytes + secondBytes, new FileInfo(paths.AuditLogPath).Length);
            Assert.False(File.Exists(paths.AuditLogPath + ".1"));

            await exactBoundaryWriter.WriteAsync("third", "success", "S-1-5-18", "service");
            Assert.True(File.Exists(paths.AuditLogPath + ".1"));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }

            if (Directory.Exists(sampleDirectory))
            {
                Directory.Delete(sampleDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task TransactionId_IsDurablyRecordedAndFoundInCurrentFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"v12-audit-transaction-{Guid.NewGuid():N}");
        try
        {
            var paths = new ServicePaths(directory);
            var audit = new JsonlAuditLog(paths, new FixedTimeProvider());
            var transactionId = Guid.NewGuid();

            await audit.WriteAsync(
                "mode.set",
                "success",
                "S-1-5-18",
                "service",
                new { previous = new { mode = "Auto" }, current = new { mode = "Manual" } },
                CancellationToken.None,
                transactionId);

            Assert.True(await audit.ContainsTransactionAsync(transactionId));
            Assert.False(await audit.ContainsTransactionAsync(Guid.NewGuid()));
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(paths.AuditLogPath));
            Assert.Equal(transactionId, document.RootElement.GetProperty("transactionId").GetGuid());
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ContainsTransaction_SearchesAcrossRotationAndResumesInterruptedDestinations()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"v12-audit-transaction-rotation-{Guid.NewGuid():N}");
        try
        {
            var paths = new ServicePaths(directory);
            var transactionId = Guid.NewGuid();
            var seed = new JsonlAuditLog(paths, new FixedTimeProvider(), long.MaxValue, retainedFiles: 2);
            await seed.WriteAsync(
                "mode.set", "success", "S-1-5-18", "service", new { value = "seed" },
                CancellationToken.None, transactionId);
            var firstLength = new FileInfo(paths.AuditLogPath).Length;

            // Model a prior interrupted rotation with both destinations already present. The
            // overwrite moves must converge rather than permanently blocking future appends.
            await File.WriteAllTextAsync(paths.AuditLogPath + ".1", "{\"transactionId\":null}\n");
            await File.WriteAllTextAsync(paths.AuditLogPath + ".2", "{\"transactionId\":null}\n");
            var rotating = new JsonlAuditLog(paths, new FixedTimeProvider(), firstLength, retainedFiles: 2);
            await rotating.WriteAsync("next", "success", "S-1-5-18", "service");

            Assert.True(await rotating.ContainsTransactionAsync(transactionId));
            Assert.True(File.Exists(paths.AuditLogPath));
            Assert.True(File.Exists(paths.AuditLogPath + ".1"));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ContainsTransaction_IgnoresOnlyAnIncompleteActiveTail()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"v12-audit-transaction-tail-{Guid.NewGuid():N}");
        try
        {
            var paths = new ServicePaths(directory);
            paths.EnsureDirectories();
            var transactionId = Guid.NewGuid();
            await File.WriteAllTextAsync(
                paths.AuditLogPath,
                $"{{\"transactionId\":\"{transactionId:D}\"}}\n{{\"transactionId\":");
            var audit = new JsonlAuditLog(paths, new FixedTimeProvider());

            Assert.True(await audit.ContainsTransactionAsync(transactionId));
            Assert.False(await audit.ContainsTransactionAsync(Guid.NewGuid()));

            await File.WriteAllTextAsync(paths.AuditLogPath + ".1", "{\"transactionId\":");
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                audit.ContainsTransactionAsync(Guid.NewGuid()));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ContainsTransaction_FailsClosedOnInvalidCompleteRecordOrSearchBudget()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"v12-audit-transaction-invalid-{Guid.NewGuid():N}");
        try
        {
            var paths = new ServicePaths(directory);
            paths.EnsureDirectories();
            await File.WriteAllTextAsync(paths.AuditLogPath, "{not-json}\n");
            var audit = new JsonlAuditLog(
                paths,
                new FixedTimeProvider(),
                maxFileBytes: 1024,
                retainedFiles: 1,
                maxTransactionSearchBytes: 8);

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                audit.ContainsTransactionAsync(Guid.NewGuid()));

            var roomy = new JsonlAuditLog(
                paths,
                new FixedTimeProvider(),
                1024,
                retainedFiles: 1,
                maxTransactionSearchBytes: 1024);
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                roomy.ContainsTransactionAsync(Guid.NewGuid()));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() =>
            new(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);
    }
}
