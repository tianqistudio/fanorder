using System.IO.Compression;
using System.Text;
using System.Text.Json;
using V12UltraController.Core.Diagnostics;

namespace V12UltraController.Tests.Diagnostics;

public sealed class DiagnosticsArchiveWriterTests
{
    [Fact]
    public async Task WriteAsync_CreatesOneUtf8JsonEntryAndLeavesNoTemporaryFile()
    {
        using var directory = new TemporaryDirectory();
        var destination = Path.Combine(directory.Path, "diagnostics.zip");
        var diagnostics = JsonSerializer.SerializeToElement(new
        {
            serviceState = "FailSafe",
            note = "风扇安全 ✓",
            device = new { vendorId = "374A", productId = "B101", firmware = "1602" },
        });

        await new DiagnosticsArchiveWriter().WriteAsync(
            destination,
            diagnostics,
            TestContext.Current.CancellationToken);

        using var archive = ZipFile.OpenRead(destination);
        var entry = Assert.Single(archive.Entries);
        Assert.Equal("diagnostics.json", entry.FullName);
        await using var entryStream = entry.Open();
        using var buffer = new MemoryStream();
        await entryStream.CopyToAsync(buffer, TestContext.Current.CancellationToken);
        var bytes = buffer.ToArray();
        Assert.False(bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }));
        var json = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
            .GetString(bytes);
        using var parsed = JsonDocument.Parse(json);
        Assert.Equal("FailSafe", parsed.RootElement.GetProperty("serviceState").GetString());
        Assert.Equal("风扇安全 ✓", parsed.RootElement.GetProperty("note").GetString());
        Assert.Contains("风扇安全 ✓", json, StringComparison.Ordinal);
        AssertNoTemporaryFiles(directory.Path);
    }

    [Fact]
    public async Task WriteAsync_ExistingDestinationIsRejectedAndPreserved()
    {
        using var directory = new TemporaryDirectory();
        var destination = Path.Combine(directory.Path, "diagnostics.zip");
        var original = Encoding.UTF8.GetBytes("existing evidence");
        await File.WriteAllBytesAsync(destination, original, TestContext.Current.CancellationToken);

        _ = await Assert.ThrowsAsync<DiagnosticsArchiveDestinationExistsException>(() =>
            new DiagnosticsArchiveWriter().WriteAsync(
                destination,
                SampleDiagnostics(),
                TestContext.Current.CancellationToken));

        Assert.Equal(original, await File.ReadAllBytesAsync(destination, TestContext.Current.CancellationToken));
        AssertNoTemporaryFiles(directory.Path);
    }

    [Fact]
    public async Task WriteAsync_PreCancelledRequestCreatesNothing()
    {
        using var directory = new TemporaryDirectory();
        var destination = Path.Combine(directory.Path, "diagnostics.zip");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new DiagnosticsArchiveWriter().WriteAsync(destination, SampleDiagnostics(), cancellation.Token));

        Assert.Empty(Directory.EnumerateFileSystemEntries(directory.Path));
    }

    [Fact]
    public async Task WriteAsync_CancellationDuringPayloadRemovesPartialArchive()
    {
        using var directory = new TemporaryDirectory();
        var destination = Path.Combine(directory.Path, "diagnostics.zip");
        using var cancellation = new CancellationTokenSource();
        var writer = new DiagnosticsArchiveWriter(
            new CancellingPayloadWriter(cancellation),
            FileSystemDiagnosticsArchiveCommitter.Instance);

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            writer.WriteAsync(destination, SampleDiagnostics(), cancellation.Token));

        Assert.False(File.Exists(destination));
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory.Path));
    }

    [Fact]
    public async Task WriteAsync_InjectedPayloadFailureRemovesPartialArchive()
    {
        using var directory = new TemporaryDirectory();
        var destination = Path.Combine(directory.Path, "diagnostics.zip");
        var writer = new DiagnosticsArchiveWriter(
            new ThrowingPayloadWriter(),
            FileSystemDiagnosticsArchiveCommitter.Instance);

        _ = await Assert.ThrowsAsync<InjectedDiagnosticsException>(() =>
            writer.WriteAsync(destination, SampleDiagnostics(), TestContext.Current.CancellationToken));

        Assert.False(File.Exists(destination));
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory.Path));
    }

    [Fact]
    public async Task WriteAsync_InjectedAtomicCommitFailureRemovesCompleteTemporaryArchive()
    {
        using var directory = new TemporaryDirectory();
        var destination = Path.Combine(directory.Path, "diagnostics.zip");
        var committer = new ThrowingCommitter();
        var writer = new DiagnosticsArchiveWriter(JsonDiagnosticsArchivePayloadWriter.Instance, committer);

        _ = await Assert.ThrowsAsync<InjectedDiagnosticsException>(() =>
            writer.WriteAsync(destination, SampleDiagnostics(), TestContext.Current.CancellationToken));

        Assert.True(committer.SawCompleteArchive);
        Assert.False(File.Exists(destination));
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory.Path));
    }

    [Fact]
    public async Task WriteAsync_DestinationCreatedDuringCommitIsPreservedAndTemporaryIsRemoved()
    {
        using var directory = new TemporaryDirectory();
        var destination = Path.Combine(directory.Path, "diagnostics.zip");
        var racingContents = Encoding.UTF8.GetBytes("created by another writer");
        var writer = new DiagnosticsArchiveWriter(
            JsonDiagnosticsArchivePayloadWriter.Instance,
            new RacingDestinationCommitter(racingContents));

        _ = await Assert.ThrowsAsync<DiagnosticsArchiveDestinationExistsException>(() =>
            writer.WriteAsync(destination, SampleDiagnostics(), TestContext.Current.CancellationToken));

        Assert.Equal(
            racingContents,
            await File.ReadAllBytesAsync(destination, TestContext.Current.CancellationToken));
        AssertNoTemporaryFiles(directory.Path);
    }

    private static JsonElement SampleDiagnostics() =>
        JsonSerializer.SerializeToElement(new { serviceState = "test", failSafe = true });

    private static void AssertNoTemporaryFiles(string directory) =>
        Assert.DoesNotContain(
            Directory.EnumerateFiles(directory),
            path => Path.GetFileName(path).StartsWith(".", StringComparison.Ordinal) &&
                    path.EndsWith(".tmp", StringComparison.Ordinal));

    private sealed class CancellingPayloadWriter(CancellationTokenSource cancellation)
        : IDiagnosticsArchivePayloadWriter
    {
        public async Task WriteAsync(
            Stream destination,
            JsonElement diagnostics,
            JsonSerializerOptions serializerOptions,
            CancellationToken cancellationToken)
        {
            await destination.WriteAsync("partial"u8.ToArray(), cancellationToken);
            cancellation.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private sealed class ThrowingPayloadWriter : IDiagnosticsArchivePayloadWriter
    {
        public async Task WriteAsync(
            Stream destination,
            JsonElement diagnostics,
            JsonSerializerOptions serializerOptions,
            CancellationToken cancellationToken)
        {
            await destination.WriteAsync("partial"u8.ToArray(), cancellationToken);
            throw new InjectedDiagnosticsException();
        }
    }

    private sealed class ThrowingCommitter : IDiagnosticsArchiveCommitter
    {
        public bool SawCompleteArchive { get; private set; }

        public void CommitNew(string temporaryPath, string destinationPath)
        {
            using (var archive = ZipFile.OpenRead(temporaryPath))
            {
                SawCompleteArchive = archive.Entries.Count == 1 &&
                                     archive.Entries[0].FullName == "diagnostics.json";
            }

            throw new InjectedDiagnosticsException();
        }
    }

    private sealed class RacingDestinationCommitter(byte[] destinationContents)
        : IDiagnosticsArchiveCommitter
    {
        public void CommitNew(string temporaryPath, string destinationPath)
        {
            File.WriteAllBytes(destinationPath, destinationContents);
            File.Move(temporaryPath, destinationPath, overwrite: false);
        }
    }

    private sealed class InjectedDiagnosticsException : Exception
    {
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"v12-diagnostics-{Guid.NewGuid():N}");
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
