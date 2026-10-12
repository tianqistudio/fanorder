using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using V12UltraController.Core.Hardware;

namespace V12UltraController.Service;

internal sealed record DeviceStateCacheEntry(
    int SchemaVersion,
    DeviceIdentity Identity,
    byte[] FeatureReport,
    DateTimeOffset CapturedAtUtc,
    string Sha256)
{
    public const int CurrentSchemaVersion = 1;
}

/// <summary>
/// Persists the last fully validated non-sensitive device state. The cache contains no device
/// path or serial number and can only be consumed after an exact allow-listed identity match.
/// </summary>
internal interface IDeviceStateCacheStore
{
    Task<V12DeviceState?> LoadAsync(
        DeviceIdentity expectedIdentity,
        CancellationToken cancellationToken = default);

    Task SaveAsync(
        DeviceIdentity identity,
        V12DeviceState state,
        DateTimeOffset capturedAtUtc,
        CancellationToken cancellationToken = default);
}

internal sealed class DeviceStateCacheStore(ServicePaths paths) : IDeviceStateCacheStore
{
    private const long MaximumDocumentBytes = 16 * 1024;
    private static readonly HashSet<string> ExpectedEntryProperties = new(StringComparer.Ordinal)
    {
        "schemaVersion",
        "identity",
        "featureReport",
        "capturedAtUtc",
        "sha256",
    };
    private static readonly HashSet<string> ExpectedIdentityProperties = new(StringComparer.Ordinal)
    {
        "vendorId",
        "productId",
        "version",
        "usagePage",
        "usage",
        "featureReportLength",
    };

    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<V12DeviceState?> LoadAsync(
        DeviceIdentity expectedIdentity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expectedIdentity);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            FileStream stream;
            try
            {
                stream = new FileStream(
                    paths.DeviceStateCachePath,
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

            DeviceStateCacheEntry entry;
            await using (stream)
            {
                if (stream.Length is <= 0 or > MaximumDocumentBytes)
                {
                    throw new InvalidDataException(
                        "The V12 device-state cache has an invalid document length.");
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
                    ValidateExactObjectShape(
                        document.RootElement,
                        ExpectedEntryProperties,
                        "V12 device-state cache");
                    if (!document.RootElement.TryGetProperty("identity", out var identityElement))
                    {
                        throw new InvalidDataException(
                            "The V12 device-state cache identity is missing.");
                    }

                    ValidateExactObjectShape(
                        identityElement,
                        ExpectedIdentityProperties,
                        "V12 device-state cache identity");
                    entry = document.RootElement.Deserialize<DeviceStateCacheEntry>(jsonOptions)
                        ?? throw new InvalidDataException("The V12 device-state cache is empty.");
                }
                catch (JsonException exception)
                {
                    throw new InvalidDataException(
                        "The V12 device-state cache is not valid strict JSON.",
                        exception);
                }
            }

            if (entry.SchemaVersion != DeviceStateCacheEntry.CurrentSchemaVersion)
            {
                throw new InvalidDataException(
                    $"Unsupported V12 device-state cache schema {entry.SchemaVersion}.");
            }

            if (entry.Identity is null)
            {
                throw new InvalidDataException("The cached V12 identity is missing.");
            }

            if (entry.Identity != expectedIdentity || !expectedIdentity.IsSupportedV12Ultra)
            {
                return null;
            }

            if (entry.CapturedAtUtc == default || entry.CapturedAtUtc.Offset != TimeSpan.Zero)
            {
                throw new InvalidDataException("The cached V12 capture time must be non-default UTC.");
            }

            if (entry.FeatureReport is null || entry.FeatureReport.Length != V12FeatureReport.ReportLength)
            {
                throw new InvalidDataException("The cached V12 feature report has an invalid length.");
            }

            var expectedHash = CalculateHash(entry with { Sha256 = string.Empty });
            if (entry.Sha256 is null ||
                entry.Sha256.Length != 64 ||
                !entry.Sha256.All(Uri.IsHexDigit) ||
                !CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(expectedHash),
                    Encoding.ASCII.GetBytes(entry.Sha256.ToUpperInvariant())))
            {
                throw new InvalidDataException("The V12 device-state cache SHA-256 is invalid.");
            }

            try
            {
                return V12FeatureReport.ParseState(entry.FeatureReport);
            }
            catch (V12ProtocolException exception)
            {
                throw new InvalidDataException("The cached V12 feature report is invalid.", exception);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task SaveAsync(
        DeviceIdentity identity,
        V12DeviceState state,
        DateTimeOffset capturedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(state);
        if (!identity.IsSupportedV12Ultra)
        {
            throw new InvalidDataException("Only the exact allow-listed V12 Ultra identity may be cached.");
        }

        var unsignedEntry = new DeviceStateCacheEntry(
            DeviceStateCacheEntry.CurrentSchemaVersion,
            identity,
            state.ExportFeatureReport(),
            capturedAtUtc.ToUniversalTime(),
            string.Empty);
        var entry = unsignedEntry with { Sha256 = CalculateHash(unsignedEntry) };

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await AtomicFile.WriteJsonAsync(paths.DeviceStateCachePath, entry, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private static string CalculateHash(DeviceStateCacheEntry entry)
    {
        var identity = entry.Identity;
        var canonical = string.Join('\n',
            entry.SchemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
            identity.VendorId.ToString("X4", System.Globalization.CultureInfo.InvariantCulture),
            identity.ProductId.ToString("X4", System.Globalization.CultureInfo.InvariantCulture),
            identity.Version.ToString("X4", System.Globalization.CultureInfo.InvariantCulture),
            identity.UsagePage.ToString("X4", System.Globalization.CultureInfo.InvariantCulture),
            identity.Usage.ToString("X4", System.Globalization.CultureInfo.InvariantCulture),
            identity.FeatureReportLength.ToString(System.Globalization.CultureInfo.InvariantCulture),
            entry.CapturedAtUtc.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Convert.ToHexString(entry.FeatureReport));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static void ValidateExactObjectShape(
        JsonElement element,
        HashSet<string> expectedProperties,
        string description)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"The {description} must be a JSON object.");
        }

        var remaining = new HashSet<string>(expectedProperties, StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!expectedProperties.Contains(property.Name))
            {
                throw new InvalidDataException(
                    $"The {description} contains unknown property '{property.Name}'.");
            }

            if (!remaining.Remove(property.Name))
            {
                throw new InvalidDataException(
                    $"The {description} contains duplicate property '{property.Name}'.");
            }
        }

        if (remaining.Count > 0)
        {
            throw new InvalidDataException(
                $"The {description} is missing required properties: {string.Join(", ", remaining.Order())}.");
        }
    }
}
