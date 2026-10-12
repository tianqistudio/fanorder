using V12UltraController.Core.Configuration;
using V12UltraController.Core.Thermal;

namespace V12UltraController.Core.Control;

public enum ControlMode
{
    Auto,
    Manual,
    PureManual,
    Released,
}

public enum FailSafeReason
{
    None,
    NotCommissioned,
    MythCoolConflict,
    DeviceUnavailable,
    UnsupportedDevice,
    ControllerStalled,
    HidFailure,
    TelemetryStale,
    ConfigurationInvalid,
    StartupWarmup,
    DeviceReconnected,
    Recovering,
    EmergencyTemperature,
}

public sealed record ControlPolicyOptions
{
    public int StartupValidSamples { get; init; } = 10;

    public TimeSpan TelemetryStaleAfter { get; init; } = TimeSpan.FromSeconds(10);

    public TimeSpan FaultRecoveryDuration { get; init; } = TimeSpan.FromSeconds(10);

    public double EmergencyTemperatureCelsius { get; init; } = 90;

    public double EmergencyRecoveryBelowCelsius { get; init; } = 85;

    public TimeSpan EmergencyRecoveryDuration { get; init; } = TimeSpan.FromSeconds(60);

    public TimeSpan DownshiftWindow { get; init; } = TimeSpan.FromSeconds(30);

    public byte MinimumWriteDeltaPercent { get; init; } = 2;

    public TimeSpan ReaffirmInterval { get; init; } = TimeSpan.FromSeconds(30);

    public byte FailSafePercent { get; init; } = 100;

    public void Validate()
    {
        if (StartupValidSamples < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(StartupValidSamples));
        }

        if (TelemetryStaleAfter <= TimeSpan.Zero
            || FaultRecoveryDuration < TimeSpan.Zero
            || EmergencyRecoveryDuration < TimeSpan.Zero
            || DownshiftWindow < TimeSpan.Zero
            || ReaffirmInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(ControlPolicyOptions), "Policy durations are invalid.");
        }

        if (EmergencyTemperatureCelsius is < 0 or > 120
            || EmergencyRecoveryBelowCelsius is < 0 or > 120
            || EmergencyRecoveryBelowCelsius >= EmergencyTemperatureCelsius)
        {
            throw new ArgumentOutOfRangeException(nameof(ControlPolicyOptions), "Emergency thresholds are invalid.");
        }

        if (MinimumWriteDeltaPercent is < 1 or > 100 || FailSafePercent != 100)
        {
            throw new ArgumentOutOfRangeException(nameof(ControlPolicyOptions), "Fan-control thresholds are invalid.");
        }
    }
}

public sealed record ControlInput(
    DateTimeOffset Now,
    ControllerConfiguration Configuration,
    ThermalSnapshot Thermal,
    bool DeviceConnected,
    bool ProtocolCompatible,
    bool CommunicationHealthy,
    TimeSpan MonotonicNow,
    long ThermalSampleSequence,
    bool DeviceReconnected = false,
    bool ControlLoopHealthy = true,
    bool MythCoolConflict = false);

public sealed record ControlDecision(
    byte RequestedPercent,
    bool ShouldWrite,
    bool FailSafeActive,
    FailSafeReason FailSafeReason,
    DateTimeOffset? FailSafeSince,
    double? EffectiveTemperatureCelsius);

public sealed class ControlPolicy
{
    private readonly ControlPolicyOptions _options;
    private readonly Queue<(TimeSpan Timestamp, double Temperature)> _recentTemperatures = new();
    private long? _lastThermalSampleSequence;
    private int _startupValidSamples;
    private bool _startupComplete;
    private bool _recoveringFromFault;
    private TimeSpan? _healthySince;
    private int _recoveryValidSamples;
    private bool _emergencyActive;
    private TimeSpan? _emergencyBelowSince;
    private DateTimeOffset? _failSafeSince;
    private FailSafeReason _rememberedFault = FailSafeReason.StartupWarmup;
    private byte? _lastSuccessfulPercent;
    private TimeSpan? _lastSuccessfulWriteAt;
    private byte? _observedManualPercent;

    public ControlPolicy(ControlPolicyOptions? options = null)
    {
        _options = options ?? new ControlPolicyOptions();
        _options.Validate();
    }

    // An explicit new manual target bypasses delta/reaffirm delays without
    // re-entering startup warmup or clearing the independent thermal latches.
    public void RequestManualWrite() { _lastSuccessfulPercent = null; _observedManualPercent = null; }

    public void ObserveManualTarget(byte percent) => _observedManualPercent = percent;

    internal bool IsSettledForSystemShutdownOff =>
        _startupComplete &&
        _lastSuccessfulPercent is not null &&
        !_recoveringFromFault &&
        !_emergencyActive &&
        _failSafeSince is null;

    public ControlDecision Evaluate(ControlInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(input.Configuration);
        ArgumentNullException.ThrowIfNull(input.Thermal);

        var configurationValid = TryValidateConfiguration(input.Configuration);
        if (configurationValid && input.Configuration.Mode is ControlMode.PureManual or ControlMode.Released)
            return new ControlDecision(input.Configuration.ManualSpeedPercent, false, false, FailSafeReason.None, null,
                input.Thermal.ControlCelsius);
        var telemetryValid = input.Thermal.CpuIsValid
            && input.Thermal.ControlCelsius is not null
            && IsFresh(input.Now, input.Thermal.CapturedAt);
        // UTC is used only to reject stale/future telemetry. A coordinator-assigned monotonic
        // acquisition sequence distinguishes genuinely new reads from a replay without making
        // startup or recovery wait for the wall clock to catch up after an NTP/manual rollback.
        // Every currently valid observation still contributes to the monotonic high-water
        // window: dropping a hotter reading here would delay an otherwise immediate upshift.
        var advancesHealthLatches = telemetryValid &&
                                    input.ThermalSampleSequence > 0 &&
                                    (_lastThermalSampleSequence is null ||
                                     input.ThermalSampleSequence > _lastThermalSampleSequence);
        if (advancesHealthLatches)
        {
            _lastThermalSampleSequence = input.ThermalSampleSequence;
        }

        var currentTemperature = telemetryValid ? input.Thermal.ControlCelsius : null;
        if (currentTemperature is { } observedTemperature)
        {
            AddTemperature(input.MonotonicNow, observedTemperature);
        }
        else
        {
            TrimTemperatures(input.MonotonicNow);
        }

        UpdateEmergencyState(input.MonotonicNow, currentTemperature);

        var immediateFault = GetImmediateFault(input, configurationValid, telemetryValid);
        if (immediateFault != FailSafeReason.None)
        {
            EnterFault(input.Now, immediateFault);
        }
        else if (_emergencyActive)
        {
            EnterFailSafe(input.Now, FailSafeReason.EmergencyTemperature);
        }
        else if (!_startupComplete)
        {
            if (advancesHealthLatches)
            {
                _startupValidSamples++;
            }

            if (_startupValidSamples >= _options.StartupValidSamples)
            {
                _startupComplete = true;
                ExitFailSafe();
            }
            else
            {
                EnterFailSafe(input.Now, FailSafeReason.StartupWarmup);
            }
        }
        else if (_recoveringFromFault)
        {
            if (advancesHealthLatches)
            {
                _healthySince ??= input.MonotonicNow;
                _recoveryValidSamples++;
            }

            if (advancesHealthLatches
                && _healthySince is not null
                && _recoveryValidSamples >= _options.StartupValidSamples
                && input.MonotonicNow - _healthySince >= _options.FaultRecoveryDuration)
            {
                _recoveringFromFault = false;
                _healthySince = null;
                _recoveryValidSamples = 0;
                ExitFailSafe();
            }
            else
            {
                EnterFailSafe(input.Now, FailSafeReason.Recovering);
            }
        }
        else
        {
            ExitFailSafe();
        }

        var failSafeReason = _failSafeSince is null ? FailSafeReason.None : _rememberedFault;
        var requested = _failSafeSince is not null
            ? _options.FailSafePercent
            : CalculateConfiguredTarget(input.Configuration, GetEffectiveTemperature());
        var canWrite = input.Configuration.Commissioned
            && input.DeviceConnected
            && input.ProtocolCompatible
            && !input.MythCoolConflict;
        var observedManualAlreadyApplied = input.Configuration.Mode == ControlMode.Manual &&
            _failSafeSince is null && _observedManualPercent == requested;
        var shouldWrite = canWrite && !observedManualAlreadyApplied &&
            ShouldWrite(requested, input.MonotonicNow, _failSafeSince is not null);

        return new ControlDecision(
            requested,
            shouldWrite,
            _failSafeSince is not null,
            failSafeReason,
            _failSafeSince,
            GetEffectiveTemperature());
    }

    public void RecordWriteSuccess(TimeSpan monotonicTimestamp, byte writtenPercent)
    {
        if (writtenPercent is < 10 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(writtenPercent));
        }

        _lastSuccessfulPercent = writtenPercent;
        if (_observedManualPercent != writtenPercent) _observedManualPercent = null;
        _lastSuccessfulWriteAt = monotonicTimestamp;
    }

    public void Reset()
    {
        _observedManualPercent = null;
        _recentTemperatures.Clear();
        _lastThermalSampleSequence = null;
        _startupValidSamples = 0;
        _startupComplete = false;
        _recoveringFromFault = false;
        _healthySince = null;
        _recoveryValidSamples = 0;
        _emergencyActive = false;
        _emergencyBelowSince = null;
        _failSafeSince = null;
        _rememberedFault = FailSafeReason.StartupWarmup;
        _lastSuccessfulPercent = null;
        _lastSuccessfulWriteAt = null;
    }

    private static bool TryValidateConfiguration(ControllerConfiguration configuration)
    {
        try
        {
            configuration.Validate();
            return true;
        }
        catch (ConfigurationValidationException)
        {
            return false;
        }
    }

    private FailSafeReason GetImmediateFault(ControlInput input, bool configurationValid, bool telemetryValid)
    {
        if (!configurationValid)
        {
            return FailSafeReason.ConfigurationInvalid;
        }

        if (!input.Configuration.Commissioned)
        {
            return FailSafeReason.NotCommissioned;
        }

        if (input.MythCoolConflict)
        {
            return FailSafeReason.MythCoolConflict;
        }

        if (!input.DeviceConnected)
        {
            return FailSafeReason.DeviceUnavailable;
        }

        if (!input.ProtocolCompatible)
        {
            return FailSafeReason.UnsupportedDevice;
        }

        if (!input.ControlLoopHealthy)
        {
            return FailSafeReason.ControllerStalled;
        }

        if (!input.CommunicationHealthy)
        {
            return FailSafeReason.HidFailure;
        }

        if (!telemetryValid)
        {
            return FailSafeReason.TelemetryStale;
        }

        if (input.DeviceReconnected)
        {
            return FailSafeReason.DeviceReconnected;
        }

        return FailSafeReason.None;
    }

    private void EnterFault(DateTimeOffset now, FailSafeReason reason)
    {
        EnterFailSafe(now, reason);
        _startupValidSamples = 0;
        _healthySince = null;
        _recoveryValidSamples = 0;
        if (_startupComplete)
        {
            _recoveringFromFault = true;
        }
    }

    private void EnterFailSafe(DateTimeOffset now, FailSafeReason reason)
    {
        _failSafeSince ??= now;
        _rememberedFault = reason;
    }

    private void ExitFailSafe()
    {
        _failSafeSince = null;
        _rememberedFault = FailSafeReason.None;
    }

    private bool IsFresh(DateTimeOffset now, DateTimeOffset capturedAt)
    {
        var age = now - capturedAt;
        // A future timestamp is not evidence of a fresh sensor update.  Accepting even a
        // small clock lead would let a malformed or stale sample count toward startup and
        // fault-recovery latches, so fail closed until a newly captured sample is observed.
        return age >= TimeSpan.Zero && age <= _options.TelemetryStaleAfter;
    }

    private void UpdateEmergencyState(TimeSpan now, double? currentTemperature)
    {
        if (currentTemperature >= _options.EmergencyTemperatureCelsius)
        {
            _emergencyActive = true;
            _emergencyBelowSince = null;
            return;
        }

        if (!_emergencyActive)
        {
            return;
        }

        if (currentTemperature is null)
        {
            // Recovery requires continuously valid evidence that every observed
            // temperature remains below the threshold. A telemetry gap resets it.
            _emergencyBelowSince = null;
            return;
        }

        if (currentTemperature < _options.EmergencyRecoveryBelowCelsius)
        {
            _emergencyBelowSince ??= now;
            if (now - _emergencyBelowSince >= _options.EmergencyRecoveryDuration)
            {
                _emergencyActive = false;
                _emergencyBelowSince = null;
            }
        }
        else
        {
            _emergencyBelowSince = null;
        }
    }

    private void AddTemperature(TimeSpan now, double temperature)
    {
        _recentTemperatures.Enqueue((now, temperature));
        TrimTemperatures(now);
    }

    private void TrimTemperatures(TimeSpan now)
    {
        while (_recentTemperatures.TryPeek(out var sample)
               && now - sample.Timestamp > _options.DownshiftWindow)
        {
            _recentTemperatures.Dequeue();
        }
    }

    private double? GetEffectiveTemperature() => _recentTemperatures.Count == 0
        ? null
        : _recentTemperatures.Max(static sample => sample.Temperature);

    private static byte CalculateConfiguredTarget(ControllerConfiguration configuration, double? effectiveTemperature)
    {
        if (configuration.Mode == ControlMode.Manual)
        {
            return configuration.ManualSpeedPercent;
        }

        if (effectiveTemperature is null)
        {
            return 100;
        }

        var target = configuration.Curve.Interpolate(effectiveTemperature.Value);
        return checked((byte)Math.Clamp(Math.Round(target, MidpointRounding.AwayFromZero), 10, 100));
    }

    private bool ShouldWrite(byte requested, TimeSpan now, bool failSafeActive)
    {
        if (_lastSuccessfulPercent is null || _lastSuccessfulWriteAt is null)
        {
            return true;
        }

        // The normal write delta must never delay a safety escalation from 99% to 100%.
        // Keep requesting it until a successful safety write has been recorded.
        return (failSafeActive && _lastSuccessfulPercent.Value != _options.FailSafePercent)
            || Math.Abs(requested - _lastSuccessfulPercent.Value) >= _options.MinimumWriteDeltaPercent
            || now - _lastSuccessfulWriteAt >= _options.ReaffirmInterval;
    }
}
