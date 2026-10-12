using System.Text.Json;
using System.Text.Json.Serialization;

namespace V12UltraController.Service;

internal sealed record SafetyProtectionIntent([property: JsonRequired] int SchemaVersion,
    [property: JsonRequired] bool Disabled, [property: JsonRequired] DateTimeOffset? DisabledAt,
    [property: JsonRequired] string? CallerSid);

internal interface ISafetyProtectionStore
{
    Task<SafetyProtectionIntent?> LoadAsync(CancellationToken cancellationToken);
    Task SaveAsync(SafetyProtectionIntent intent, CancellationToken cancellationToken);
}

// This independent intent never changes controller.json or either pending audit journal.
// Its directory has the installer's service-writer ACL; clients only use the pipe API.
internal sealed class SafetyProtectionStore(ServicePaths paths) : ISafetyProtectionStore
{
    public async Task<SafetyProtectionIntent?> LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var input = new FileStream(paths.SafetyProtectionPath, FileMode.Open,
                FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
            if (input.Length is <= 0 or > 4096) throw new InvalidDataException("Invalid safety intent size.");
            using var document = await JsonDocument.ParseAsync(input, cancellationToken: cancellationToken).ConfigureAwait(false);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid safety intent shape.");
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
                if (!names.Add(property.Name) || property.Name is not ("SchemaVersion" or "Disabled" or "DisabledAt" or "CallerSid"))
                    throw new InvalidDataException("Invalid safety intent fields.");
            if (names.Count != 4) throw new InvalidDataException("Incomplete safety intent.");
            var intent = root.Deserialize<SafetyProtectionIntent>();
            if (intent is null || intent.SchemaVersion != 1 || (intent.Disabled && intent.DisabledAt is null) ||
                (!intent.Disabled && intent.DisabledAt is not null))
                throw new InvalidDataException("Invalid safety intent.");
            return intent;
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    public async Task SaveAsync(SafetyProtectionIntent intent, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(paths.DataDirectory);
        var temporary = paths.SafetyProtectionPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(output, intent, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, paths.SafetyProtectionPath, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
