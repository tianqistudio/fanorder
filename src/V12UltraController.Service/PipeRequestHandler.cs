using System.Text.Json;
using System.Text.Json.Serialization;
using V12UltraController.Core.Configuration;
using V12UltraController.Core.Control;
using V12UltraController.Core.Hardware;
using V12UltraController.Core.Ipc;

namespace V12UltraController.Service;

internal sealed class PipeRequestHandler
{
    private readonly IControllerApi coordinator;
    private readonly JsonlAuditLog auditLog;
    private readonly ILogger<PipeRequestHandler> logger;
    private readonly ThermalHistoryRecorder? history;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private static readonly string[] NoProperties = [];
    private static readonly string[] ModeProperties = ["mode"];
    private static readonly string[] SpeedProperties = ["percent"];
    private static readonly string[] StartupProperties = ["enabled"];
    private static readonly string[] StartupFanProperties = ["percent"];
    private static readonly string[] SafetyResumeProperties = ["confirmed"];
    private static readonly string[] PowerProperties = ["poweredOn", "confirmed"];
    private static readonly string[] CurveProperties = ["points"];
    private static readonly string[] AttendedProperties = ["attended"];
    private static readonly string[] AttendedSessionProperties = ["attended", "sessionId"];
    private static readonly string[] NearFullTestProperties = ["attended", "sessionId", "nearFullOnly"];
    private static readonly string[] SessionProperties = ["sessionId"];
    private static readonly string[] RollbackProperties = ["startMythCool", "installer", "attended"];
    private static readonly string[] ShutdownEnableProperties = ["attended", "qualificationId"];
    private static readonly string[] CurvePointProperties = ["temperatureCelsius", "fanPercent"];

    public PipeRequestHandler(
        ControllerCoordinator coordinator,
        JsonlAuditLog auditLog,
        ILogger<PipeRequestHandler> logger,
        ThermalHistoryRecorder history)
        : this(new ControllerApiAdapter(coordinator), auditLog, logger)
    {
        this.history = history;
    }

    public PipeRequestHandler(ControllerCoordinator coordinator, JsonlAuditLog auditLog, ILogger<PipeRequestHandler> logger)
        : this(new ControllerApiAdapter(coordinator), auditLog, logger) { }

    internal PipeRequestHandler(
        IControllerApi coordinator,
        JsonlAuditLog auditLog,
        ILogger<PipeRequestHandler> logger)
    {
        this.coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        this.auditLog = auditLog ?? throw new ArgumentNullException(nameof(auditLog));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<PipeResponse> HandleAsync(
        PipeRequest request,
        PipePeerIdentity peer,
        CancellationToken cancellationToken)
    {
        var requestId = IsCanonicalRequestId(request.RequestId) ? request.RequestId : "unknown";
        try
        {
            ValidateEnvelope(request);
            ValidatePayloadShape(request);
            return request.Operation switch
            {
                PipeOperations.StatusGet => PipeResponse.Success(requestId, coordinator.GetStatus()),
                PipeOperations.SafetyDisable => PipeResponse.Success(requestId,
                    await coordinator.DisableSafetyProtectionAsync(peer, cancellationToken).ConfigureAwait(false)),
                PipeOperations.SafetyResume => PipeResponse.Success(requestId,
                    await coordinator.ResumeSafetyProtectionAsync(request.Payload.GetProperty("confirmed").GetBoolean(),
                        peer, cancellationToken).ConfigureAwait(false)),
                PipeOperations.LightingGet => PipeResponse.Success(requestId,
                    await coordinator.GetLightingAsync(cancellationToken).ConfigureAwait(false)),
                PipeOperations.LightingSet => PipeResponse.Success(requestId,
                    await coordinator.SetLightingAsync(ParseLighting(request.Payload), peer, cancellationToken).ConfigureAwait(false)),
                PipeOperations.StartupGet => PipeResponse.Success(requestId, coordinator.GetStartup()),
                PipeOperations.StartupSet => PipeResponse.Success(requestId,
                    await coordinator.SetStartupAsync(request.Payload.GetProperty("enabled").GetBoolean(),
                        peer, cancellationToken).ConfigureAwait(false)),
                PipeOperations.StartupFanGet => PipeResponse.Success(requestId, coordinator.GetStartupFan()),
                PipeOperations.StartupFanSet => PipeResponse.Success(requestId,
                    await coordinator.SetStartupFanPercentAsync(ParseStartupFanPercent(request.Payload),
                        peer, cancellationToken).ConfigureAwait(false)),
                PipeOperations.CommissionRecoverSafety => PipeResponse.Success(requestId,
                    await coordinator.RecoverCommissioningSafetyAsync(request.Payload.GetProperty("attended").GetBoolean(),
                        peer, cancellationToken).ConfigureAwait(false)),
                PipeOperations.ModeSet => await SetModeAsync(request, peer, cancellationToken).ConfigureAwait(false),
                PipeOperations.SpeedSet => await SetSpeedAsync(request, peer, cancellationToken).ConfigureAwait(false),
                PipeOperations.PowerSet => PipeResponse.Success(requestId,
                    await coordinator.SetManualPowerAsync(request.Payload.GetProperty("poweredOn").GetBoolean(),
                        request.Payload.GetProperty("confirmed").GetBoolean(), peer, cancellationToken).ConfigureAwait(false)),
                PipeOperations.CurveGet => PipeResponse.Success(requestId, coordinator.GetCurve()),
                PipeOperations.HistoryAnalyze => PipeResponse.Success(requestId,
                    history?.Analyze(coordinator.GetCurve()) ??
                    V12UltraController.Core.Status.CurveAdvisor.Analyze([], coordinator.GetCurve(), DateTimeOffset.UtcNow)),
                PipeOperations.CurveAdviceApply => await ApplyCurveAdviceAsync(request, peer, cancellationToken).ConfigureAwait(false),
                PipeOperations.ProfileApply => PipeResponse.Success(requestId,
                    await coordinator.ApplyProfileAsync(ParseProfileRequest(request.Payload), requestId, peer, cancellationToken)
                        .ConfigureAwait(false)),
                PipeOperations.CurveSet => await SetCurveAsync(request, peer, cancellationToken).ConfigureAwait(false),
                PipeOperations.DiagnosticsExport => PipeResponse.Success(requestId, coordinator.GetDiagnostics()),
                PipeOperations.ShutdownStatus => PipeResponse.Success(requestId, coordinator.GetShutdownStatus()),
                PipeOperations.ShutdownQualify =>
                    await QualifyShutdownAsync(request, peer, cancellationToken).ConfigureAwait(false),
                PipeOperations.ShutdownEnable =>
                    await EnableShutdownAsync(request, peer, cancellationToken).ConfigureAwait(false),
                PipeOperations.ShutdownDisable =>
                    await DisableShutdownAsync(request, peer, cancellationToken).ConfigureAwait(false),
                PipeOperations.CommissionProbe =>
                    await ProbeAsync(request, peer, cancellationToken).ConfigureAwait(false),
                PipeOperations.CommissionIdempotent =>
                    await IdempotentAsync(request, peer, cancellationToken).ConfigureAwait(false),
                PipeOperations.CommissionTest =>
                    await TestAsync(request, peer, cancellationToken).ConfigureAwait(false),
                PipeOperations.CommissionCommit =>
                    await CommitAsync(request, peer, cancellationToken).ConfigureAwait(false),
                PipeOperations.CommissionRollback =>
                    await RollbackAsync(request, peer, cancellationToken).ConfigureAwait(false),
                PipeOperations.CommissionRecoverTaskTransition =>
                    await RecoverTaskTransitionAsync(request, peer, cancellationToken).ConfigureAwait(false),
                PipeOperations.CommissionAbort =>
                    await AbortAsync(request, peer, cancellationToken).ConfigureAwait(false),
                _ => throw new ControllerOperationException(PipeErrorCodes.InvalidArgument,
                    "Unknown operation."),
            };
        }
        catch (ControllerCoordinator.SafetyOutputSuppressedException exception)
        {
            await AuditFailureAsync(request.Operation, PipeErrorCodes.InvalidArgument, peer, cancellationToken)
                .ConfigureAwait(false);
            return PipeResponse.Failure(requestId, PipeErrorCodes.InvalidArgument, exception.Message);
        }
        catch (ControllerOperationException exception)
        {
            await AuditFailureAsync(request.Operation, exception.Code, peer, cancellationToken).ConfigureAwait(false);
            return PipeResponse.Failure(requestId, exception.Code, exception.Message);
        }
        catch (ConfigurationValidationException exception)
        {
            await AuditFailureAsync(request.Operation, PipeErrorCodes.InvalidArgument, peer, cancellationToken)
                .ConfigureAwait(false);
            return PipeResponse.Failure(requestId, PipeErrorCodes.InvalidArgument, exception.Message);
        }
        catch (V12ProtocolException exception)
        {
            await AuditFailureAsync(request.Operation, PipeErrorCodes.ProtocolChecksum, peer, cancellationToken)
                .ConfigureAwait(false);
            return PipeResponse.Failure(requestId, PipeErrorCodes.ProtocolChecksum, exception.Message);
        }
        catch (Exception exception) when (exception is JsonException or FormatException or ArgumentException)
        {
            await AuditFailureAsync(request.Operation, PipeErrorCodes.InvalidArgument, peer, cancellationToken)
                .ConfigureAwait(false);
            return PipeResponse.Failure(requestId, PipeErrorCodes.InvalidArgument, exception.Message);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Coordinator hardware paths classify expected device/timeout failures as a
            // ControllerOperationException with HID_TIMEOUT. A raw IOException, Win32Exception,
            // UnauthorizedAccessException, or TimeoutException can also come from protected
            // ProgramData, audit, configuration, marker, or Task Scheduler persistence, so this
            // protocol boundary must not mislabel every such failure as a HID fault.
            logger.LogError(exception, "Unhandled API operation failure for {Operation}.", request.Operation);
            await AuditFailureAsync(request.Operation, PipeErrorCodes.InternalError, peer, cancellationToken)
                .ConfigureAwait(false);
            return PipeResponse.Failure(requestId, PipeErrorCodes.InternalError,
                "The service could not complete the request. Export diagnostics for details.");
        }
    }

    private static void ValidateEnvelope(PipeRequest request)
    {
        if (request.ApiVersion != PipeProtocol.ApiVersion)
        {
            throw new ControllerOperationException(PipeErrorCodes.InvalidArgument,
                $"Unsupported apiVersion {request.ApiVersion}; expected {PipeProtocol.ApiVersion}.");
        }

        if (!IsCanonicalRequestId(request.RequestId))
        {
            throw new ControllerOperationException(PipeErrorCodes.InvalidArgument,
                "requestId must be a non-empty UUID in canonical D format.");
        }

        if (string.IsNullOrWhiteSpace(request.Operation) || request.Operation.Length > 64)
        {
            throw new ControllerOperationException(PipeErrorCodes.InvalidArgument,
                "operation is required and must contain at most 64 characters.");
        }

        if (request.Payload.ValueKind != JsonValueKind.Object)
        {
            throw new ControllerOperationException(PipeErrorCodes.InvalidArgument,
                "payload must be a JSON object.");
        }
    }

    private static LightingSettings ParseLighting(JsonElement payload) => new(
        payload.GetProperty("enabled").GetBoolean(), payload.GetProperty("mode").GetInt32(),
        payload.GetProperty("color").GetInt32(), payload.GetProperty("brightness").GetInt32(),
        payload.GetProperty("speed").GetInt32());

    private static byte ParseStartupFanPercent(JsonElement payload)
    {
        if (!payload.GetProperty("percent").TryGetByte(out var percent))
            throw InvalidPayload("percent must be an integer between 10 and 100.");
        return percent;
    }

    private static bool IsCanonicalRequestId(string? requestId) =>
        requestId is { Length: 36 } &&
        Guid.TryParseExact(requestId, "D", out var value) &&
        value != Guid.Empty;

    private static void ValidatePayloadShape(PipeRequest request)
    {
        switch (request.Operation)
        {
            case PipeOperations.StatusGet:
            case PipeOperations.LightingGet:
            case PipeOperations.StartupGet:
            case PipeOperations.CurveGet:
            case PipeOperations.HistoryAnalyze:
            case PipeOperations.DiagnosticsExport:
            case PipeOperations.ShutdownStatus:
            case PipeOperations.ShutdownDisable:
            case PipeOperations.SafetyDisable:
                ValidateExactObject(request.Payload, NoProperties, "payload");
                return;

            case PipeOperations.StartupFanGet:
                ValidateExactObject(request.Payload, NoProperties, "payload");
                return;

            case PipeOperations.SafetyResume:
                ValidateExactObject(request.Payload, SafetyResumeProperties, "payload");
                RequireKind(request.Payload, "confirmed", JsonValueKind.True);
                return;

            case PipeOperations.ModeSet:
                ValidateExactObject(request.Payload, ModeProperties, "payload");
                RequireKind(request.Payload, "mode", JsonValueKind.String);
                return;

            case PipeOperations.LightingSet:
                ValidateLightingPayload(request.Payload);
                return;

            case PipeOperations.ProfileApply:
                _ = ParseProfileRequest(request.Payload);
                return;

            case PipeOperations.SpeedSet:
                ValidateExactObject(request.Payload, SpeedProperties, "payload");
                RequireKind(request.Payload, "percent", JsonValueKind.Number);
                return;

            case PipeOperations.StartupSet:
                ValidateExactObject(request.Payload, StartupProperties, "payload");
                RequireKind(request.Payload, "enabled", JsonValueKind.True, JsonValueKind.False);
                return;

            case PipeOperations.StartupFanSet:
                ValidateExactObject(request.Payload, StartupFanProperties, "payload");
                RequireKind(request.Payload, "percent", JsonValueKind.Number);
                if (!request.Payload.GetProperty("percent").TryGetByte(out var startupPercent) || startupPercent is < 10 or > 100)
                    throw InvalidPayload("percent must be an integer between 10 and 100.");
                return;

            case PipeOperations.PowerSet:
                ValidateExactObject(request.Payload, PowerProperties, "payload");
                RequireKind(request.Payload, "poweredOn", JsonValueKind.True, JsonValueKind.False);
                RequireKind(request.Payload, "confirmed", JsonValueKind.True, JsonValueKind.False);
                return;

            case PipeOperations.CurveSet:
                ValidateCurvePayload(request.Payload);
                return;
            case PipeOperations.CurveAdviceApply:
                ValidateExactObject(request.Payload, ["expected", "proposed"], "payload");
                ValidateCurvePayload(request.Payload.GetProperty("expected"));
                ValidateCurvePayload(request.Payload.GetProperty("proposed"));
                return;

            case PipeOperations.CommissionProbe:
            case PipeOperations.CommissionRecoverTaskTransition:
            case PipeOperations.CommissionRecoverSafety:
            case PipeOperations.ShutdownQualify:
                ValidateExactObject(request.Payload, AttendedProperties, "payload");
                RequireKind(request.Payload, "attended", JsonValueKind.True, JsonValueKind.False);
                return;

            case PipeOperations.CommissionIdempotent:
            case PipeOperations.CommissionCommit:
                ValidateExactObject(request.Payload, AttendedSessionProperties, "payload");
                RequireKind(request.Payload, "attended", JsonValueKind.True, JsonValueKind.False);
                RequireKind(request.Payload, "sessionId", JsonValueKind.String);
                return;

            case PipeOperations.CommissionTest:
                var hasNearFull = request.Payload.ValueKind == JsonValueKind.Object &&
                    request.Payload.TryGetProperty("nearFullOnly", out _);
                ValidateExactObject(request.Payload,
                    hasNearFull ? NearFullTestProperties : AttendedSessionProperties, "payload");
                RequireKind(request.Payload, "attended", JsonValueKind.True, JsonValueKind.False);
                RequireKind(request.Payload, "sessionId", JsonValueKind.String);
                if (hasNearFull)
                {
                    RequireKind(request.Payload, "nearFullOnly", JsonValueKind.True, JsonValueKind.False);
                }
                return;

            case PipeOperations.CommissionAbort:
                ValidateExactObject(request.Payload, SessionProperties, "payload");
                RequireKind(request.Payload, "sessionId", JsonValueKind.String);
                return;

            case PipeOperations.CommissionRollback:
                ValidateExactObject(request.Payload, RollbackProperties, "payload");
                RequireKind(request.Payload, "startMythCool", JsonValueKind.True, JsonValueKind.False);
                RequireKind(request.Payload, "installer", JsonValueKind.True, JsonValueKind.False);
                RequireKind(request.Payload, "attended", JsonValueKind.True, JsonValueKind.False);
                return;

            case PipeOperations.ShutdownEnable:
                ValidateExactObject(request.Payload, ShutdownEnableProperties, "payload");
                RequireKind(request.Payload, "attended", JsonValueKind.True, JsonValueKind.False);
                RequireKind(request.Payload, "qualificationId", JsonValueKind.String);
                return;
        }
    }

    private static ProfileApplyRequest ParseProfileRequest(JsonElement payload)
    {
        var hasConfirmation = payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("confirmResumeControl", out _);
        ValidateExactObject(payload, hasConfirmation ? ["profile", "confirmResumeControl"] : ["profile"], "payload");
        if (hasConfirmation) RequireKind(payload, "confirmResumeControl", JsonValueKind.True, JsonValueKind.False);
        var value = payload.GetProperty("profile");
        ValidateExactObject(value, ["id", "name", "mode", "manualSpeedPercent", "curve", "lighting"], "profile");
        foreach (var name in new[] { "id", "name", "mode" }) RequireKind(value, name, JsonValueKind.String);
        RequireKind(value, "manualSpeedPercent", JsonValueKind.Number);
        if (!value.GetProperty("manualSpeedPercent").TryGetByte(out var percent))
            throw InvalidPayload("manualSpeedPercent must be an integer between 10 and 100.");
        var mode = value.GetProperty("mode").GetString() switch
        {
            "auto" => ControlMode.Auto,
            "manual" => ControlMode.Manual,
            _ => throw InvalidPayload("Profiles support only auto or manual control."),
        };
        ValidateCurvePayload(value.GetProperty("curve"));
        var curve = new FanCurve(Deserialize<CurvePayload>(value.GetProperty("curve")).Points!);
        RequireKind(value, "lighting", JsonValueKind.Null, JsonValueKind.Object);
        LightingSettings? lighting = null;
        if (value.GetProperty("lighting").ValueKind == JsonValueKind.Object)
        {
            ValidateLightingPayload(value.GetProperty("lighting"));
            lighting = ParseLighting(value.GetProperty("lighting"));
        }
        var profile = new ControlProfileSnapshot(value.GetProperty("id").GetString()!, value.GetProperty("name").GetString()!,
            mode, percent, curve, lighting);
        profile.Validate();
        return new ProfileApplyRequest(profile, hasConfirmation && payload.GetProperty("confirmResumeControl").GetBoolean());
    }

    private static void ValidateLightingPayload(JsonElement payload)
    {
        ValidateExactObject(payload, ["enabled", "mode", "color", "brightness", "speed"], "lighting");
        RequireKind(payload, "enabled", JsonValueKind.True, JsonValueKind.False);
        foreach (var name in new[] { "mode", "color", "brightness", "speed" })
        {
            RequireKind(payload, name, JsonValueKind.Number);
            if (!payload.GetProperty(name).TryGetInt32(out _))
                throw InvalidPayload("Lighting values must be integers.");
        }
        ParseLighting(payload).Validate();
    }

    private static void ValidateCurvePayload(JsonElement payload)
    {
        ValidateExactObject(payload, CurveProperties, "payload");
        RequireKind(payload, "points", JsonValueKind.Array);
        var points = payload.GetProperty("points");
        var count = points.GetArrayLength();
        if (count is < 2 or > 20)
        {
            throw InvalidPayload("points must contain between 2 and 20 curve points.");
        }

        foreach (var point in points.EnumerateArray())
        {
            if (point.ValueKind != JsonValueKind.Object)
            {
                throw InvalidPayload("Every curve point must be a JSON object.");
            }

            ValidateExactObject(point, CurvePointProperties, "curve point");
            RequireKind(point, "temperatureCelsius", JsonValueKind.Number);
            RequireKind(point, "fanPercent", JsonValueKind.Number);
        }
    }

    private static void ValidateExactObject(
        JsonElement element,
        IReadOnlyCollection<string> expectedProperties,
        string context)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw InvalidPayload($"{context} must be a JSON object.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!seen.Add(property.Name))
            {
                throw InvalidPayload($"{context} contains a duplicate property.");
            }

            if (expectedProperties.Contains(property.Name, StringComparer.Ordinal))
            {
                continue;
            }

            if (expectedProperties.Contains(property.Name, StringComparer.OrdinalIgnoreCase))
            {
                throw InvalidPayload($"{context} contains a property with non-canonical casing.");
            }

            throw InvalidPayload($"{context} contains an unknown property.");
        }

        foreach (var expected in expectedProperties)
        {
            if (!seen.Contains(expected))
            {
                throw InvalidPayload($"{context} is missing required property '{expected}'.");
            }
        }
    }

    private static void RequireKind(
        JsonElement element,
        string propertyName,
        params JsonValueKind[] allowedKinds)
    {
        var actual = element.GetProperty(propertyName).ValueKind;
        if (!allowedKinds.Contains(actual))
        {
            throw InvalidPayload($"Property '{propertyName}' has an invalid JSON type.");
        }
    }

    private static ControllerOperationException InvalidPayload(string message) =>
        new(PipeErrorCodes.InvalidArgument, message);

    private async Task<PipeResponse> SetModeAsync(
        PipeRequest request,
        PipePeerIdentity peer,
        CancellationToken cancellationToken)
    {
        var payload = Deserialize<ModePayload>(request.Payload);
        var mode = payload.Mode switch
        {
            "auto" => ControlMode.Auto,
            "manual" => ControlMode.Manual,
            "puremanual" => ControlMode.PureManual,
            "released" => ControlMode.Released,
            _ => throw new ControllerOperationException(PipeErrorCodes.InvalidArgument,
                "mode must be auto or manual."),
        };
        await coordinator.SetModeAsync(mode, peer, cancellationToken).ConfigureAwait(false);
        return PipeResponse.Success(request.RequestId, coordinator.GetStatus());
    }

    private async Task<PipeResponse> SetSpeedAsync(
        PipeRequest request,
        PipePeerIdentity peer,
        CancellationToken cancellationToken)
    {
        var payload = Deserialize<SpeedPayload>(request.Payload);
        if (payload.Percent is < 10 or > 100)
        {
            throw new ControllerOperationException(PipeErrorCodes.InvalidArgument,
                "percent must be between 10 and 100.");
        }

        await coordinator.SetManualSpeedAsync(checked((byte)payload.Percent), peer, cancellationToken)
            .ConfigureAwait(false);
        return PipeResponse.Success(request.RequestId, coordinator.GetStatus());
    }

    private async Task<PipeResponse> SetCurveAsync(
        PipeRequest request,
        PipePeerIdentity peer,
        CancellationToken cancellationToken)
    {
        var payload = Deserialize<CurvePayload>(request.Payload);
        var curve = new FanCurve(payload.Points ?? []);
        await coordinator.SetCurveAsync(curve, peer, cancellationToken).ConfigureAwait(false);
        return PipeResponse.Success(request.RequestId, curve);
    }

    private async Task<PipeResponse> ApplyCurveAdviceAsync(PipeRequest request, PipePeerIdentity peer, CancellationToken cancellationToken)
    {
        FanCurve Read(string name) => new(Deserialize<CurvePayload>(request.Payload.GetProperty(name)).Points
            ?? throw new ArgumentException("Curve points are required."));
        await coordinator.ApplyCurveAdviceAsync(Read("expected"), Read("proposed"), peer, cancellationToken).ConfigureAwait(false);
        return PipeResponse.Success(request.RequestId, coordinator.GetStatus());
    }

    private async Task<PipeResponse> ProbeAsync(
        PipeRequest request,
        PipePeerIdentity peer,
        CancellationToken cancellationToken)
    {
        var payload = Deserialize<CommissionPayload>(request.Payload);
        var result = await coordinator.ProbeCommissioningAsync(payload.Attended, peer, cancellationToken)
            .ConfigureAwait(false);
        return PipeResponse.Success(request.RequestId, result);
    }

    private async Task<PipeResponse> IdempotentAsync(
        PipeRequest request,
        PipePeerIdentity peer,
        CancellationToken cancellationToken)
    {
        var payload = RequireSession(Deserialize<CommissionPayload>(request.Payload));
        var result = await coordinator.VerifyIdempotentAsync(
            payload.SessionId!.Value, payload.Attended, peer, cancellationToken).ConfigureAwait(false);
        return PipeResponse.Success(request.RequestId, result);
    }

    private async Task<PipeResponse> TestAsync(
        PipeRequest request,
        PipePeerIdentity peer,
        CancellationToken cancellationToken)
    {
        var payload = RequireSession(Deserialize<CommissionPayload>(request.Payload));
        var result = await coordinator.TestCommissioningAsync(
            payload.SessionId!.Value, payload.Attended, peer, cancellationToken, payload.NearFullOnly).ConfigureAwait(false);
        return PipeResponse.Success(request.RequestId, result);
    }

    private async Task<PipeResponse> CommitAsync(
        PipeRequest request,
        PipePeerIdentity peer,
        CancellationToken cancellationToken)
    {
        var payload = RequireSession(Deserialize<CommissionPayload>(request.Payload));
        var result = await coordinator.CommitCommissioningAsync(
            payload.SessionId!.Value, payload.Attended, peer, cancellationToken).ConfigureAwait(false);
        return PipeResponse.Success(request.RequestId, result);
    }

    private async Task<PipeResponse> RollbackAsync(
        PipeRequest request,
        PipePeerIdentity peer,
        CancellationToken cancellationToken)
    {
        var payload = Deserialize<RollbackPayload>(request.Payload);
        if (payload.Installer && !string.Equals(peer.CallerSid, "S-1-5-18", StringComparison.Ordinal))
        {
            throw new ControllerOperationException(PipeErrorCodes.InvalidArgument,
                "installer=true is reserved for a LocalSystem uninstall action.");
        }

        var result = await coordinator.RollbackAsync(
            payload.StartMythCool, payload.Installer, payload.Attended, peer, cancellationToken)
            .ConfigureAwait(false);
        return PipeResponse.Success(request.RequestId, result);
    }

    private async Task<PipeResponse> AbortAsync(
        PipeRequest request,
        PipePeerIdentity peer,
        CancellationToken cancellationToken)
    {
        var payload = RequireSession(Deserialize<CommissionPayload>(request.Payload));
        var result = await coordinator.AbortCommissioningAsync(
            payload.SessionId!.Value, peer, cancellationToken).ConfigureAwait(false);
        return PipeResponse.Success(request.RequestId, result);
    }

    private async Task<PipeResponse> RecoverTaskTransitionAsync(
        PipeRequest request,
        PipePeerIdentity peer,
        CancellationToken cancellationToken)
    {
        var payload = Deserialize<RecoveryPayload>(request.Payload);
        if (!payload.Attended)
        {
            throw new ControllerOperationException(
                PipeErrorCodes.InvalidArgument,
                "commission.recover-task-transition requires attended=true.");
        }

        var result = await coordinator.RecoverTaskTransitionAsync(
            payload.Attended, peer, cancellationToken).ConfigureAwait(false);
        return PipeResponse.Success(request.RequestId, result);
    }

    private async Task<PipeResponse> QualifyShutdownAsync(
        PipeRequest request,
        PipePeerIdentity peer,
        CancellationToken cancellationToken)
    {
        var payload = Deserialize<ShutdownQualificationPayload>(request.Payload);
        var result = await coordinator.QualifySystemShutdownOffAsync(
            payload.Attended, peer, cancellationToken).ConfigureAwait(false);
        return PipeResponse.Success(request.RequestId, result);
    }

    private async Task<PipeResponse> DisableShutdownAsync(
        PipeRequest request,
        PipePeerIdentity peer,
        CancellationToken cancellationToken)
    {
        _ = Deserialize<EmptyPayload>(request.Payload);
        var result = await coordinator.DisableSystemShutdownOffAsync(peer, cancellationToken)
            .ConfigureAwait(false);
        return PipeResponse.Success(request.RequestId, result);
    }

    private async Task<PipeResponse> EnableShutdownAsync(
        PipeRequest request,
        PipePeerIdentity peer,
        CancellationToken cancellationToken)
    {
        var payload = Deserialize<ShutdownEnablePayload>(request.Payload);
        if (!payload.Attended || payload.QualificationId is null ||
            payload.QualificationId == Guid.Empty)
        {
            throw new ControllerOperationException(
                PipeErrorCodes.InvalidArgument,
                "shutdown.enable requires attended=true and a valid qualificationId.");
        }

        var result = await coordinator.EnableSystemShutdownOffAsync(
                payload.QualificationId.Value,
                payload.Attended,
                peer,
                cancellationToken)
            .ConfigureAwait(false);
        return PipeResponse.Success(request.RequestId, result);
    }

    private async Task AuditFailureAsync(
        string operation,
        string code,
        PipePeerIdentity peer,
        CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        try
        {
            await auditLog.WriteAsync(
                    operation,
                    code,
                    peer.CallerSid,
                    peer.Source,
                    cancellationToken: CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Could not write the API failure audit record.");
        }
    }

    private static T Deserialize<T>(JsonElement payload) where T : class
    {
        if (payload.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            throw new ControllerOperationException(
                PipeErrorCodes.InvalidArgument,
                "Request payload is required.");
        }

        return payload.Deserialize<T>(JsonOptions)
            ?? throw new ControllerOperationException(PipeErrorCodes.InvalidArgument, "Request payload is required.");
    }

    private static CommissionPayload RequireSession(CommissionPayload payload)
    {
        if (payload.SessionId is null || payload.SessionId == Guid.Empty)
        {
            throw new ControllerOperationException(PipeErrorCodes.InvalidArgument,
                "A valid commissioning sessionId is required.");
        }

        return payload;
    }

    private sealed record ModePayload(string? Mode);
    private sealed record SpeedPayload(int Percent);
    private sealed record CurvePayload(IReadOnlyList<FanCurvePoint>? Points);
    private sealed record CommissionPayload(bool Attended, Guid? SessionId, bool NearFullOnly = false);
    private sealed record RollbackPayload(bool StartMythCool, bool Installer, bool Attended);
    private sealed record RecoveryPayload(bool Attended);
    private sealed record ShutdownQualificationPayload(bool Attended);
    private sealed record ShutdownEnablePayload(bool Attended, Guid? QualificationId);
    private sealed record EmptyPayload;
}
