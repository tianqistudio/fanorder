using System.Text.Json;
using System.Text.Json.Serialization;

namespace V12UltraController.Core.Configuration;

public interface IConfigurationStore
{
    Task<ControllerConfiguration> LoadAsync(CancellationToken cancellationToken);

    Task SaveAsync(ControllerConfiguration configuration, CancellationToken cancellationToken);
}

public sealed class AtomicJsonConfigurationStore : IConfigurationStore, IDisposable
{
    private const long MaximumDocumentBytes = 64 * 1024;

    private static readonly HashSet<string> SchemaOneProperties = new(StringComparer.Ordinal)
    {
        "schemaVersion",
        "commissioned",
        "mode",
        "manualSpeedPercent",
        "curve",
    };

    private static readonly HashSet<string> SchemaTwoProperties = new(SchemaOneProperties, StringComparer.Ordinal)
    {
        "systemShutdownFanBehavior",
        "shutdownOffQualification",
    };

    private static readonly HashSet<string> SchemaThreeProperties = new(SchemaTwoProperties, StringComparer.Ordinal)
    {
        "startupFanPercent",
    };

    private static readonly HashSet<string> CurveProperties = new(StringComparer.Ordinal)
    {
        "points",
    };

    private static readonly HashSet<string> CurvePointProperties = new(StringComparer.Ordinal)
    {
        "temperatureCelsius",
        "fanPercent",
    };

    private static readonly HashSet<string> ShutdownQualificationProperties = new(StringComparer.Ordinal)
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

    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IAtomicConfigurationCommitter _committer;
    private bool _disposed;

    public AtomicJsonConfigurationStore(string path)
        : this(path, FileSystemAtomicConfigurationCommitter.Instance)
    {
    }

    internal AtomicJsonConfigurationStore(string path, IAtomicConfigurationCommitter committer)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
        _committer = committer ?? throw new ArgumentNullException(nameof(committer));
    }

    public async Task<ControllerConfiguration> LoadAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            try
            {
                await using var stream = new FileStream(
                    _path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    4096,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                if (stream.Length is <= 0 or > MaximumDocumentBytes)
                {
                    throw new ConfigurationValidationException(
                        $"The controller configuration must contain between 1 and {MaximumDocumentBytes} bytes.");
                }

                using var document = await JsonDocument.ParseAsync(
                        stream,
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                ValidateDocumentShape(document.RootElement);
                var configuration = document.RootElement
                    .Deserialize<ControllerConfiguration>(SerializerOptions)
                    ?? throw new ConfigurationValidationException("The configuration document is empty.");
                configuration = configuration.UpgradeToCurrentSchema();
                configuration.Validate();
                return configuration;
            }
            catch (FileNotFoundException)
            {
                return ControllerConfiguration.CreateDefault();
            }
            catch (DirectoryNotFoundException)
            {
                return ControllerConfiguration.CreateDefault();
            }
            catch (ConfigurationValidationException)
            {
                throw;
            }
            catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
            {
                throw new ConfigurationValidationException("Unable to load a valid controller configuration.", exception);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(ControllerConfiguration configuration, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(configuration);
        configuration.Validate();

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? temporaryPath = null;
        try
        {
            var directory = Path.GetDirectoryName(_path)
                ?? throw new InvalidOperationException("The configuration path does not have a parent directory.");
            Directory.CreateDirectory(directory);
            temporaryPath = Path.Combine(
                directory,
                $".{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");

            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             4096,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, configuration, SerializerOptions, cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            _committer.Commit(temporaryPath, _path);

            temporaryPath = null;
        }
        finally
        {
            try
            {
                if (temporaryPath is not null)
                {
                    File.Delete(temporaryPath);
                }
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _gate.Dispose();
    }

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = false,
            NumberHandling = JsonNumberHandling.Strict,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        return options;
    }

    private static void ValidateDocumentShape(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new ConfigurationValidationException("The controller configuration must be a JSON object.");
        }

        const string serializedPropertyName = "schemaVersion";
        JsonElement schemaElement = default;
        var exactMatches = 0;
        foreach (var property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, serializedPropertyName, StringComparison.Ordinal))
            {
                exactMatches++;
                schemaElement = property.Value;
            }
            else if (string.Equals(property.Name, serializedPropertyName, StringComparison.OrdinalIgnoreCase))
            {
                throw new ConfigurationValidationException(
                    $"The configuration property '{serializedPropertyName}' must use exact camel-case spelling.");
            }
        }

        if (exactMatches != 1)
        {
            throw new ConfigurationValidationException(
                exactMatches == 0
                    ? $"The configuration property '{serializedPropertyName}' is required."
                    : $"The configuration property '{serializedPropertyName}' must appear exactly once.");
        }

        if (schemaElement.ValueKind != JsonValueKind.Number || !schemaElement.TryGetInt32(out var schemaVersion))
        {
            throw new ConfigurationValidationException(
                $"The configuration property '{serializedPropertyName}' must be an integer.");
        }

        if (schemaVersion is not 1 and not 2 && schemaVersion != ControllerConfiguration.CurrentSchemaVersion)
        {
            throw new ConfigurationValidationException(
                $"Unsupported configuration schema {schemaVersion}; expected 1, 2 or {ControllerConfiguration.CurrentSchemaVersion}.");
        }

        ValidateExactObjectShape(
            root,
            schemaVersion == 1 ? SchemaOneProperties : schemaVersion == 2 ? SchemaTwoProperties : SchemaThreeProperties,
            "controller configuration");
        RequireKind(root, "commissioned", "controller configuration", JsonValueKind.True, JsonValueKind.False);
        RequireExactString(
            root,
            "mode",
            "controller configuration",
            "auto",
            "manual", "pureManual", "released");
        RequireKind(root, "manualSpeedPercent", "controller configuration", JsonValueKind.Number);

        var curve = RequireKind(root, "curve", "controller configuration", JsonValueKind.Object);
        ValidateExactObjectShape(curve, CurveProperties, "fan curve");
        var points = RequireKind(curve, "points", "fan curve", JsonValueKind.Array);
        foreach (var point in points.EnumerateArray())
        {
            if (point.ValueKind != JsonValueKind.Object)
            {
                throw new ConfigurationValidationException("Every fan-curve point must be a JSON object.");
            }

            ValidateExactObjectShape(point, CurvePointProperties, "fan-curve point");
            RequireKind(point, "temperatureCelsius", "fan-curve point", JsonValueKind.Number);
            RequireKind(point, "fanPercent", "fan-curve point", JsonValueKind.Number);
        }

        if (schemaVersion >= 2)
        {
            RequireExactString(
                root,
                "systemShutdownFanBehavior",
                "controller configuration",
                "keepAt100",
                "turnOffOnSystemShutdown");
            var qualification = root.GetProperty("shutdownOffQualification");
            if (qualification.ValueKind == JsonValueKind.Null)
            {
                if (schemaVersion == ControllerConfiguration.CurrentSchemaVersion)
                    RequireKind(root, "startupFanPercent", "controller configuration", JsonValueKind.Number);
                return;
            }

            if (qualification.ValueKind != JsonValueKind.Object)
            {
                throw new ConfigurationValidationException(
                    "The controller configuration property 'shutdownOffQualification' must be an object or null.");
            }

            ValidateExactObjectShape(
                qualification,
                ShutdownQualificationProperties,
                "shutdown qualification");
            RequireKind(qualification, "protocolRevision", "shutdown qualification", JsonValueKind.Number);
            RequireKind(qualification, "vendorId", "shutdown qualification", JsonValueKind.Number);
            RequireKind(qualification, "productId", "shutdown qualification", JsonValueKind.Number);
            RequireKind(qualification, "firmwareVersion", "shutdown qualification", JsonValueKind.Number);
            RequireKind(qualification, "usagePage", "shutdown qualification", JsonValueKind.Number);
            RequireKind(qualification, "usage", "shutdown qualification", JsonValueKind.Number);
            RequireKind(qualification, "featureReportLength", "shutdown qualification", JsonValueKind.Number);
            RequireKind(
                qualification,
                "shutdownOffReadbackConfirmed",
                "shutdown qualification",
                JsonValueKind.True,
                JsonValueKind.False);
            RequireKind(
                qualification,
                "startupOnAt100ReadbackConfirmed",
                "shutdown qualification",
                JsonValueKind.True,
                JsonValueKind.False);
            RequireKind(
                qualification,
                "lightingStatePreserved",
                "shutdown qualification",
                JsonValueKind.True,
                JsonValueKind.False);
            RequireKind(qualification, "qualifiedAtUtc", "shutdown qualification", JsonValueKind.String);
            if (schemaVersion == ControllerConfiguration.CurrentSchemaVersion)
                RequireKind(root, "startupFanPercent", "controller configuration", JsonValueKind.Number);
        }
    }

    private static void ValidateExactObjectShape(
        JsonElement element,
        IReadOnlySet<string> expectedProperties,
        string description)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new ConfigurationValidationException($"The {description} must be a JSON object.");
        }

        var remaining = new HashSet<string>(expectedProperties, StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!expectedProperties.Contains(property.Name))
            {
                throw new ConfigurationValidationException(
                    $"The {description} contains unknown or incorrectly-cased property '{property.Name}'.");
            }

            if (!remaining.Remove(property.Name))
            {
                throw new ConfigurationValidationException(
                    $"The {description} contains duplicate property '{property.Name}'.");
            }
        }

        if (remaining.Count > 0)
        {
            throw new ConfigurationValidationException(
                $"The {description} is missing required properties: {string.Join(", ", remaining.Order())}.");
        }
    }

    private static JsonElement RequireKind(
        JsonElement parent,
        string propertyName,
        string description,
        params JsonValueKind[] allowedKinds)
    {
        var value = parent.GetProperty(propertyName);
        if (!allowedKinds.Contains(value.ValueKind))
        {
            throw new ConfigurationValidationException(
                $"The {description} property '{propertyName}' has an invalid JSON type.");
        }

        return value;
    }

    private static void RequireExactString(
        JsonElement parent,
        string propertyName,
        string description,
        params string[] allowedValues)
    {
        var value = RequireKind(parent, propertyName, description, JsonValueKind.String).GetString();
        if (value is null || !allowedValues.Contains(value, StringComparer.Ordinal))
        {
            throw new ConfigurationValidationException(
                $"The {description} property '{propertyName}' has an invalid string value.");
        }
    }
}

internal interface IAtomicConfigurationCommitter
{
    void Commit(string temporaryPath, string destinationPath);
}

internal sealed class FileSystemAtomicConfigurationCommitter : IAtomicConfigurationCommitter
{
    public static FileSystemAtomicConfigurationCommitter Instance { get; } = new();

    private FileSystemAtomicConfigurationCommitter()
    {
    }

    public void Commit(string temporaryPath, string destinationPath)
    {
        if (File.Exists(destinationPath))
        {
            File.Replace(temporaryPath, destinationPath, null, ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(temporaryPath, destinationPath);
        }
    }
}
