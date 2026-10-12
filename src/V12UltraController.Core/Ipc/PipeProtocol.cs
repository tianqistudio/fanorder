using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace V12UltraController.Core.Ipc;

public static class PipeProtocol
{
    public const string Name = "TianqiStudio.V12UltraController.v1";
    public const string ServiceName = "TianqiStudio.V12UltraController";
    public const int ApiVersion = 1;
    public const int MaxMessageBytes = 1024 * 1024;
}

public static class PipeOperations
{
    public const string StatusGet = "status.get";
    public const string StartupGet = "startup.get";
    public const string StartupSet = "startup.set";
    public const string StartupFanGet = "startup-fan.get";
    public const string StartupFanSet = "startup-fan.set";
    public const string ModeSet = "mode.set";
    public const string SpeedSet = "speed.set";
    public const string PowerSet = "power.set";
    public const string LightingGet = "lighting.get";
    public const string LightingSet = "lighting.set";
    public const string CurveGet = "curve.get";
    public const string CurveSet = "curve.set";
    public const string HistoryAnalyze = "history.analyze";
    public const string CurveAdviceApply = "curve.advice.apply";
    public const string ProfileApply = "profile.apply";
    public const string SafetyDisable = "safety.disable";
    public const string SafetyResume = "safety.resume";
    public const string DiagnosticsExport = "diagnostics.export";
    public const string CommissionProbe = "commission.probe";
    public const string CommissionIdempotent = "commission.idempotent";
    public const string CommissionTest = "commission.test";
    public const string CommissionCommit = "commission.commit";
    public const string CommissionAbort = "commission.abort";
    public const string CommissionRecoverSafety = "commission.recover-safety";
    public const string CommissionRollback = "commission.rollback";
    public const string CommissionRecoverTaskTransition = "commission.recover-task-transition";
    public const string ShutdownStatus = "shutdown.status";
    public const string ShutdownQualify = "shutdown.qualify";
    public const string ShutdownEnable = "shutdown.enable";
    public const string ShutdownDisable = "shutdown.disable";
}

public static class PipeErrorCodes
{
    public const string InvalidArgument = "INVALID_ARGUMENT";
    public const string ServiceUnavailable = "SERVICE_UNAVAILABLE";
    public const string NotCommissioned = "NOT_COMMISSIONED";
    public const string DeviceNotFound = "DEVICE_NOT_FOUND";
    public const string UnsupportedDevice = "UNSUPPORTED_DEVICE";
    public const string MythCoolConflict = "MYTHCOOL_CONFLICT";
    public const string TelemetryStale = "TELEMETRY_STALE";
    public const string FailSafeActive = "FAILSAFE_ACTIVE";
    public const string HidTimeout = "HID_TIMEOUT";
    public const string ProtocolChecksum = "PROTOCOL_CHECKSUM";
    public const string DeviceStateChanged = "DEVICE_STATE_CHANGED";
    public const string CommandReadbackMismatch = "COMMAND_READBACK_MISMATCH";
    public const string InternalError = "INTERNAL_ERROR";
}

public sealed record PipeRequest(
    int ApiVersion,
    string RequestId,
    string Operation,
    JsonElement Payload)
{
    public static PipeRequest Create<T>(string operation, T payload, string? requestId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        return new PipeRequest(
            PipeProtocol.ApiVersion,
            requestId ?? Guid.NewGuid().ToString("D"),
            operation,
            JsonSerializer.SerializeToElement(payload, PipeJson.Options));
    }
}

public sealed record PipeError(string Code, string Message);

public sealed record PipeResponse(
    int ApiVersion,
    string RequestId,
    bool Ok,
    JsonElement? Data,
    PipeError? Error,
    DateTimeOffset Timestamp)
{
    public static PipeResponse Success<T>(string requestId, T data) => new(
        PipeProtocol.ApiVersion,
        requestId,
        true,
        JsonSerializer.SerializeToElement(data, PipeJson.Options),
        null,
        DateTimeOffset.UtcNow);

    public static PipeResponse Failure(string requestId, string code, string message) => new(
        PipeProtocol.ApiVersion,
        requestId,
        false,
        null,
        new PipeError(code, message),
        DateTimeOffset.UtcNow);
}

public static class PipeMessageFraming
{
    public static async Task WriteAsync<T>(Stream stream, T message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(message);

        var body = JsonSerializer.SerializeToUtf8Bytes(message, PipeJson.Options);
        if (body.Length is <= 0 or > PipeProtocol.MaxMessageBytes)
        {
            throw new InvalidDataException(
                $"Named-pipe message length must be between 1 and {PipeProtocol.MaxMessageBytes} bytes.");
        }

        var prefix = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, body.Length);
        await stream.WriteAsync(prefix, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var prefix = new byte[sizeof(int)];
        await stream.ReadExactlyAsync(prefix, cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (length is <= 0 or > PipeProtocol.MaxMessageBytes)
        {
            throw new InvalidDataException(
                $"Named-pipe message length must be between 1 and {PipeProtocol.MaxMessageBytes} bytes.");
        }

        var body = new byte[length];
        await stream.ReadExactlyAsync(body, cancellationToken).ConfigureAwait(false);
        try
        {
            return JsonSerializer.Deserialize<T>(body, PipeJson.Options)
                ?? throw new InvalidDataException("The named-pipe message contained JSON null.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The named-pipe message is not valid JSON.", exception);
        }
    }
}

public interface IPipeApiClient
{
    Task<PipeResponse> SendAsync(
        PipeRequest request,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}

public sealed class NamedPipeApiClient : IPipeApiClient
{
    private readonly INamedPipeServerVerifier serverVerifier;
    private readonly string pipeName;

    public NamedPipeApiClient()
        : this(new NamedPipeServerVerifier(new WindowsPipeServerIdentitySource()), PipeProtocol.Name)
    {
    }

    internal NamedPipeApiClient(INamedPipeServerVerifier serverVerifier, string pipeName)
    {
        this.serverVerifier = serverVerifier ?? throw new ArgumentNullException(nameof(serverVerifier));
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        this.pipeName = pipeName;
    }

    public async Task<PipeResponse> SendAsync(
        PipeRequest request,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        await using var pipe = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous,
            TokenImpersonationLevel.Identification);
        await pipe.ConnectAsync(timeoutSource.Token).ConfigureAwait(false);
        serverVerifier.Verify(pipe);
        await PipeMessageFraming.WriteAsync(pipe, request, timeoutSource.Token).ConfigureAwait(false);
        return await PipeMessageFraming.ReadAsync<PipeResponse>(pipe, timeoutSource.Token).ConfigureAwait(false);
    }
}

internal static class PipeJson
{
    internal static JsonSerializerOptions Options { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new PipeRequestJsonConverter());
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}
