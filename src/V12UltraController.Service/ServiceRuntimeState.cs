using System.Text.Json;

namespace V12UltraController.Service;

internal sealed record ServiceRuntimeState(
    bool Commissioned,
    string? OwnerSid,
    DateTimeOffset? CommissionedAtUtc)
{
    public static ServiceRuntimeState Initial { get; } = new(false, null, null);

    public void Validate()
    {
        if (!Commissioned)
        {
            if (OwnerSid is not null || CommissionedAtUtc is not null)
            {
                throw new InvalidDataException(
                    "An uncommissioned runtime state must not retain an owner or commissioning time.");
            }

            return;
        }

        if (string.IsNullOrWhiteSpace(OwnerSid) ||
            OwnerSid.Length > 256 ||
            OwnerSid.Any(char.IsControl) ||
            !OwnerSid.StartsWith("S-", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "A commissioned runtime state must contain a bounded single-line Windows SID.");
        }

        if (CommissionedAtUtc is null ||
            CommissionedAtUtc == default ||
            CommissionedAtUtc.Value.Offset != TimeSpan.Zero)
        {
            throw new InvalidDataException(
                "A commissioned runtime state must contain a non-default UTC commissioning time.");
        }
    }
}

internal sealed class ServiceRuntimeStateStore(ServicePaths paths)
{
    private const long MaximumDocumentBytes = 4096;
    private static readonly HashSet<string> ExpectedProperties = new(StringComparer.Ordinal)
    {
        "commissioned",
        "ownerSid",
        "commissionedAtUtc",
    };

    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<ServiceRuntimeState> LoadAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            FileStream stream;
            try
            {
                stream = new FileStream(
                    paths.RuntimeStatePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    4096,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
            }
            catch (FileNotFoundException)
            {
                return ServiceRuntimeState.Initial;
            }
            catch (DirectoryNotFoundException)
            {
                return ServiceRuntimeState.Initial;
            }

            await using (stream)
            {
                if (stream.Length is <= 0 or > MaximumDocumentBytes)
                {
                    throw new InvalidDataException(
                        "The service runtime state has an invalid document length.");
                }

                try
                {
                    using var document = await JsonDocument.ParseAsync(
                            stream,
                            new JsonDocumentOptions
                            {
                                AllowTrailingCommas = false,
                                CommentHandling = JsonCommentHandling.Disallow,
                                MaxDepth = 4,
                            },
                            cancellationToken)
                        .ConfigureAwait(false);
                    ValidateDocumentShape(document.RootElement);

                    var state = document.RootElement.Deserialize<ServiceRuntimeState>(jsonOptions)
                        ?? throw new InvalidDataException("The service runtime state is empty.");
                    state.Validate();
                    return state;
                }
                catch (JsonException exception)
                {
                    throw new InvalidDataException(
                        "The service runtime state is not valid strict JSON.",
                        exception);
                }
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task SaveAsync(ServiceRuntimeState state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        state.Validate();
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await AtomicFile.WriteJsonAsync(paths.RuntimeStatePath, state, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private static void ValidateDocumentShape(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("The service runtime state must be a JSON object.");
        }

        var remaining = new HashSet<string>(ExpectedProperties, StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (!ExpectedProperties.Contains(property.Name))
            {
                throw new InvalidDataException(
                    $"The service runtime state contains an unknown property '{property.Name}'.");
            }

            if (!remaining.Remove(property.Name))
            {
                throw new InvalidDataException(
                    $"The service runtime state contains duplicate property '{property.Name}'.");
            }
        }

        if (remaining.Count > 0)
        {
            throw new InvalidDataException(
                $"The service runtime state is missing required properties: {string.Join(", ", remaining.Order())}.");
        }
    }
}
