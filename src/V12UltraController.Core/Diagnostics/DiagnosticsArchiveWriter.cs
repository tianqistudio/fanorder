using System.IO.Compression;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

namespace V12UltraController.Core.Diagnostics;

/// <summary>
/// Writes the already-redacted diagnostics returned by the local service to a
/// single-entry ZIP archive using the calling user's file-system authority.
/// </summary>
public sealed class DiagnosticsArchiveWriter
{
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();
    private readonly IDiagnosticsArchivePayloadWriter _payloadWriter;
    private readonly IDiagnosticsArchiveCommitter _committer;

    public DiagnosticsArchiveWriter()
        : this(JsonDiagnosticsArchivePayloadWriter.Instance, FileSystemDiagnosticsArchiveCommitter.Instance)
    {
    }

    internal DiagnosticsArchiveWriter(
        IDiagnosticsArchivePayloadWriter payloadWriter,
        IDiagnosticsArchiveCommitter committer)
    {
        _payloadWriter = payloadWriter ?? throw new ArgumentNullException(nameof(payloadWriter));
        _committer = committer ?? throw new ArgumentNullException(nameof(committer));
    }

    public async Task WriteAsync(
        string destinationPath,
        JsonElement diagnostics,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        cancellationToken.ThrowIfCancellationRequested();

        var fullDestinationPath = Path.GetFullPath(destinationPath);
        var directory = Path.GetDirectoryName(fullDestinationPath)
            ?? throw new InvalidOperationException("The diagnostics output path has no parent directory.");
        Directory.CreateDirectory(directory);

        if (File.Exists(fullDestinationPath))
        {
            throw new DiagnosticsArchiveDestinationExistsException();
        }

        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(fullDestinationPath)}.{Guid.NewGuid():N}.tmp");
        var temporaryCreated = false;
        var committed = false;
        try
        {
            await using (var stream = OpenTemporaryFile(temporaryPath))
            {
                temporaryCreated = true;
                using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
                {
                    var entry = archive.CreateEntry("diagnostics.json", CompressionLevel.Optimal);
                    await using var entryStream = entry.Open();
                    await _payloadWriter.WriteAsync(
                            entryStream,
                            diagnostics,
                            SerializerOptions,
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                _committer.CommitNew(temporaryPath, fullDestinationPath);
            }
            catch (IOException exception) when (File.Exists(fullDestinationPath))
            {
                throw new DiagnosticsArchiveDestinationExistsException(exception);
            }

            committed = true;
        }
        finally
        {
            if (!committed && temporaryCreated)
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static FileStream OpenTemporaryFile(string temporaryPath) => new(
        temporaryPath,
        FileMode.CreateNew,
        FileAccess.Write,
        FileShare.None,
        64 * 1024,
        FileOptions.Asynchronous | FileOptions.WriteThrough);

    private static JsonSerializerOptions CreateSerializerOptions() => new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };
}

public sealed class DiagnosticsArchiveDestinationExistsException : IOException
{
    public DiagnosticsArchiveDestinationExistsException()
        : base("The diagnostics output already exists; choose a new path. Existing files are never overwritten.")
    {
    }

    internal DiagnosticsArchiveDestinationExistsException(Exception innerException)
        : base("The diagnostics output already exists; choose a new path. Existing files are never overwritten.",
            innerException)
    {
    }
}

internal interface IDiagnosticsArchivePayloadWriter
{
    Task WriteAsync(
        Stream destination,
        JsonElement diagnostics,
        JsonSerializerOptions serializerOptions,
        CancellationToken cancellationToken);
}

internal sealed class JsonDiagnosticsArchivePayloadWriter : IDiagnosticsArchivePayloadWriter
{
    public static JsonDiagnosticsArchivePayloadWriter Instance { get; } = new();

    private JsonDiagnosticsArchivePayloadWriter()
    {
    }

    public Task WriteAsync(
        Stream destination,
        JsonElement diagnostics,
        JsonSerializerOptions serializerOptions,
        CancellationToken cancellationToken) =>
        JsonSerializer.SerializeAsync(destination, diagnostics, serializerOptions, cancellationToken);
}

internal interface IDiagnosticsArchiveCommitter
{
    void CommitNew(string temporaryPath, string destinationPath);
}

internal sealed class FileSystemDiagnosticsArchiveCommitter : IDiagnosticsArchiveCommitter
{
    public static FileSystemDiagnosticsArchiveCommitter Instance { get; } = new();

    private FileSystemDiagnosticsArchiveCommitter()
    {
    }

    public void CommitNew(string temporaryPath, string destinationPath) =>
        File.Move(temporaryPath, destinationPath, overwrite: false);
}
