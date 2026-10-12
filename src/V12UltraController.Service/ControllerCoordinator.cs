using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using V12UltraController.Core.Configuration;
using V12UltraController.Core.Control;
using V12UltraController.Core.Hardware;
using V12UltraController.Core.Ipc;
using V12UltraController.Core.Security;
using V12UltraController.Core.Status;
using V12UltraController.Core.Thermal;

namespace V12UltraController.Service;

internal enum HostStopDisposition
{
    None,
    Finalizing,
    KeepAt100Confirmed,
    KeepAt100Unconfirmed,
    SystemShutdownOffConfirmed,
}

internal sealed partial class ControllerCoordinator : IAsyncDisposable
{
    private const int CommissioningRequiredValidSamples = 10;
    private static readonly TimeSpan HidTimeout = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan ThermalTimeout = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan CacheIoTimeout = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan CacheWriterStopBudget = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan HostStopFinalizationBudget = TimeSpan.FromMilliseconds(1250);
    private static readonly TimeSpan ShutdownQualificationTransactionBudget = TimeSpan.FromSeconds(5);
    private static readonly HashSet<string> ConfigurationMutationEvents = new(StringComparer.Ordinal)
    {
        PipeOperations.ModeSet,
        PipeOperations.SpeedSet,
        PipeOperations.CurveSet,
        PipeOperations.CurveAdviceApply,
        PipeOperations.ProfileApply,
        PipeOperations.StartupFanSet,
        PipeOperations.ShutdownEnable,
        PipeOperations.ShutdownDisable,
    };
    private static readonly HashSet<string> SafetyOperationEvents = new(StringComparer.Ordinal)
    {
        PipeOperations.PowerSet,
        PipeOperations.LightingSet,
        PipeOperations.CommissionProbe,
        PipeOperations.CommissionIdempotent,
        PipeOperations.CommissionTest,
        PipeOperations.CommissionAbort,
        "commission.safety-abort",
        PipeOperations.ShutdownQualify,
    };
    private static readonly DeviceIdentity SupportedDeviceIdentity = new(
        DeviceAllowList.V12UltraVendorId,
        DeviceAllowList.V12UltraProductId,
        DeviceAllowList.V12UltraFirmwareVersion,
        DeviceAllowList.V12UltraUsagePage,
        DeviceAllowList.V12UltraUsage);
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly object hostStopSync = new();
    private readonly object deviceStateCacheSync = new();
    private readonly IConfigurationStore configurationStore;
    private readonly ServiceRuntimeStateStore runtimeStateStore;
    private readonly IDeviceStateCacheStore deviceStateCacheStore;
    private readonly IV12DeviceLocator deviceLocator;
    private readonly IThermalSource thermalSource;
    private readonly HidIoDispatcher hidDispatcher;
    private readonly ThermalIoDispatcher thermalDispatcher;
    private readonly ControlPolicy policy;
    private readonly IMythCoolConflictDetector conflictDetector;
    private readonly SingleWriterLease writerLease;
    private readonly IMythCoolScheduledTaskManager taskManager;
    private readonly TakeoverTransitionJournalStore transitionJournalStore;
    private readonly AuditMutationJournalStore auditMutationJournalStore;
    private readonly SafetyOperationAuditJournalStore safetyOperationAuditJournalStore;
    private readonly IForcedUninstallRecoveryMarkerStore forcedUninstallRecoveryMarkerStore;
    private readonly JsonlAuditLog auditLog;
    private readonly ServiceStartupSettings startupSettings;
    private readonly TimeProvider timeProvider;
    private readonly long monotonicOriginTimestamp;
    private readonly ILogger<ControllerCoordinator> logger;
    private readonly Task deviceStateCachePreload;
    private readonly Guid abandonedOpenRetentionScope = Guid.NewGuid();

    private ControllerConfiguration configuration = ControllerConfiguration.CreateDefault();
    private ServiceRuntimeState runtimeState = ServiceRuntimeState.Initial;
    private IV12Device? device;
    private V12DeviceState? lastDeviceState;
    private ThermalSnapshot lastThermal = new(DateTimeOffset.MinValue, null, []);
    private long lastThermalSampleSequence;
    private long? lastCommissioningBackgroundSampleSequence;
    private int commissioningValidCpuSamples;
    private ControlDecision? lastDecision;
    private CommissioningSession? commissioningSession;
    private ShutdownQualificationPending? shutdownQualificationPending;
    private ControllerStatus status = CreateInitialStatus();
    private string? configurationError;
    private string? deviceStateCacheError;
    private bool hardwareAccessBlocked;
    private bool invalidConfigurationWithPendingJournal;
    private bool auditMutationBlocked;
    private string? auditMutationError;
    private AuditMutationJournal? activeAuditMutation;
    private bool safetyOperationAuditBlocked;
    private string? safetyOperationAuditError;
    private SafetyOperationAuditJournal? activeSafetyOperationAudit;
    private ForcedUninstallRecoveryMarker? forcedUninstallRecoveryMarker;
    private string? forcedUninstallRecoveryMarkerError;
    private bool configurationLoadInvalid;
    private bool runtimeStateLoadInvalid;
    private bool commissioningStateMismatch;
    private bool requiresProcessRestartForRecovery;
    private bool everOpened;
    private bool deviceWasMissing;
    private long lastHeartbeatUtcTicks;
    private long lastHeartbeatTimestamp;
    private DateTimeOffset? lastSuccessfulRead;
    private DateTimeOffset? lastSuccessfulWrite;
    private TimeSpan? lastSuccessfulWriteMonotonic;
    private byte? lastWrittenPercent;
    private HardwarePoisonState? hardwarePoison;
    private Task? hostStopFinalization;
    private int hostStopDisposition;
    private bool initialized;
    // Consumed after the first verified device open in this service process. This
    // makes the startup kick one-shot and prevents reconnects from overriding a
    // later physical or UI manual adjustment.
    private bool startupFanKickPending = true;
    private int disposeRequested;
    private PendingDeviceStateCacheWrite? pendingDeviceStateCacheWrite;
    private DeviceStateCacheSnapshot? deviceStateCacheSnapshot;
    private Task deviceStateCacheWriter = Task.CompletedTask;
    private bool deviceStateCacheWriterRunning;
    private bool deviceStateCacheWriterStopping;

    public ControllerCoordinator(
        IConfigurationStore configurationStore,
        ServiceRuntimeStateStore runtimeStateStore,
        IDeviceStateCacheStore deviceStateCacheStore,
        IV12DeviceLocator deviceLocator,
        IThermalSource thermalSource,
        HidIoDispatcher hidDispatcher,
        ThermalIoDispatcher thermalDispatcher,
        ControlPolicy policy,
        IMythCoolConflictDetector conflictDetector,
        SingleWriterLease writerLease,
        IMythCoolScheduledTaskManager taskManager,
        TakeoverTransitionJournalStore transitionJournalStore,
        AuditMutationJournalStore auditMutationJournalStore,
        SafetyOperationAuditJournalStore safetyOperationAuditJournalStore,
        IForcedUninstallRecoveryMarkerStore forcedUninstallRecoveryMarkerStore,
        JsonlAuditLog auditLog,
        TimeProvider timeProvider,
        ILogger<ControllerCoordinator> logger,
        ISafetyProtectionStore safetyProtectionStore,
        IServiceStartupRegistration? startupRegistration = null)
    {
        startupSettings = new ServiceStartupSettings(startupRegistration ?? new WindowsServiceStartupRegistration(), auditLog);
        this.configurationStore = configurationStore;
        this.safetyProtectionStore = safetyProtectionStore;
        this.runtimeStateStore = runtimeStateStore;
        this.deviceStateCacheStore = deviceStateCacheStore;
        this.deviceLocator = deviceLocator;
        this.thermalSource = thermalSource;
        this.hidDispatcher = hidDispatcher;
        this.thermalDispatcher = thermalDispatcher;
        this.policy = policy;
        this.conflictDetector = conflictDetector;
        this.writerLease = writerLease;
        this.taskManager = taskManager;
        this.transitionJournalStore = transitionJournalStore;
        this.auditMutationJournalStore = auditMutationJournalStore;
        this.safetyOperationAuditJournalStore = safetyOperationAuditJournalStore ??
            throw new ArgumentNullException(nameof(safetyOperationAuditJournalStore));
        this.forcedUninstallRecoveryMarkerStore =
            forcedUninstallRecoveryMarkerStore ??
            throw new ArgumentNullException(nameof(forcedUninstallRecoveryMarkerStore));
        this.auditLog = auditLog;
        this.timeProvider = timeProvider;
        monotonicOriginTimestamp = timeProvider.GetTimestamp();
        this.logger = logger;
        MarkHeartbeat();
        deviceStateCachePreload = Task.Run(PreloadDeviceStateCacheAsync);
    }

    public DateTimeOffset LastHeartbeatUtc => new(Interlocked.Read(ref lastHeartbeatUtcTicks), TimeSpan.Zero);
    public bool IsCommissioned => configuration.Commissioned;
    public bool IsHardwarePoisoned => Volatile.Read(ref hardwarePoison) is not null;
    public bool RequiresControlHeartbeat =>
        IsCommissioned || Volatile.Read(ref commissioningSession) is not null || writerLease.IsHeld;
    public bool IsWatchdogArmed =>
        IsHardwarePoisoned || RequiresControlHeartbeat ||
        hidDispatcher.HasPendingOperations || thermalDispatcher.HasPendingOperations;
    internal HostStopDisposition HostStopDisposition =>
        (HostStopDisposition)Volatile.Read(ref hostStopDisposition);
    internal int RetainedAbandonedOpenDeviceCount =>
        AbandonedOpenResourceRoot.CountCapturedDevices(abandonedOpenRetentionScope);
    public bool HasStalledControlLoop(TimeSpan threshold) =>
        timeProvider.GetElapsedTime(
            Interlocked.Read(ref lastHeartbeatTimestamp),
            timeProvider.GetTimestamp()) > threshold;

    public bool HasStalledHardwareOperation() =>
        hidDispatcher.HasStalledOperation(HidTimeout) ||
        thermalDispatcher.HasStalledOperation(TimeSpan.FromSeconds(2));
    public ControllerStatus GetStatus()
    {
        var current = Volatile.Read(ref status);
        var poison = Volatile.Read(ref hardwarePoison);
        return (poison is null ? current : ProjectHardwarePoison(current, poison)) with
        { SafetyProtection = ReadSafetyProtection() };
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (initialized)
            {
                return;
            }

            // This is deliberately the first durable-state read performed by service
            // initialization. A retained forced-uninstall marker means a previous installer
            // explicitly skipped verified rollback; neither takeover recovery, Task Scheduler,
            // nor HID may be touched until an attended rollback proves the boundary safe.
            try
            {
                forcedUninstallRecoveryMarker =
                    await forcedUninstallRecoveryMarkerStore.LoadAsync(cancellationToken)
                        .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                forcedUninstallRecoveryMarkerError =
                    "The forced-uninstall recovery marker is unreadable or invalid " +
                    $"({exception.GetType().Name}). It was preserved and requires manual repair.";
                hardwareAccessBlocked = true;
                logger.LogCritical(
                    exception,
                    "The forced-uninstall recovery marker could not be validated. " +
                    "No takeover recovery, Task Scheduler access, or HID access will be attempted.");
            }

            await LoadSafetyProtectionAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                runtimeState = await runtimeStateStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                runtimeStateLoadInvalid = true;
                runtimeState = ServiceRuntimeState.Initial;
                configurationError =
                    $"The service runtime state is unreadable or invalid ({exception.GetType().Name}).";
                hardwareAccessBlocked = true;
                logger.LogCritical(exception,
                    "The service runtime state could not be validated; Task Scheduler and HID recovery are blocked.");
            }
            var configurationLoadFailed = false;
            try
            {
                configuration = await configurationStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (ConfigurationValidationException exception)
            {
                configurationLoadFailed = true;
                configuration = ControllerConfiguration.CreateDefault() with
                {
                    Commissioned = runtimeState.Commissioned,
                };
                configurationError ??= exception.Message;
                logger.LogError(exception,
                    "Controller configuration is invalid; a known commissioned owner will use 100% fail-safe only.");
            }

            configurationLoadInvalid = configurationLoadFailed;
            // Arm only from the startup configuration, never from a later mode
            // change. Pure manual resumes its saved target once if the fan is off.
            Volatile.Write(ref startupPureManualRecoveryAllowed,
                !configurationLoadInvalid && configuration.Mode == ControlMode.PureManual &&
                ReadSafetyProtection().PersistenceError is null ? 1 : 0);

            if (HasForcedUninstallRecoveryBarrier)
            {
                // Do not recover a mutation here: the marker is the earlier, stronger ownership
                // barrier. Read the WAL only so the explicit rollback path can refuse to bypass it.
                try
                {
                    activeAuditMutation = await auditMutationJournalStore.LoadAsync(cancellationToken)
                        .ConfigureAwait(false);
                    if (activeAuditMutation is not null)
                    {
                        LatchAuditMutationBlocked(
                            new InvalidDataException(
                                "A durable audit mutation is pending behind a forced-uninstall marker."),
                            activeAuditMutation,
                            "forced-uninstall startup validation");
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    LatchAuditMutationBlocked(
                        exception,
                        journal: null,
                        "forced-uninstall audit-mutation validation");
                }
            }
            else if (configurationLoadFailed)
            {
                try
                {
                    activeAuditMutation = await auditMutationJournalStore.LoadAsync(cancellationToken)
                        .ConfigureAwait(false);
                    if (activeAuditMutation is not null)
                    {
                        LatchAuditMutationBlocked(
                            new InvalidDataException(
                                "The controller configuration is invalid while an audit mutation barrier is present."),
                            activeAuditMutation,
                            "startup configuration validation");
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    LatchAuditMutationBlocked(
                        exception,
                        journal: null,
                        "startup audit-mutation journal validation");
                }
            }
            else
            {
                try
                {
                    await RecoverPendingAuditMutationAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    LatchAuditMutationBlocked(
                        exception,
                        activeAuditMutation,
                        "startup audit-mutation recovery");
                }
            }

            var pendingJournalExists = !HasForcedUninstallRecoveryBarrier && !runtimeStateLoadInvalid &&
                await transitionJournalStore.ExistsAsync(cancellationToken).ConfigureAwait(false);
            if (!HasForcedUninstallRecoveryBarrier && configurationLoadFailed && pendingJournalExists)
            {
                // Configuration is already untrustworthy, so do not parse/recover the journal or
                // query Task Scheduler. Its mere presence makes writer ownership ambiguous and
                // must block every HID/commissioning path while preserving the journal verbatim.
                invalidConfigurationWithPendingJournal = true;
                hardwareAccessBlocked = true;
                configurationError =
                    "The controller configuration is invalid and a takeover transition journal is " +
                    "present. Task and hardware ownership cannot be reconstructed together; the " +
                    "journal was preserved without task access and explicit attended recovery is required.";
                logger.LogCritical(
                    "Invalid configuration coexists with a takeover journal; task recovery and HID access are blocked.");
            }
            else if (SafetyProtectionDisabled && pendingJournalExists)
            {
                hardwareAccessBlocked = true;
                configurationError = "自动控制已禁用；未决接管恢复事务已保留，需要明确恢复处理。";
            }
            else if (!SafetyProtectionDisabled && !HasForcedUninstallRecoveryBarrier && !runtimeStateLoadInvalid &&
                     !configurationLoadFailed && !auditMutationBlocked)
            {
                try
                {
                    await RecoverPendingTakeoverTransitionAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    configurationError = $"Takeover transition recovery failed: {exception.Message}";
                    hardwareAccessBlocked = true;
                    logger.LogCritical(exception,
                        "The pending takeover transition could not be recovered; HID access is blocked.");
                }
            }

            if (!runtimeStateLoadInvalid &&
                runtimeState.Commissioned != configuration.Commissioned)
            {
                // Without a valid journal there is no trustworthy way to determine whether
                // Myth.Cool owns the device. Do not touch HID while the durable states disagree.
                configuration = configuration with { Commissioned = true };
                runtimeState = runtimeState with { Commissioned = true };
                configurationError ??=
                    "Commissioning state files disagree without a recoverable transition journal.";
                hardwareAccessBlocked = true;
                commissioningStateMismatch = true;
                logger.LogCritical(
                    "Configuration and runtime commissioning state disagree; HID access is blocked.");
            }

            try
            {
                if (SafetyProtectionDisabled)
                {
                    activeSafetyOperationAudit = await safetyOperationAuditJournalStore.LoadAsync(cancellationToken)
                        .ConfigureAwait(false);
                    if (activeSafetyOperationAudit is not null)
                        LatchSafetyOperationAuditBlocked(new InvalidOperationException("自动控制已禁用；未决安全事务已保留。"),
                            activeSafetyOperationAudit, "startup automatic output disabled");
                }
                else await RecoverPendingSafetyOperationAuditAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LatchSafetyOperationAuditBlocked(
                    exception,
                    activeSafetyOperationAudit,
                    "startup safety-operation audit recovery");
            }

            if (HasForcedUninstallRecoveryBarrier)
            {
                hardwareAccessBlocked = true;
            }

            initialized = true;
            UpdateStatus(safetyOperationAuditBlocked
                ? "SafetyOperationAuditRecoveryRequired"
                : HasForcedUninstallRecoveryBarrier
                ? forcedUninstallRecoveryMarkerError is null
                    ? "ForcedUninstallRecoveryRequired"
                    : "ForcedUninstallRecoveryMarkerInvalid"
                : configurationError is null
                    ? configuration.Commissioned ? "Starting" : "NotCommissioned"
                    : "ConfigurationInvalid");
        }
        finally
        {
            MarkHeartbeat();
            gate.Release();
        }
    }

    public async Task TickAsync(CancellationToken cancellationToken)
    {
        MarkHeartbeat();
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (await TryEnsureStartupFanOnUnderGateAsync(cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            if (SafetyProtectionDisabled || (AutomaticAdjustmentsDisabled && commissioningSession is null))
            {
                if (!IsHardwarePoisoned) await UpdateThermalAsync(cancellationToken).ConfigureAwait(false);
                await ObserveManualDeviceAsync(cancellationToken).ConfigureAwait(false);
                SetPassiveDecision();
                UpdateStatus(configuration.Mode.ToString());
                return;
            }
            if (ManualRequestPending || observedManualControlHold &&
                (observedManualPersistenceUnconfirmed || hardwareAccessBlocked || configurationError is not null))
            {
                SetPassiveDecision();
                UpdateStatus(ManualRequestPending ? "ManualRequestPending" : "ManualObservationSaveUnconfirmed");
                return;
            }
            if (IsHardwarePoisoned)
            {
                UpdateStatus("HardwarePoisoned", FailSafeReason.HidFailure);
                return;
            }

            if (HasForcedUninstallRecoveryBarrier)
            {
                // A fresh process cannot truthfully claim that it sent 100% after a forced
                // uninstall. Preserve an explicit unavailable safety state and, critically,
                // schedule no thermal/HID/device cleanup operation from the normal loop.
                lastDecision = new ControlDecision(
                    100,
                    ShouldWrite: false,
                    FailSafeActive: true,
                    FailSafeReason.ConfigurationInvalid,
                    timeProvider.GetUtcNow(),
                    EffectiveTemperatureCelsius: null);
                UpdateStatus(forcedUninstallRecoveryMarkerError is null
                        ? "ForcedUninstallRecoveryRequired"
                        : "ForcedUninstallRecoveryMarkerInvalid",
                    FailSafeReason.ConfigurationInvalid);
                return;
            }

            await ExpireCommissioningSessionAsync().ConfigureAwait(false);
            if (IsHardwarePoisoned)
            {
                UpdateStatus("HardwarePoisoned", FailSafeReason.HidFailure);
                return;
            }

            await UpdateThermalAsync(cancellationToken).ConfigureAwait(false);
            if (IsHardwarePoisoned)
            {
                UpdateStatus("HardwarePoisoned", FailSafeReason.HidFailure);
                return;
            }

            if (hardwareAccessBlocked)
            {
                // Failed commit compensation may intentionally retain an already-open
                // device and its process-scoped writer lease when the task is proven
                // disabled. Releasing it here would contradict the reported owner and
                // could permit a second writer before journal recovery resolves ownership.
                if (!configuration.Commissioned)
                {
                    await ReleaseDeviceAsync().ConfigureAwait(false);
                }
                else if (device is { } blockedTarget &&
                         (lastWrittenPercent != 100 || lastDeviceState?.PoweredOn != true || manualPower.OffRequested))
                {
                    manualPower.RequestOn();
                    try
                    {
                        await WriteFanSpeedAsync(blockedTarget, 100, cancellationToken)
                            .ConfigureAwait(false);
                        lastSuccessfulWrite = timeProvider.GetUtcNow();
                        lastSuccessfulWriteMonotonic = GetMonotonicNow();
                        lastWrittenPercent = 100;
                    }
                    catch (Exception exception) when (IsHardwareFailure(exception))
                    {
                        MarkHardwarePoisoned(
                            "BLOCKED_STATE_FAILSAFE_WRITE_FAILED",
                            "audit or takeover recovery fail-safe write",
                            exception);
                    }
                }

                lastDecision = new ControlDecision(
                    100,
                    ShouldWrite: false,
                    FailSafeActive: true,
                    FailSafeReason.ConfigurationInvalid,
                    timeProvider.GetUtcNow(),
                    lastThermal.ControlCelsius);
                UpdateStatus(
                    safetyOperationAuditBlocked
                        ? "SafetyOperationAuditRecoveryRequired"
                        : "ConfigurationInvalid",
                    FailSafeReason.ConfigurationInvalid);
                return;
            }

            if (commissioningSession is not null)
            {
                if (await MonitorCommissioningSessionAsync(cancellationToken).ConfigureAwait(false))
                {
                    return;
                }

                var session = commissioningSession;
                UpdateStatus($"Commissioning{session.Stage}", FailSafeReason.NotCommissioned,
                    session.ProbedIdentity);
                return;
            }

            if (!configuration.Commissioned)
            {
                await ReleaseDeviceAsync().ConfigureAwait(false);

                lastDecision = policy.Evaluate(new ControlInput(
                    timeProvider.GetUtcNow(),
                    configuration,
                    lastThermal,
                    false,
                    false,
                    false,
                    GetMonotonicNow(),
                    lastThermalSampleSequence));
                UpdateStatus(configurationError is null ? "NotCommissioned" : "ConfigurationInvalid",
                    configurationError is null ? FailSafeReason.NotCommissioned : FailSafeReason.ConfigurationInvalid);
                return;
            }

            var conflict = conflictDetector.Inspect();
            if (conflict.HasConflict)
            {
                if (!await ReleaseDeviceAfterConflictSafetyAsync(cancellationToken).ConfigureAwait(false))
                {
                    return;
                }

                lastDecision = policy.Evaluate(new ControlInput(
                    timeProvider.GetUtcNow(), configuration, lastThermal, false, false, false,
                    GetMonotonicNow(),
                    lastThermalSampleSequence,
                    MythCoolConflict: true));
                UpdateStatus("BlockedByMythCool", FailSafeReason.MythCoolConflict);
                return;
            }

            if (!writerLease.TryAcquire())
            {
                await ReleaseDeviceAsync(releaseWriterLease: false).ConfigureAwait(false);
                UpdateStatus("WriterLeaseUnavailable", FailSafeReason.HidFailure);
                return;
            }

            var reconnected = false;
            var newlyOpened = false;
            if (device is null)
            {
                var candidates = await FindCandidateIdentitiesAsync(cancellationToken).ConfigureAwait(false);
                if (candidates.Count == 0)
                {
                    deviceWasMissing = everOpened;
                    lastDecision = policy.Evaluate(new ControlInput(
                        timeProvider.GetUtcNow(), configuration, lastThermal, false, false, false,
                        GetMonotonicNow(),
                        lastThermalSampleSequence));
                    UpdateStatus("DeviceUnavailable", FailSafeReason.DeviceUnavailable);
                    return;
                }

                if (candidates.Count != 1)
                {
                    lastDecision = policy.Evaluate(new ControlInput(
                        timeProvider.GetUtcNow(), configuration, lastThermal, true, false, false,
                        GetMonotonicNow(),
                        lastThermalSampleSequence));
                    UpdateStatus("DuplicateDevices", FailSafeReason.UnsupportedDevice, candidates[0]);
                    return;
                }

                var candidate = candidates[0];

                if (!candidate.IsSupportedV12Ultra)
                {
                    lastDecision = policy.Evaluate(new ControlInput(
                        timeProvider.GetUtcNow(), configuration, lastThermal, true, false, false,
                        GetMonotonicNow(),
                        lastThermalSampleSequence));
                    UpdateStatus("UnsupportedDevice", FailSafeReason.UnsupportedDevice, candidate);
                    return;
                }

                var openResult = await OpenExpectedDeviceAsync(candidate, cancellationToken)
                    .ConfigureAwait(false);
                if (openResult.Status != V12DeviceOpenStatus.Opened)
                {
                    UpdateStatusForOpenFailure(openResult.Status, candidate);
                    return;
                }

                device = openResult.Device
                    ?? throw new InvalidOperationException("An opened device result did not contain a device.");
                newlyOpened = true;

                reconnected = deviceWasMissing;
                everOpened = true;
                deviceWasMissing = false;
                if (reconnected)
                {
                    policy.Reset();
                }
            }

            try
            {
                if (newlyOpened)
                {
                    var initialState = await ReadInitialStateOrRestoreCacheAsync(device, cancellationToken)
                        .ConfigureAwait(false);
                    lastDeviceState = initialState;
                    var fanWasNotRunning = initialState.PoweredOn != true || initialState.FanSpeedPercent < 10;
                    var startupPercent = fanWasNotRunning && startupFanKickPending && !manualPower.OffRequested
                        ? configuration.Mode == ControlMode.Manual
                            ? configuration.ManualSpeedPercent
                            : configuration.StartupFanPercent
                        : (byte)100;
                    await WriteFanSpeedAsync(device, startupPercent, cancellationToken).ConfigureAwait(false);
                    var safeReadback = await ReadDeviceStateAsync(device, cancellationToken).ConfigureAwait(false);
                    safeReadback = await RestoreOwnedSoftwareModeAsync(device, initialState, safeReadback,
                        cancellationToken).ConfigureAwait(false);
                    // Software-mode restoration establishes its own ON/100 baseline.
                    // Re-apply the requested startup kick afterwards so a device that
                    // reports a non-physical mode still starts at the configured target.
                    if (startupPercent != 100 &&
                        safeReadback.PoweredOn && safeReadback.FanSpeedPercent != startupPercent)
                    {
                        await WriteFanSpeedAsync(device, startupPercent, cancellationToken).ConfigureAwait(false);
                        safeReadback = await ReadDeviceStateAsync(device, cancellationToken).ConfigureAwait(false);
                    }
                    if (!safeReadback.PoweredOn || safeReadback.FanSpeedPercent != startupPercent ||
                        !LightingMatches(initialState, safeReadback))
                    {
                        throw new V12ProtocolException(
                            $"A newly opened V12 Ultra did not confirm ON at {startupPercent}% with lighting preserved.");
                    }

                    var safeAt = timeProvider.GetUtcNow();
                    lastDeviceState = safeReadback;
                    lastSuccessfulRead = safeAt;
                    lastSuccessfulWrite = safeAt;
                    lastSuccessfulWriteMonotonic = GetMonotonicNow();
                    lastWrittenPercent = startupPercent;
                    policy.RecordWriteSuccess(lastSuccessfulWriteMonotonic.Value, startupPercent);
                    startupFanKickPending = false;
                }
                else
                {
                    var readRevision = Interlocked.Read(ref manualRequestRevision);
                    var resetObservationBaseline = explicitControlBaselineResetPending;
                    var previousObservation = ReferenceEquals(lastObservedManualDevice, device)
                        ? lastObservedManualDeviceBaseline : null;
                    lastDeviceState = await ReadDeviceStateAsync(device, cancellationToken).ConfigureAwait(false);
                    lastSuccessfulRead = timeProvider.GetUtcNow();
                    if (ManualRequestPending || readRevision != Interlocked.Read(ref manualRequestRevision)) return;
                    var expectedPercent = previousObservation?.FanSpeedPercent ?? lastWrittenPercent;
                    var expectedPower = previousObservation?.PoweredOn ?? !manualPower.OffRequested;
                    if (!resetObservationBaseline && (lastDeviceState.PoweredOn != expectedPower ||
                        expectedPercent is { } expected && lastDeviceState.FanSpeedPercent != expected))
                    {
                        if (await AdoptObservedManualStateAsync(lastDeviceState, cancellationToken, readRevision)
                            .ConfigureAwait(false))
                        {
                            manualPower.ObserveDevicePower(lastDeviceState.PoweredOn);
                            return;
                        }
                    }
                    // A newly queued request may have won the adoption CAS. Keep the
                    // previous baseline until it succeeds or this physical change is adopted.
                    if (ManualRequestPending || readRevision != Interlocked.Read(ref manualRequestRevision)) return;
                    RecordManualObservationBaseline(device, lastDeviceState);
                }

                var now = timeProvider.GetUtcNow();
                MarkHeartbeat();
                if (configurationError is not null)
                {
                    var monotonicNow = GetMonotonicNow();
                    var shouldWrite = lastWrittenPercent != 100 ||
                                      lastSuccessfulWriteMonotonic is null ||
                                      monotonicNow - lastSuccessfulWriteMonotonic.Value >= TimeSpan.FromSeconds(30);
                    lastDecision = new ControlDecision(
                        100,
                        shouldWrite,
                        true,
                        FailSafeReason.ConfigurationInvalid,
                        now,
                        lastThermal.ControlCelsius);
                    if (shouldWrite)
                    {
                        await WriteFanSpeedAsync(device, 100, cancellationToken).ConfigureAwait(false);
                        lastSuccessfulWrite = now;
                        lastSuccessfulWriteMonotonic = monotonicNow;
                        lastWrittenPercent = 100;
                    }

                    UpdateStatus("ConfigurationInvalid", FailSafeReason.ConfigurationInvalid);
                    return;
                }

                lastDecision = policy.Evaluate(new ControlInput(
                    now,
                    configuration,
                    lastThermal,
                    true,
                    true,
                    true,
                    GetMonotonicNow(),
                    lastThermalSampleSequence,
                    DeviceReconnected: reconnected));

                var powerDecision = manualPower.Evaluate(CreateManualPowerInput());
                if (powerDecision.KeepOff)
                {
                    UpdateStatus("ManuallyOff");
                    return;
                }

                if (lastDeviceState is { PoweredOn: false } &&
                    (configuration.Mode != ControlMode.Manual || lastDecision.FailSafeActive))
                {
                    await ConfirmManualPowerOnAsync(device, cancellationToken).ConfigureAwait(false);
                    policy.Reset();
                    UpdateStatus("FailSafe", FailSafeReason.Recovering);
                    return;
                }

                if (lastDecision.ShouldWrite)
                {
                    var manualTarget = configuration.Mode == ControlMode.Manual && !lastDecision.FailSafeActive;
                    if (manualTarget)
                    {
                        if (manualCommandUnconfirmed ||
                            !await WriteConfiguredManualTargetAsync(device, lastDecision.RequestedPercent, cancellationToken)
                                .ConfigureAwait(false))
                        {
                            UpdateStatus(manualCommandUnconfirmed ? "ManualCommandUnconfirmed" : GetStatus().ServiceState);
                            return;
                        }
                    }
                    else await WriteFanSpeedAsync(device, lastDecision.RequestedPercent, cancellationToken)
                            .ConfigureAwait(false);
                    policy.RecordWriteSuccess(GetMonotonicNow(), lastDecision.RequestedPercent);
                    lastSuccessfulWrite = now;
                    lastSuccessfulWriteMonotonic = GetMonotonicNow();
                    lastWrittenPercent = lastDecision.RequestedPercent;
                    // The next GET is compared with this command, then becomes a new
                    // observation baseline. Do not mistake our own write for a button change.
                    if (!manualTarget) lastObservedManualDeviceBaseline = null;
                }

                UpdateStatus(lastDecision.FailSafeActive ? "FailSafe" : "Running");
            }
            catch (Exception exception) when (IsHardwareFailure(exception))
            {
                logger.LogError(exception, "V12 Ultra HID control failed.");
                await BestEffortFailSafeAsync(CancellationToken.None).ConfigureAwait(false);
                await ReleaseDeviceAsync().ConfigureAwait(false);
                deviceWasMissing = true;
                UpdateStatus("HidFailure", FailSafeReason.HidFailure);
            }
        }
        catch (SafetyOutputSuppressedException)
        {
            SetPassiveDecision();
            UpdateStatus(SafetyProtectionDisabled ? "SafetyProtectionDisabled" : "ManualRequestPending");
        }
        finally
        {
            try { await AdvanceProfileApplicationUnderGateAsync(cancellationToken).ConfigureAwait(false); }
            finally { MarkHeartbeat(); gate.Release(); }
        }
    }

    public async Task SetModeAsync(ControlMode mode, PipePeerIdentity peer, CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(mode))
        {
            throw Invalid("mode must be auto or manual.");
        }

        var revision = BeginManualRequest();
        var previous = manualOperationRevision.Value;
        manualOperationRevision.Value = revision;
        try
        {
            await UpdateConfigurationAsync(current =>
            {
                if (commissioningSession is not null) throw Invalid("Finish commissioning before changing ownership mode.");
                if (mode is ControlMode.PureManual or ControlMode.Released && !current.Commissioned)
                    throw Invalid("Complete commissioning before selecting a passive mode.");
                return current with { Mode = mode };
            }, "mode.set", peer, cancellationToken, revision).ConfigureAwait(false);
        }
        finally { manualOperationRevision.Value = previous; ResolveManualRequest(revision); }
    }

    public Task SetManualSpeedAsync(byte percent, PipePeerIdentity peer, CancellationToken cancellationToken) =>
        SetManualSpeedRequestAsync(percent, peer, cancellationToken);

    public async Task SetCurveAsync(FanCurve curve, PipePeerIdentity peer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(curve);
        try
        {
            curve.Validate();
        }
        catch (ConfigurationValidationException exception)
        {
            throw Invalid(exception.Message, exception);
        }

        await UpdateConfigurationAsync(current => current with { Curve = curve }, "curve.set", peer, cancellationToken)
            .ConfigureAwait(false);
    }

    public FanCurve GetCurve() => configuration.Curve;

    public Task ApplyCurveAdviceAsync(FanCurve expected, FanCurve proposed, PipePeerIdentity peer, CancellationToken cancellationToken)
    {
        expected.Validate();
        proposed.Validate();
        return UpdateConfigurationAsync(current =>
        {
            if (!current.Curve.Points.SequenceEqual(expected.Points))
                throw Invalid("The curve changed since analysis. Analyze again before applying.");
            return current with { Curve = proposed, Mode = ControlMode.Auto };
        }, PipeOperations.CurveAdviceApply, peer, cancellationToken);
    }

    public ShutdownStatus GetShutdownStatus() => CreateShutdownStatus();

    public object GetDiagnostics()
    {
        var statusSnapshot = GetStatus();
        var hardwareSafety = statusSnapshot.HardwareSafety;
        var cacheError = Volatile.Read(ref deviceStateCacheError);
        return new
        {
            generatedAt = timeProvider.GetUtcNow(),
            product = ServiceConstants.DisplayName,
            apiVersion = PipeProtocol.ApiVersion,
            status = statusSnapshot,
            policy = new
            {
                failSafePercent = 100,
                startupFanPercent = configuration.StartupFanPercent,
                emergencyTemperatureCelsius = 90,
                emergencyRecoveryBelowCelsius = 85,
            },
            environment = new
            {
                osVersion = Environment.OSVersion.VersionString,
                processArchitecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
                serviceVersion = typeof(ControllerCoordinator).Assembly.GetName().Version?.ToString(),
            },
            hardwareSafety = new
            {
                poisoned = hardwareSafety.Poisoned,
                reason = hardwareSafety.Reason,
                since = hardwareSafety.Since,
            },
            deviceStateCache = new
            {
                healthy = cacheError is null,
                errorCode = cacheError,
            },
            auditMutation = statusSnapshot.AuditMutation,
            safetyOperationAudit = statusSnapshot.SafetyOperationAudit,
            forcedUninstallRecovery = statusSnapshot.ForcedUninstallRecovery,
            configurationErrorCode = configurationError is null
                ? null
                : "CONTROLLER_CONFIGURATION_ERROR",
            // Deliberately omit device paths, serial numbers, usernames and full SIDs.
        };
    }

    public async Task<object> QualifySystemShutdownOffAsync(
        bool attended,
        PipePeerIdentity peer,
        CancellationToken cancellationToken)
    {
        RequireAttended(attended, "Shutdown qualification");
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        SafetyOperationAuditJournal? operationAudit = null;
        V12DeviceState? confirmedRecoveryReadback = null;
        try
        {
            EnsureInitialized();
            ThrowIfHardwarePoisoned();
            ThrowIfHardwareAccessBlocked();
            if (configuration.SystemShutdownFanBehavior != SystemShutdownFanBehavior.KeepAt100 ||
                configuration.ShutdownOffQualification is not null)
            {
                throw Invalid(
                    "Disable the existing shutdown behavior before running a new qualification test.");
            }

            if (GetActiveShutdownQualificationPending() is not null)
            {
                throw Invalid(
                    "A shutdown qualification is already awaiting explicit enable or disable.");
            }

            var target = RequireShutdownQualificationPreconditions();
            var qualificationBaselineConfiguration = configuration;
            var qualificationOwnerSid = runtimeState.OwnerSid!;
            var qualificationIdentity = target.Identity;
            var shutdownDevice = target as IV12SystemShutdownDevice
                ?? throw new ControllerOperationException(
                    PipeErrorCodes.UnsupportedDevice,
                    "The current V12 Ultra handle does not support the fixed shutdown qualification protocol.");

            ThermalSnapshot thermal;
            try
            {
                thermal = await ReadThermalWithTimeoutAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (IsHardwareFailure(exception))
            {
                throw new ControllerOperationException(
                    PipeErrorCodes.TelemetryStale,
                    "Shutdown qualification requires a fresh CPU and control temperature below 60 C.",
                    exception);
            }

            lastThermal = thermal;
            RequireShutdownQualificationThermal(thermal);
            _ = RequireShutdownQualificationPreconditions(target);

            operationAudit = await BeginSafetyOperationAuditAsync(
                    PipeOperations.ShutdownQualify,
                    peer.CallerSid,
                    peer.Source,
                    SafetyOperationKind.HidWithOn100Recovery,
                    new
                    {
                        behavior = qualificationBaselineConfiguration.SystemShutdownFanBehavior.ToString(),
                        qualified = qualificationBaselineConfiguration.ShutdownOffQualification is not null,
                        pending = false,
                        identity = FormatIdentity(qualificationIdentity),
                    },
                    new
                    {
                        behavior = SystemShutdownFanBehavior.KeepAt100.ToString(),
                        qualified = false,
                        pending = true,
                        sequence = "GET-ON100-GET-OFF-GET-ON100-GET",
                        identity = FormatIdentity(qualificationIdentity),
                        finalPoweredOn = true,
                        finalPercent = 100,
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            using var cancellationGate = new ShutdownQualificationCancellationGate(cancellationToken);
            ShutdownQualificationTransactionResult transaction;
            try
            {
                transaction = await RunHidOperationAsync(
                        token => ExecuteShutdownQualificationTransactionAsync(
                            target,
                            shutdownDevice,
                            cancellationGate,
                            qualificationIdentity,
                            qualificationOwnerSid,
                            token),
                        ShutdownQualificationTransactionBudget,
                        cancellationGate.PreOffCancellationToken,
                        timeoutPoisonResource: "attended shutdown qualification HID transaction")
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is TimeoutException or OperationCanceledException)
            {
                if (IsHardwarePoisoned)
                {
                    throw new ControllerOperationException(
                        PipeErrorCodes.HidTimeout,
                        "Shutdown qualification timed out with an unknown hardware result. The handle and writer lease are retained until the service exits.",
                        exception);
                }

                throw;
            }

            if (!transaction.FinalOnAtOneHundredConfirmed || transaction.FinalReadback is null)
            {
                MarkHardwarePoisoned(
                    "SHUTDOWN_QUALIFICATION_RECOVERY_UNCONFIRMED",
                    "attended shutdown qualification ON-at-100 recovery",
                    transaction.Failure ?? new V12ProtocolException(
                        "The shutdown qualification transaction did not confirm its final ON-at-100 state."));
                throw new ControllerOperationException(
                    PipeErrorCodes.HidTimeout,
                    "Shutdown qualification could not confirm final ON at 100%. Hardware ownership is retained until the service exits.",
                    transaction.Failure);
            }

            var confirmedAt = timeProvider.GetUtcNow();
            lastDeviceState = transaction.FinalReadback;
            lastSuccessfulRead = confirmedAt;
            lastSuccessfulWrite = confirmedAt;
            lastWrittenPercent = 100;
            policy.RecordWriteSuccess(GetMonotonicNow(), 100);
            confirmedRecoveryReadback = transaction.FinalReadback;

            if (!transaction.Qualified)
            {
                UpdateStatus("ShutdownQualificationRefused");
                if (cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException(cancellationToken);
                }

                throw CreateShutdownQualificationFailure(transaction.Failure);
            }

            // Cancellation after the OFF boundary never interrupts recovery, but it also cannot
            // silently turn a disconnected UI gesture into a durable opt-in. The device is now
            // proven ON at 100%; leave the default disabled configuration unchanged.
            cancellationToken.ThrowIfCancellationRequested();
            _ = RequireShutdownQualificationPreconditions(target);
            RequireShutdownQualificationThermal(lastThermal);
            if (!string.Equals(
                    runtimeState.OwnerSid,
                    qualificationOwnerSid,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new ControllerOperationException(
                    PipeErrorCodes.InternalError,
                    "The commissioned runtime owner changed during shutdown qualification.");
            }

            if (target.Identity != qualificationIdentity)
            {
                throw new ControllerOperationException(
                    PipeErrorCodes.UnsupportedDevice,
                    "The exact device-handle identity changed during shutdown qualification.");
            }

            if (!RecoveryConfigurationMatches(qualificationBaselineConfiguration, configuration))
            {
                throw new ControllerOperationException(
                    PipeErrorCodes.InternalError,
                    "The controller configuration changed during shutdown qualification.");
            }

            var pending = new ShutdownQualificationPending(
                Guid.NewGuid(),
                peer.CallerSid,
                qualificationOwnerSid,
                target,
                qualificationIdentity,
                qualificationBaselineConfiguration,
                confirmedAt,
                confirmedAt + ServiceConstants.ShutdownQualificationPendingLifetime,
                GetMonotonicNow() + ServiceConstants.ShutdownQualificationPendingLifetime);
            Volatile.Write(ref shutdownQualificationPending, pending);

            await ResolveAndFinishSafetyOperationAuditAsync(
                    operationAudit,
                    SafetyOperationAuditOutcome.Success,
                    new
                    {
                        behavior = configuration.SystemShutdownFanBehavior.ToString(),
                        qualified = false,
                        pending = true,
                        qualificationId = pending.Id,
                        pending.ExpiresAtUtc,
                        identity = FormatIdentity(target.Identity),
                        pending.QualifiedAtUtc,
                        sequence = "GET-ON100-GET-OFF-GET-ON100-GET",
                        lightingPreserved = true,
                        finalPoweredOn = true,
                        finalPercent = 100,
                    },
                    CancellationToken.None)
                .ConfigureAwait(false);
            operationAudit = null;
            UpdateStatus("ShutdownQualificationPending");
            return new
            {
                shutdown = CreateShutdownStatus(),
                qualificationId = pending.Id,
                expiresAt = pending.ExpiresAtUtc,
                finalState = PublicDeviceState(transaction.FinalReadback),
            };
        }
        catch (Exception exception)
        {
            if (operationAudit is not null && !safetyOperationAuditBlocked)
            {
                if (confirmedRecoveryReadback is not null)
                {
                    await ResolveAndFinishSafetyOperationAuditAsync(
                            operationAudit,
                            SafetyOperationAuditOutcome.Compensated,
                            new
                            {
                                result = "qualification-not-published",
                                failure = exception.GetType().Name,
                                finalPoweredOn = confirmedRecoveryReadback.PoweredOn,
                                finalPercent = confirmedRecoveryReadback.FanSpeedPercent,
                                lightingPreserved = true,
                                pending = false,
                            },
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    operationAudit = null;
                }
                else
                {
                    LatchSafetyOperationAuditBlocked(
                        exception,
                        activeSafetyOperationAudit ?? operationAudit,
                        "shutdown qualification before confirmed ON-at-100 recovery");
                }
            }

            throw;
        }
        finally
        {
            MarkHeartbeat();
            gate.Release();
        }
    }

    public async Task<object> EnableSystemShutdownOffAsync(
        Guid qualificationId,
        bool attended,
        PipePeerIdentity peer,
        CancellationToken cancellationToken)
    {
        RequireAttended(attended, "Shutdown enable");
        if (qualificationId == Guid.Empty)
        {
            throw Invalid("shutdown.enable requires a valid qualificationId.");
        }

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureInitialized();
            ThrowIfHardwarePoisoned();
            ThrowIfHardwareAccessBlocked();
            var pending = GetActiveShutdownQualificationPending()
                ?? throw Invalid("The shutdown qualification is missing or expired; run the attended test again.");
            if (pending.Id != qualificationId)
            {
                throw Invalid("The shutdown qualificationId does not match the current pending test.");
            }

            if (!string.Equals(pending.CallerSid, peer.CallerSid, StringComparison.OrdinalIgnoreCase))
            {
                throw Invalid("The pending shutdown qualification belongs to a different caller SID.");
            }

            var currentTarget = device;
            if (currentTarget is null || !ReferenceEquals(currentTarget, pending.Target) ||
                currentTarget.Identity != pending.Identity)
            {
                _ = Interlocked.CompareExchange(ref shutdownQualificationPending, null, pending);
                throw new ControllerOperationException(
                    PipeErrorCodes.UnsupportedDevice,
                    "The exact device handle changed after shutdown qualification; run the test again.");
            }

            var target = RequireShutdownQualificationPreconditions(pending.Target);
            RequirePendingShutdownQualificationStillMatches(pending, target);

            ThermalSnapshot thermal;
            try
            {
                thermal = await ReadThermalWithTimeoutAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (IsHardwareFailure(exception))
            {
                throw new ControllerOperationException(
                    PipeErrorCodes.TelemetryStale,
                    "Shutdown enable requires a fresh CPU and control temperature below 60 C.",
                    exception);
            }

            lastThermal = thermal;
            RequireShutdownQualificationThermal(thermal);
            target = RequireShutdownQualificationPreconditions(pending.Target);
            RequirePendingShutdownQualificationStillMatches(pending, target);
            cancellationToken.ThrowIfCancellationRequested();

            if (!ReferenceEquals(
                    Interlocked.CompareExchange(ref shutdownQualificationPending, null, pending),
                    pending))
            {
                throw Invalid("The pending shutdown qualification changed before enable.");
            }

            var qualification = CreateShutdownQualification(target.Identity, pending.QualifiedAtUtc);
            var enabledConfiguration = configuration with
            {
                SystemShutdownFanBehavior = SystemShutdownFanBehavior.TurnOffOnSystemShutdown,
                ShutdownOffQualification = qualification,
            };
            enabledConfiguration.Validate();
            await PersistConfigurationMutationAsync(
                    enabledConfiguration,
                    PipeOperations.ShutdownEnable,
                    peer,
                    cancellationToken)
                .ConfigureAwait(false);
            UpdateStatus("ShutdownEnabled");
            return new { shutdown = CreateShutdownStatus() };
        }
        finally
        {
            MarkHeartbeat();
            gate.Release();
        }
    }

    public async Task<object> DisableSystemShutdownOffAsync(
        PipePeerIdentity peer,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureInitialized();
            if (configurationError is not null || invalidConfigurationWithPendingJournal)
            {
                throw new ControllerOperationException(
                    PipeErrorCodes.InternalError,
                    "The current configuration cannot be verified semantically; shutdown disable will not overwrite fallback state.");
            }

            _ = Interlocked.Exchange(ref shutdownQualificationPending, null);
            var disabled = configuration with
            {
                SystemShutdownFanBehavior = SystemShutdownFanBehavior.KeepAt100,
                ShutdownOffQualification = null,
            };
            disabled.Validate();
            await PersistConfigurationMutationAsync(
                    disabled,
                    PipeOperations.ShutdownDisable,
                    peer,
                    cancellationToken)
                .ConfigureAwait(false);
            UpdateStatus(configuration.Commissioned ? "ShutdownDisabled" : "NotCommissioned");
            return new { shutdown = CreateShutdownStatus(), hidAccessed = false };
        }
        catch (ConfigurationValidationException exception)
        {
            throw Invalid(exception.Message, exception);
        }
        catch (ControllerOperationException)
        {
            throw;
        }
        catch (Exception exception) when (!IsFatalPersistenceFailure(exception))
        {
            throw new ControllerOperationException(
                PipeErrorCodes.InternalError,
                "The shutdown behavior mutation did not complete safely; inspect audit-mutation status before retrying.",
                exception);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<object> ProbeCommissioningAsync(
        bool attended,
        PipePeerIdentity peer,
        CancellationToken cancellationToken)
    {
        RequireAttended(attended);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureInitialized();
            ThrowIfHardwarePoisoned();
            ThrowIfHardwareAccessBlocked();
            if (configuration.Commissioned)
            {
                throw new ControllerOperationException(PipeErrorCodes.InvalidArgument,
                    "The controller is already commissioned.");
            }

            if (configurationError is not null)
            {
                throw new ControllerOperationException(PipeErrorCodes.InvalidArgument,
                    "Repair the invalid configuration before commissioning.");
            }

            if (commissioningSession is not null)
            {
                throw new ControllerOperationException(
                    PipeErrorCodes.InvalidArgument,
                    "An existing commissioning session must be explicitly aborted before starting a new probe.");
            }

            var conflict = conflictDetector.Inspect();
            if (conflict.HasConflict)
            {
                throw new ControllerOperationException(PipeErrorCodes.MythCoolConflict,
                    FormatMythCoolConflictMessage(
                        conflict,
                        $"Close the conflicting process before commissioning: {string.Join(", ", conflict.Processes)}."));
            }

            _ = await RequireCommissioningProbeThermalReadyAsync(cancellationToken).ConfigureAwait(false);

            var identities = await RunHidOperationAsync(
                token => deviceLocator.EnumerateAsync(token), HidTimeout, cancellationToken,
                timeoutPoisonResource: "commissioning device enumeration")
                .ConfigureAwait(false);
            var candidates = FindV12Candidates(identities);
            if (candidates.Count == 0)
            {
                throw new ControllerOperationException(PipeErrorCodes.DeviceNotFound,
                    "A V12 Ultra HID collection was not found.");
            }

            if (candidates.Count != 1)
            {
                throw new ControllerOperationException(PipeErrorCodes.UnsupportedDevice,
                    "Multiple V12 Ultra HID collections were found; refusing to select one implicitly.");
            }

            var candidate = candidates[0];
            if (!candidate.IsSupportedV12Ultra)
            {
                throw new ControllerOperationException(PipeErrorCodes.UnsupportedDevice,
                    $"Unsupported V12 Ultra firmware or HID identity: {FormatIdentity(candidate)}.");
            }

            var session = new CommissioningSession(
                Guid.NewGuid(),
                peer.CallerSid,
                timeProvider.GetUtcNow() + ServiceConstants.CommissioningSessionLifetime,
                candidate);
            var operationAudit = await BeginSafetyOperationAuditAsync(
                    PipeOperations.CommissionProbe,
                    peer.CallerSid,
                    peer.Source,
                    SafetyOperationKind.SessionOnly,
                    new { sessionActive = false, stage = (string?)null },
                    new
                    {
                        sessionActive = true,
                        stage = session.Stage.ToString(),
                        sessionId = session.Id,
                        identity = FormatIdentity(candidate),
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            commissioningSession = session;
            UpdateStatus("CommissioningProbe", FailSafeReason.NotCommissioned, candidate);
            await ResolveAndFinishSafetyOperationAuditAsync(
                operationAudit,
                SafetyOperationAuditOutcome.Success,
                new
                {
                    sessionActive = true,
                    stage = session.Stage.ToString(),
                    sessionId = session.Id,
                    identity = FormatIdentity(candidate),
                },
                CancellationToken.None)
                .ConfigureAwait(false);
            return new
            {
                sessionId = session.Id,
                expiresAt = session.ExpiresAtUtc,
                identity = new
                {
                    vendorId = $"{candidate.VendorId:X4}",
                    productId = $"{candidate.ProductId:X4}",
                    firmware = $"{candidate.Version:X4}",
                    usagePage = $"{candidate.UsagePage:X4}",
                    usage = $"{candidate.Usage:X4}",
                    featureReportLength = candidate.FeatureReportLength,
                },
                nextStage = "idempotent",
            };
        }
        finally
        {
            MarkHeartbeat();
            gate.Release();
        }
    }

    public async Task<object> VerifyIdempotentAsync(
        Guid sessionId,
        bool attended,
        PipePeerIdentity peer,
        CancellationToken cancellationToken)
    {
        RequireAttended(attended);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfHardwarePoisoned();
            ThrowIfHardwareAccessBlocked();
            var session = await RequireSessionAsync(
                sessionId, peer, CommissioningStage.Probed).ConfigureAwait(false);
            IV12Device? openedDevice = null;
            try
            {
                await RequireSafeCommissioningThermalAsync(session, requireBelowSixty: false, cancellationToken)
                    .ConfigureAwait(false);

                var identities = await RunHidOperationAsync(
                    token => deviceLocator.EnumerateAsync(token), HidTimeout, cancellationToken,
                    timeoutPoisonResource: "commissioning identity revalidation")
                    .ConfigureAwait(false);
                var candidates = FindV12Candidates(identities);
                if (candidates.Count != 1 ||
                    !candidates[0].IsSupportedV12Ultra ||
                    candidates[0] != session.ProbedIdentity)
                {
                    var issue = new CommissioningSafetyIssue(
                        FailSafeReason.UnsupportedDevice,
                        PipeErrorCodes.UnsupportedDevice,
                        "The V12 Ultra identity changed after probe; commissioning must restart.",
                        "CommissioningAbortedIdentityDrift");
                    await AbortCommissioningForSafetyAsync(session, issue, CancellationToken.None)
                        .ConfigureAwait(false);
                    throw new ControllerOperationException(issue.ErrorCode, issue.Message);
                }

                var operationAudit = await BeginSafetyOperationAuditAsync(
                        PipeOperations.CommissionIdempotent,
                        peer.CallerSid,
                        peer.Source,
                        SafetyOperationKind.HidWithOn100Recovery,
                        new
                        {
                            sessionId,
                            stage = CommissioningStage.Probed.ToString(),
                            deviceAttached = false,
                        },
                        new
                        {
                            sessionId,
                            stage = CommissioningStage.IdempotentVerified.ToString(),
                            sequence = "GET-EQUIVALENT-GET",
                            identity = FormatIdentity(session.ProbedIdentity),
                        },
                        cancellationToken)
                    .ConfigureAwait(false);

                if (!writerLease.TryAcquire())
                {
                    throw new ControllerOperationException(PipeErrorCodes.HidTimeout,
                        "Another controller owns the V12 Ultra writer lease.");
                }

                var openResult = await OpenExpectedDeviceAsync(session.ProbedIdentity, cancellationToken)
                    .ConfigureAwait(false);
                if (openResult.Status != V12DeviceOpenStatus.Opened)
                {
                    throw CreateOpenFailureException(openResult.Status);
                }

                openedDevice = openResult.Device
                    ?? throw new ControllerOperationException(PipeErrorCodes.InternalError,
                        "The validated V12 Ultra open result did not contain a device.");
                session.TakeDevice(openedDevice);
                openedDevice = null;
                var sessionDevice = session.Device
                    ?? throw new InvalidOperationException("The commissioning session did not retain its opened device.");
                if (sessionDevice.Identity != session.ProbedIdentity)
                {
                    throw new ControllerOperationException(PipeErrorCodes.UnsupportedDevice,
                        "The opened V12 Ultra identity differs from the probed identity.");
                }

                var original = await ReadDeviceStateAsync(sessionDevice, cancellationToken).ConfigureAwait(false);
                lastSuccessfulRead = timeProvider.GetUtcNow();
                if (!original.PoweredOn || original.FanSpeedPercent is < 10 or > 100)
                {
                    throw new ControllerOperationException(PipeErrorCodes.InvalidArgument,
                        "Commissioning requires the fan to be powered on with a valid current speed.");
                }

                session.RecordOriginalState(original);
                var attached = session.RequireAttachedDevice();
                await WriteFanSpeedAsync(
                    attached.Device,
                    attached.OriginalState.FanSpeedPercent,
                    cancellationToken).ConfigureAwait(false);
                lastSuccessfulWrite = timeProvider.GetUtcNow();
                var readback = await ReadDeviceStateAsync(attached.Device, cancellationToken)
                    .ConfigureAwait(false);
                lastSuccessfulRead = timeProvider.GetUtcNow();
                if (!LightingMatches(attached.OriginalState, readback) ||
                    readback.FanSpeedPercent != attached.OriginalState.FanSpeedPercent ||
                    !readback.PoweredOn)
                {
                    throw new ControllerOperationException(PipeErrorCodes.ProtocolChecksum,
                        "The equivalent write/readback changed unexpected state; commissioning stopped.");
                }

                session.Stage = CommissioningStage.IdempotentVerified;
                UpdateStatus("CommissioningIdempotentVerified", FailSafeReason.NotCommissioned,
                    session.ProbedIdentity);
                await ResolveAndFinishSafetyOperationAuditAsync(
                    operationAudit,
                    SafetyOperationAuditOutcome.Success,
                    new
                    {
                        sessionId,
                        stage = session.Stage.ToString(),
                        equivalentReadbackConfirmed = true,
                        poweredOn = readback.PoweredOn,
                        fanPercent = readback.FanSpeedPercent,
                        lightingPreserved = true,
                    },
                    CancellationToken.None).ConfigureAwait(false);
                return new { sessionId, verified = true, nextStage = "test" };
            }
            catch
            {
                if (ReferenceEquals(commissioningSession, session) && !safetyOperationAuditBlocked)
                {
                    await AbortCommissioningForSafetyAsync(
                        session,
                        new CommissioningSafetyIssue(
                            FailSafeReason.HidFailure,
                            PipeErrorCodes.HidTimeout,
                            "The idempotent commissioning check failed; commissioning must restart.",
                            "CommissioningAbortedIdempotentFailure"),
                        CancellationToken.None).ConfigureAwait(false);
                }

                throw;
            }
            finally
            {
                if (openedDevice is not null)
                {
                    await DisposeHardwareResourceAsync(
                        openedDevice,
                        "commissioning idempotent device",
                        CancellationToken.None,
                        HidTimeout,
                        poisonOnFailure: true).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            MarkHeartbeat();
            gate.Release();
        }
    }

    public async Task<object> TestCommissioningAsync(
        Guid sessionId,
        bool attended,
        PipePeerIdentity peer,
        CancellationToken cancellationToken,
        bool nearFullOnly = false)
    {
        RequireAttended(attended);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfHardwarePoisoned();
            ThrowIfHardwareAccessBlocked();
            var session = await RequireSessionAsync(
                sessionId, peer, CommissioningStage.IdempotentVerified).ConfigureAwait(false);
            (IV12Device Device, V12DeviceState OriginalState) attached;
            ThermalSnapshot thermal;
            byte[] speeds;
            bool coolEnoughForStagedTest;
            try
            {
                attached = session.RequireAttachedDevice();
                thermal = await RequireSafeCommissioningThermalAsync(
                    session, requireBelowSixty: false, cancellationToken).ConfigureAwait(false);
                coolEnoughForStagedTest = !nearFullOnly && thermal.ControlCelsius is < 60;
                speeds = nearFullOnly ? new byte[] { 99, 100 } : coolEnoughForStagedTest
                    ? new byte[] { 50, 70, 100 }
                    : new byte[] { 100 };

                _ = await BeginSafetyOperationAuditAsync(
                        PipeOperations.CommissionTest,
                        peer.CallerSid,
                        peer.Source,
                        SafetyOperationKind.HidWithOn100Recovery,
                        new
                        {
                            sessionId,
                            stage = CommissioningStage.IdempotentVerified.ToString(),
                            deviceAttached = true,
                        },
                        new
                        {
                            sessionId,
                            intendedStage = nearFullOnly
                                ? CommissioningStage.IdempotentVerified.ToString()
                                : CommissioningStage.TestVerified.ToString(),
                            speeds = speeds.Select(static speed => (int)speed).ToArray(),
                            nearFullOnly,
                            finalPoweredOn = true,
                            finalPercent = 100,
                        },
                        cancellationToken)
                    .ConfigureAwait(false);

                foreach (var speed in speeds)
                {
                    if (speed < 100)
                    {
                        await RequireSafeCommissioningThermalAsync(
                            session, requireBelowSixty: !nearFullOnly, cancellationToken).ConfigureAwait(false);
                    }

                    await WriteFanSpeedAsync(attached.Device, speed, cancellationToken).ConfigureAwait(false);
                    lastSuccessfulWrite = timeProvider.GetUtcNow();
                    await Task.Delay(TimeSpan.FromSeconds(1), timeProvider, cancellationToken).ConfigureAwait(false);
                    // Recheck fresh telemetry even for the no-lowering 100% path.
                    await RequireSafeCommissioningThermalAsync(
                        session, requireBelowSixty: speed < 100 && !nearFullOnly, cancellationToken).ConfigureAwait(false);

                    var readback = await ReadDeviceStateAsync(attached.Device, cancellationToken)
                        .ConfigureAwait(false);
                    lastSuccessfulRead = timeProvider.GetUtcNow();
                    if (readback.FanSpeedPercent != speed || !readback.PoweredOn ||
                        !LightingMatches(attached.OriginalState, readback))
                    {
                        throw new ControllerOperationException(PipeErrorCodes.ProtocolChecksum,
                            $"The {speed}% commissioning stage did not read back safely.");
                    }
                }
            }
            catch (Exception exception)
            {
                if (ReferenceEquals(commissioningSession, session) && !safetyOperationAuditBlocked)
                {
                    await AbortCommissioningForSafetyAsync(
                        session,
                        CreateCommissioningFailureIssue(exception, "Test"),
                        CancellationToken.None).ConfigureAwait(false);
                }

                throw;
            }

            var testedSpeeds = speeds.Select(static speed => (int)speed).ToArray();
            if (nearFullOnly)
            {
                // An explicit attended 99 -> 100 diagnostic never earns commit eligibility,
                // even below 60 C. Exceptions/cancellation above run the existing ON100 cleanup.
                UpdateStatus("CommissioningIdempotentVerified", FailSafeReason.NotCommissioned,
                    session.ProbedIdentity);
                await ResolveAndFinishSafetyOperationAuditAsync(
                    activeSafetyOperationAudit ?? throw new InvalidOperationException(
                        "The near-full test lost its durable safety-operation audit."),
                    SafetyOperationAuditOutcome.Success,
                    new
                    {
                        sessionId,
                        nearFullOnly = true,
                        stage = session.Stage.ToString(),
                        speeds = testedSpeeds,
                        finalPoweredOn = true,
                        finalPercent = 100,
                        commissioningVerified = false
                    },
                    CancellationToken.None).ConfigureAwait(false);
                return new
                {
                    sessionId,
                    verified = false,
                    nearFullTestPassed = true,
                    testedSpeeds,
                    retainedPercent = 100,
                    nextStage = "test"
                };
            }

            session.Stage = CommissioningStage.TestVerified;
            UpdateStatus("CommissioningTestVerified", FailSafeReason.NotCommissioned,
                session.ProbedIdentity);
            await ResolveAndFinishSafetyOperationAuditAsync(
                activeSafetyOperationAudit ?? throw new InvalidOperationException(
                    "The commissioning test lost its durable safety-operation audit."),
                SafetyOperationAuditOutcome.Success,
                new
                {
                    sessionId,
                    result = "success",
                    validationPath = coolEnoughForStagedTest ? "50-70-100" : "100-only-no-lowering",
                    lowerSpeedStagesSkipped = !coolEnoughForStagedTest,
                    stage = session.Stage.ToString(),
                    speeds = testedSpeeds,
                    thermal.ControlCelsius,
                    finalPoweredOn = true,
                    finalPercent = 100,
                },
                CancellationToken.None).ConfigureAwait(false);
            return new
            {
                sessionId,
                verified = true,
                testedSpeeds,
                lowerSpeedStagesSkipped = !coolEnoughForStagedTest,
                retainedPercent = (byte)100,
                nextStage = "commit",
            };
        }
        finally
        {
            MarkHeartbeat();
            gate.Release();
        }
    }

    public async Task<object> CommitCommissioningAsync(
        Guid sessionId,
        bool attended,
        PipePeerIdentity peer,
        CancellationToken cancellationToken)
    {
        RequireAttended(attended);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfHardwarePoisoned();
            ThrowIfHardwareAccessBlocked();
            var session = await RequireSessionAsync(
                sessionId, peer, CommissioningStage.TestVerified).ConfigureAwait(false);
            (IV12Device Device, V12DeviceState OriginalState) attached;
            V12DeviceState safeReadback;
            ScheduledTaskBackupMetadata backup;
            try
            {
                attached = session.RequireAttachedDevice();
                await RequireSafeCommissioningThermalAsync(
                    session, requireBelowSixty: false, cancellationToken).ConfigureAwait(false);
                if (conflictDetector.Inspect().HasConflict)
                {
                    throw new ControllerOperationException(PipeErrorCodes.MythCoolConflict,
                        "Myth.Cool restarted during commissioning; commit was refused.");
                }

                await WriteFanSpeedAsync(attached.Device, 100, cancellationToken).ConfigureAwait(false);
                safeReadback = await ReadDeviceStateAsync(attached.Device, cancellationToken)
                    .ConfigureAwait(false);
                if (!safeReadback.PoweredOn || safeReadback.FanSpeedPercent != 100 ||
                    !LightingMatches(attached.OriginalState, safeReadback))
                {
                    throw new ControllerOperationException(PipeErrorCodes.ProtocolChecksum,
                        "The 100% fail-safe state was not confirmed; commit was refused.");
                }

                backup = await taskManager.BackupCurrentAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                if (ReferenceEquals(commissioningSession, session))
                {
                    await AbortCommissioningForSafetyAsync(
                        session,
                        CreateCommissioningFailureIssue(exception, "CommitPreflight"),
                        CancellationToken.None).ConfigureAwait(false);
                }

                throw;
            }

            var previousConfiguration = configuration;
            var previousRuntimeState = runtimeState;
            var transitionStartedAt = timeProvider.GetUtcNow().ToUniversalTime();
            var previousAuditSnapshot = CreateTakeoverAuditSnapshot(
                previousConfiguration,
                previousRuntimeState,
                backup.OriginallyEnabled,
                backup.XmlSha256,
                exactBackupXmlMatch: true,
                definitionMatchesBackup: true);
            var transition = new TakeoverTransitionJournal(
                TakeoverTransitionJournal.CurrentSchemaVersion,
                Guid.NewGuid(),
                TakeoverTransitionOperation.Commit,
                TakeoverTransitionStage.Prepared,
                peer.CallerSid,
                backup.XmlSha256,
                backup.OriginallyEnabled,
                StartMythCoolRequested: false,
                transitionStartedAt,
                CreatePendingTakeoverAudit(
                    "commission.commit",
                    peer.CallerSid,
                    peer.Source,
                    previousAuditSnapshot));
            var disableAttempted = false;
            var taskDisableConfirmed = false;
            var businessStateFinalized = false;
            try
            {
                await transitionJournalStore.PrepareAsync(transition, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                transition = await transitionJournalStore.AdvanceAsync(
                        transition,
                        transition with { Stage = TakeoverTransitionStage.TaskDisableInProgress },
                        CancellationToken.None)
                    .ConfigureAwait(false);
                disableAttempted = true;
                await taskManager.DisableAsync(CancellationToken.None).ConfigureAwait(false);
                taskDisableConfirmed = true;
                // Once the mutating command starts, caller cancellation is no longer allowed
                // to strand a definite readback behind the write-ahead stage.
                transition = await transitionJournalStore.AdvanceAsync(
                        transition,
                        transition with { Stage = TakeoverTransitionStage.TaskDisabled },
                        CancellationToken.None)
                    .ConfigureAwait(false);

                var commissionedConfiguration = configuration with { Commissioned = true };
                var commissionedState = new ServiceRuntimeState(
                    true,
                    peer.CallerSid,
                    transition.StartedAtUtc);
                await runtimeStateStore.SaveAsync(commissionedState, CancellationToken.None).ConfigureAwait(false);
                transition = await transitionJournalStore.AdvanceAsync(
                        transition,
                        transition with { Stage = TakeoverTransitionStage.RuntimeStatePersisted },
                        CancellationToken.None)
                    .ConfigureAwait(false);
                await configurationStore.SaveAsync(commissionedConfiguration, CancellationToken.None)
                    .ConfigureAwait(false);
                transition = await transitionJournalStore.AdvanceAsync(
                        transition,
                        transition with { Stage = TakeoverTransitionStage.ConfigurationPersisted },
                        CancellationToken.None)
                    .ConfigureAwait(false);

                var persistedConfiguration = await configurationStore.LoadAsync(CancellationToken.None)
                    .ConfigureAwait(false);
                var persistedRuntime = await runtimeStateStore.LoadAsync(CancellationToken.None)
                    .ConfigureAwait(false);
                var finalTaskInspection = await taskManager.InspectCurrentAgainstBackupAsync(
                        backup,
                        CancellationToken.None)
                    .ConfigureAwait(false);
                if (!RecoveryConfigurationMatches(commissionedConfiguration, persistedConfiguration) ||
                    persistedRuntime != commissionedState ||
                    finalTaskInspection.Enabled ||
                    !finalTaskInspection.DefinitionMatchesBackup)
                {
                    throw new InvalidDataException(
                        "Commissioning commit did not reach its exact durable controller/task state.");
                }

                businessStateFinalized = true;
                var currentAuditSnapshot = CreateTakeoverAuditSnapshot(
                    persistedConfiguration,
                    persistedRuntime,
                    finalTaskInspection.Enabled,
                    finalTaskInspection.CurrentXmlSha256,
                    finalTaskInspection.ExactBackupXmlMatch,
                    finalTaskInspection.DefinitionMatchesBackup);
                transition = await ResolveTakeoverTransitionAsync(
                        transition,
                        TakeoverTransitionAuditResult.Success,
                        TakeoverTransitionAuditAction.CommitCompleted,
                        currentAuditSnapshot,
                        resolver: null,
                        CancellationToken.None)
                    .ConfigureAwait(false);

                configuration = commissionedConfiguration;
                runtimeState = commissionedState;
                device = attached.Device;
                lastDeviceState = safeReadback;
                lastSuccessfulRead = timeProvider.GetUtcNow();
                lastSuccessfulWrite = lastSuccessfulRead;
                lastWrittenPercent = 100;
                commissioningSession = null;
                policy.Reset();
                UpdateStatus("Starting");
                await FinishTakeoverTransitionAsync(
                        transition,
                        backup,
                        "direct-commit",
                        CancellationToken.None)
                    .ConfigureAwait(false);
                return new { commissioned = true, failSafePercent = 100 };
            }
            catch (Exception commitException)
            {
                if (businessStateFinalized)
                {
                    configurationError =
                        "Commissioning state is durable, but its audit transaction is still pending. " +
                        "The takeover journal was retained and normal control is blocked until recovery.";
                    hardwareAccessBlocked = true;
                    device = attached.Device;
                    commissioningSession = null;
                    lastDeviceState = safeReadback;
                    lastWrittenPercent = 100;
                    lastSuccessfulWrite = timeProvider.GetUtcNow();
                    UpdateStatus("TakeoverAuditPending", FailSafeReason.ConfigurationInvalid);
                    throw new ControllerOperationException(
                        PipeErrorCodes.InternalError,
                        configurationError,
                        commitException);
                }

                if (disableAttempted && !taskDisableConfirmed)
                {
                    // schtasks may still be capable of applying a late /Disable. Never start a
                    // compensating /Enable that the orphan could subsequently overwrite. Keep
                    // the verified device, session and process-owned writer lease at 100% until
                    // this process exits; the TaskDisableInProgress journal is the only durable truth.
                    await BestEffortWriteAsync(attached.Device, 100).ConfigureAwait(false);
                    configuration = previousConfiguration;
                    runtimeState = previousRuntimeState;
                    lastDeviceState = safeReadback;
                    lastSuccessfulWrite = timeProvider.GetUtcNow();
                    lastWrittenPercent = 100;
                    configurationError =
                        "The Myth.Cool scheduled-task mutation did not reach a durable confirmed " +
                        "state. No compensation was started; the transition journal, device handle " +
                        "and writer lease are retained until process exit. A service restart alone " +
                        "will not clear the journal; attended task-state recovery is required.";
                    hardwareAccessBlocked = true;
                    requiresProcessRestartForRecovery = true;
                    MarkHardwarePoisoned(
                        "TASK_COMMAND_OUTCOME_UNCERTAIN",
                        "Myth.Cool scheduled-task takeover command",
                        commitException);
                }
                else if (!disableAttempted)
                {
                    configuration = previousConfiguration;
                    runtimeState = previousRuntimeState;
                    if (ReferenceEquals(commissioningSession, session))
                    {
                        await AbortCommissioningForSafetyAsync(
                            session,
                            CreateCommissioningFailureIssue(commitException, "CommitTransitionStart"),
                            CancellationToken.None).ConfigureAwait(false);
                    }
                }
                else
                {
                    await BestEffortWriteAsync(attached.Device, 100).ConfigureAwait(false);
                    var compensationSucceeded = false;
                    var controllerStateDecommissioned = false;
                    var sessionReleaseAttempted = false;
                    var sessionSafelyReleased = false;
                    var taskRestoreStarted = false;
                    var taskRestoreOutcomeUncertain = false;
                    try
                    {
                        var rollbackTransition = transition with
                        {
                            Operation = TakeoverTransitionOperation.Rollback,
                            Stage = TakeoverTransitionStage.Prepared,
                        };
                        _ = await RequireMatchingBackupAsync(rollbackTransition, CancellationToken.None)
                            .ConfigureAwait(false);
                        rollbackTransition = await transitionJournalStore.AdvanceAsync(
                                transition,
                                rollbackTransition,
                                CancellationToken.None)
                            .ConfigureAwait(false);

                        await configurationStore.SaveAsync(previousConfiguration, CancellationToken.None)
                            .ConfigureAwait(false);
                        await runtimeStateStore.SaveAsync(previousRuntimeState, CancellationToken.None)
                            .ConfigureAwait(false);
                        rollbackTransition = await transitionJournalStore.AdvanceAsync(
                                rollbackTransition,
                                rollbackTransition with
                                {
                                    Stage = TakeoverTransitionStage.ControllerStateDecommissioned,
                                },
                                CancellationToken.None)
                            .ConfigureAwait(false);

                        configuration = previousConfiguration;
                        runtimeState = previousRuntimeState;
                        controllerStateDecommissioned = true;
                        sessionReleaseAttempted = true;
                        sessionSafelyReleased = await DisposeCommissioningSessionAsync().ConfigureAwait(false);
                        if (!sessionSafelyReleased)
                        {
                            ThrowIfHardwarePoisoned();
                        }

                        if (!await ReleaseDeviceAsync().ConfigureAwait(false))
                        {
                            ThrowIfHardwarePoisoned();
                        }

                        rollbackTransition = await transitionJournalStore.AdvanceAsync(
                                rollbackTransition,
                                rollbackTransition with
                                {
                                    Stage = TakeoverTransitionStage.TaskRestoreInProgress,
                                },
                                CancellationToken.None)
                            .ConfigureAwait(false);
                        taskRestoreStarted = true;
                        await taskManager.RestoreAsync(startMythCool: false, CancellationToken.None)
                            .ConfigureAwait(false);
                        rollbackTransition = await transitionJournalStore.AdvanceAsync(
                                rollbackTransition,
                                rollbackTransition with
                                {
                                    Stage = TakeoverTransitionStage.TaskRestored,
                                },
                                CancellationToken.None)
                            .ConfigureAwait(false);
                        var restoredInspection = await taskManager.InspectCurrentAgainstBackupAsync(
                                backup,
                                CancellationToken.None)
                            .ConfigureAwait(false);
                        if (restoredInspection.Enabled != backup.OriginallyEnabled ||
                            !restoredInspection.DefinitionMatchesBackup)
                        {
                            throw new InvalidDataException(
                                "Commit compensation did not restore the exact Myth.Cool task state.");
                        }

                        var compensationCurrent = CreateTakeoverAuditSnapshot(
                            previousConfiguration,
                            previousRuntimeState,
                            restoredInspection.Enabled,
                            restoredInspection.CurrentXmlSha256,
                            restoredInspection.ExactBackupXmlMatch,
                            restoredInspection.DefinitionMatchesBackup);
                        rollbackTransition = await ResolveTakeoverTransitionAsync(
                                rollbackTransition,
                                TakeoverTransitionAuditResult.Compensated,
                                TakeoverTransitionAuditAction.CommitFailedRolledBack,
                                compensationCurrent,
                                resolver: null,
                                CancellationToken.None)
                            .ConfigureAwait(false);
                        await FinishTakeoverTransitionAsync(
                                rollbackTransition,
                                backup,
                                "commit-failure-compensation",
                                CancellationToken.None)
                            .ConfigureAwait(false);
                        compensationSucceeded = true;
                    }
                    catch (Exception compensationException)
                    {
                        taskRestoreOutcomeUncertain = taskRestoreStarted;
                        logger.LogCritical(compensationException,
                            "Could not complete durable rollback after a failed commissioning commit.");
                    }

                    if (!compensationSucceeded)
                    {
                        var canRetainControllerOwnership =
                            taskDisableConfirmed &&
                            !taskRestoreStarted &&
                            !sessionReleaseAttempted &&
                            !controllerStateDecommissioned &&
                            !IsHardwarePoisoned &&
                            writerLease.IsHeld &&
                            ReferenceEquals(commissioningSession, session) &&
                            ReferenceEquals(session.Device, attached.Device);

                        if (canRetainControllerOwnership)
                        {
                            // The device, lease and disabled-task proof remain intact. Keep
                            // ownership in this process until journal recovery can decide it.
                            configuration = previousConfiguration with { Commissioned = true };
                            runtimeState = new ServiceRuntimeState(
                                true, peer.CallerSid, timeProvider.GetUtcNow());
                            configurationError =
                                "Commit compensation failed before device release; controller ownership " +
                                "is retained at 100%, Myth.Cool remains disabled, and the recovery journal is retained.";
                            device = attached.Device;
                            lastDeviceState = safeReadback;
                            commissioningSession = null;
                            lastWrittenPercent = 100;
                            lastSuccessfulWrite = timeProvider.GetUtcNow();
                        }
                        else
                        {
                            // Once release has begun or task restoration has started, neither
                            // controller ownership nor the task's final state may be claimed. If
                            // an attached session remains, release it wherever safety is provable.
                            if (!sessionSafelyReleased &&
                                ReferenceEquals(commissioningSession, session) &&
                                !IsHardwarePoisoned)
                            {
                                sessionReleaseAttempted = true;
                                sessionSafelyReleased = await DisposeCommissioningSessionAsync()
                                    .ConfigureAwait(false);
                            }

                            configuration = previousConfiguration with { Commissioned = false };
                            runtimeState = ServiceRuntimeState.Initial;
                            configurationError =
                                "Commit compensation failed after controller ownership was released " +
                                "or scheduled-task restoration became uncertain; the recovery journal " +
                                "is retained and hardware access is blocked.";
                            lastDeviceState = null;
                        }

                        hardwareAccessBlocked = true;
                        if (taskRestoreOutcomeUncertain)
                        {
                            requiresProcessRestartForRecovery = true;
                            Environment.ExitCode = 7;
                        }

                        UpdateStatus("CommitCompensationFailed", FailSafeReason.ConfigurationInvalid);
                    }
                }

                throw new ControllerOperationException(PipeErrorCodes.InternalError,
                    "Commissioning commit failed and safety rollback was attempted.", commitException);
            }
        }
        catch (ControllerOperationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          InvalidOperationException or InvalidDataException)
        {
            throw new ControllerOperationException(PipeErrorCodes.InternalError,
                "Commissioning could not be committed; the Myth.Cool task was left or restored to its prior state.",
                exception);
        }
        finally
        {
            MarkHeartbeat();
            gate.Release();
        }
    }

    public Task<object> RollbackAsync(
        bool startMythCool,
        bool installer,
        PipePeerIdentity peer,
        CancellationToken cancellationToken) =>
        RollbackAsync(startMythCool, installer, attended: false, peer, cancellationToken);

    public async Task<object> RollbackAsync(
        bool startMythCool,
        bool installer,
        bool attended,
        PipePeerIdentity peer,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var rollbackOwnershipReleased = false;
        var rollbackTaskRestoreStarted = false;
        TakeoverTransitionStage? rollbackRecoveryStage = null;
        try
        {
            EnsureInitialized();
            ThrowIfHardwarePoisoned();
            if (forcedUninstallRecoveryMarkerError is not null)
            {
                throw new ControllerOperationException(
                    PipeErrorCodes.InternalError,
                    "The forced-uninstall recovery marker is unreadable or invalid. It was " +
                    "preserved; repair the marker evidence manually before retrying rollback.");
            }

            if (forcedUninstallRecoveryMarker is { } recoveryMarker)
            {
                RequireAttended(attended, "Forced-uninstall recovery");
                return await RecoverForcedUninstallAsync(
                        recoveryMarker,
                        startMythCool,
                        installer,
                        peer,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            ThrowIfHardwareAccessBlocked();

            var pendingTransition = await transitionJournalStore.LoadAsync(cancellationToken)
                .ConfigureAwait(false);
            if (pendingTransition is not null)
            {
                await RecoverPendingTakeoverTransitionAsync(cancellationToken).ConfigureAwait(false);
                configurationError = null;
                hardwareAccessBlocked = false;
            }

            var alreadyUncommissioned = !configuration.Commissioned && !runtimeState.Commissioned;
            CommissioningSession? previousSession = null;
            if (alreadyUncommissioned)
            {
                if (hardwareAccessBlocked)
                {
                    throw new InvalidOperationException(
                        "The previous takeover transition is unresolved; idempotent rollback is not safe.");
                }

                previousSession = commissioningSession;
                if (previousSession is { } pendingSession)
                {
                    var cleanup = await CleanupCommissioningSessionAsync(pendingSession).ConfigureAwait(false);
                    if (!cleanup.SessionReleased)
                    {
                        ThrowIfHardwarePoisoned();
                    }
                }

                if (!await ReleaseDeviceAsync().ConfigureAwait(false))
                {
                    ThrowIfHardwarePoisoned();
                }
                policy.Reset();
            }

            var backup = await taskManager.TryReadVerifiedBackupAsync(cancellationToken).ConfigureAwait(false);
            if (backup is null)
            {
                if (alreadyUncommissioned && !startMythCool)
                {
                    UpdateStatus("NotCommissioned", FailSafeReason.NotCommissioned);
                    await SafeAuditAsync(
                        "commission.rollback",
                        "success",
                        peer.CallerSid,
                        installer ? "installer" : peer.Source,
                        new
                        {
                            previous = new
                            {
                                commissioned = false,
                                ownerSid = runtimeState.OwnerSid,
                                sessionActive = previousSession is not null,
                                sessionStage = previousSession?.Stage.ToString(),
                            },
                            current = new
                            {
                                commissioned = false,
                                ownerSid = (string?)null,
                                sessionActive = false,
                                sessionStage = (string?)null,
                            },
                            restoredTask = false,
                            startMythCool = false,
                        },
                        CancellationToken.None).ConfigureAwait(false);
                    return new
                    {
                        commissioned = false,
                        restoredTask = false,
                        startedMythCool = false,
                    };
                }

                throw new ControllerOperationException(PipeErrorCodes.InvalidArgument,
                    "Rollback requires a verified Myth.Cool scheduled-task backup.");
            }

            if (startMythCool && !backup.OriginallyEnabled)
            {
                throw new ControllerOperationException(PipeErrorCodes.InvalidArgument,
                    "Myth.Cool was disabled before takeover and cannot be started implicitly.");
            }

            if (alreadyUncommissioned)
            {
                var inspection = await taskManager.InspectCurrentAgainstBackupAsync(
                        backup,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!inspection.DefinitionMatchesBackup)
                {
                    throw new InvalidDataException(
                        "The current Myth.Cool task definition differs from the verified rollback backup.");
                }

                if (inspection.Enabled == backup.OriginallyEnabled && !startMythCool)
                {
                    UpdateStatus("NotCommissioned", FailSafeReason.NotCommissioned);
                    await SafeAuditAsync(
                        "commission.rollback",
                        "success",
                        peer.CallerSid,
                        installer ? "installer" : peer.Source,
                        new
                        {
                            previous = new
                            {
                                commissioned = false,
                                ownerSid = runtimeState.OwnerSid,
                                mythCoolTaskEnabled = inspection.Enabled,
                            },
                            current = new
                            {
                                commissioned = false,
                                ownerSid = (string?)null,
                                mythCoolTaskEnabled = inspection.Enabled,
                            },
                            restoredTask = true,
                            taskMutationPerformed = false,
                            startMythCool = false,
                        },
                        CancellationToken.None).ConfigureAwait(false);
                    return new
                    {
                        commissioned = false,
                        restoredTask = true,
                        startedMythCool = false,
                    };
                }
            }

            var previousConfiguration = configuration;
            var previousRuntimeState = runtimeState;
            var initialTaskInspection = await taskManager.InspectCurrentAgainstBackupAsync(
                    backup,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!initialTaskInspection.DefinitionMatchesBackup)
            {
                throw new InvalidDataException(
                    "The current Myth.Cool task definition differs from the verified rollback backup.");
            }

            var previousAuditSnapshot = CreateTakeoverAuditSnapshot(
                previousConfiguration,
                previousRuntimeState,
                initialTaskInspection.Enabled,
                initialTaskInspection.CurrentXmlSha256,
                initialTaskInspection.ExactBackupXmlMatch,
                initialTaskInspection.DefinitionMatchesBackup);
            var transition = new TakeoverTransitionJournal(
                TakeoverTransitionJournal.CurrentSchemaVersion,
                Guid.NewGuid(),
                TakeoverTransitionOperation.Rollback,
                TakeoverTransitionStage.Prepared,
                runtimeState.OwnerSid ?? peer.CallerSid,
                backup.XmlSha256,
                backup.OriginallyEnabled,
                startMythCool,
                timeProvider.GetUtcNow().ToUniversalTime(),
                CreatePendingTakeoverAudit(
                    "commission.rollback",
                    peer.CallerSid,
                    installer ? "installer" : peer.Source,
                    previousAuditSnapshot));
            _ = await RequireMatchingBackupAsync(transition, cancellationToken).ConfigureAwait(false);
            await transitionJournalStore.PrepareAsync(transition, cancellationToken).ConfigureAwait(false);

            if (device is not null)
            {
                await BestEffortWriteAsync(device, 100).ConfigureAwait(false);
            }

            var uncommissioned = configuration with { Commissioned = false };
            await configurationStore.SaveAsync(uncommissioned, CancellationToken.None).ConfigureAwait(false);
            await runtimeStateStore.SaveAsync(ServiceRuntimeState.Initial, CancellationToken.None)
                .ConfigureAwait(false);
            transition = await transitionJournalStore.AdvanceAsync(
                    transition,
                    transition with { Stage = TakeoverTransitionStage.ControllerStateDecommissioned },
                    CancellationToken.None)
                .ConfigureAwait(false);
            rollbackRecoveryStage = TakeoverTransitionStage.ControllerStateDecommissioned;

            configuration = uncommissioned;
            runtimeState = ServiceRuntimeState.Initial;
            if (!await DisposeCommissioningSessionAsync().ConfigureAwait(false))
            {
                ThrowIfHardwarePoisoned();
            }

            if (!await ReleaseDeviceAsync().ConfigureAwait(false))
            {
                ThrowIfHardwarePoisoned();
            }

            rollbackOwnershipReleased =
                commissioningSession is null && device is null && !writerLease.IsHeld;
            if (!rollbackOwnershipReleased)
            {
                throw new InvalidOperationException(
                    "Rollback could not prove that controller hardware ownership was released.");
            }

            policy.Reset();

            cancellationToken.ThrowIfCancellationRequested();
            // From this assignment onward even an ambiguous journal write must be treated as
            // potentially durable: automatic recovery cannot prove whether a mutation followed.
            rollbackRecoveryStage = TakeoverTransitionStage.TaskRestoreInProgress;
            transition = await transitionJournalStore.AdvanceAsync(
                    transition,
                    transition with { Stage = TakeoverTransitionStage.TaskRestoreInProgress },
                    CancellationToken.None)
                .ConfigureAwait(false);
            rollbackTaskRestoreStarted = true;
            await taskManager.RestoreAsync(startMythCool, CancellationToken.None).ConfigureAwait(false);
            transition = await transitionJournalStore.AdvanceAsync(
                    transition,
                    transition with { Stage = TakeoverTransitionStage.TaskRestored },
                    CancellationToken.None)
                .ConfigureAwait(false);
            rollbackRecoveryStage = TakeoverTransitionStage.TaskRestored;

            var finalConfiguration = await configurationStore.LoadAsync(CancellationToken.None)
                .ConfigureAwait(false);
            var finalRuntime = await runtimeStateStore.LoadAsync(CancellationToken.None)
                .ConfigureAwait(false);
            var finalTaskInspection = await taskManager.InspectCurrentAgainstBackupAsync(
                    backup,
                    CancellationToken.None)
                .ConfigureAwait(false);
            if (!RecoveryConfigurationMatches(uncommissioned, finalConfiguration) ||
                finalRuntime != ServiceRuntimeState.Initial ||
                finalTaskInspection.Enabled != backup.OriginallyEnabled ||
                !finalTaskInspection.DefinitionMatchesBackup)
            {
                throw new InvalidDataException(
                    "Rollback did not reach its exact durable controller/task state.");
            }

            var currentAuditSnapshot = CreateTakeoverAuditSnapshot(
                finalConfiguration,
                finalRuntime,
                finalTaskInspection.Enabled,
                finalTaskInspection.CurrentXmlSha256,
                finalTaskInspection.ExactBackupXmlMatch,
                finalTaskInspection.DefinitionMatchesBackup);
            transition = await ResolveTakeoverTransitionAsync(
                    transition,
                    TakeoverTransitionAuditResult.Success,
                    TakeoverTransitionAuditAction.RollbackCompleted,
                    currentAuditSnapshot,
                    resolver: null,
                    CancellationToken.None)
                .ConfigureAwait(false);
            rollbackRecoveryStage = TakeoverTransitionStage.OutcomeResolved;

            configurationError = null;
            hardwareAccessBlocked = false;
            UpdateStatus("NotCommissioned", FailSafeReason.NotCommissioned);
            await FinishTakeoverTransitionAsync(
                    transition,
                    backup,
                    "direct-rollback",
                    CancellationToken.None)
                .ConfigureAwait(false);
            return new { commissioned = false, restoredTask = true, startedMythCool = startMythCool };
        }
        catch (Exception exception) when (rollbackOwnershipReleased || rollbackTaskRestoreStarted)
        {
            LatchRollbackRecoveryRequired(rollbackRecoveryStage);
            if (exception is ControllerOperationException or OperationCanceledException)
            {
                throw;
            }

            throw new ControllerOperationException(PipeErrorCodes.InternalError,
                configurationError ??
                "Rollback released controller ownership, but durable recovery did not complete.",
                exception);
        }
        catch (ControllerOperationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          InvalidOperationException or InvalidDataException)
        {
            throw new ControllerOperationException(PipeErrorCodes.InternalError,
                "Rollback did not complete. The controller remains fail-closed; inspect diagnostics before retrying.",
                exception);
        }
        finally
        {
            MarkHeartbeat();
            gate.Release();
        }
    }

    public async Task<object> RecoverTaskTransitionAsync(
        bool attended,
        PipePeerIdentity peer,
        CancellationToken cancellationToken)
    {
        RequireAttended(attended);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureInitialized();
            if (invalidConfigurationWithPendingJournal)
            {
                throw new ControllerOperationException(
                    PipeErrorCodes.InternalError,
                    "Attended task recovery is unavailable because the controller configuration " +
                    "cannot be validated. The journal was preserved without task access.");
            }

            if (requiresProcessRestartForRecovery || IsHardwarePoisoned || everOpened ||
                commissioningSession is not null || device is not null || writerLease.IsHeld ||
                hidDispatcher.HasPendingOperations || thermalDispatcher.HasPendingOperations)
            {
                throw new ControllerOperationException(
                    PipeErrorCodes.InternalError,
                    "Attended task recovery requires a fresh service process with no current or " +
                    "previous HID ownership. Restart the service, keep Myth.Cool closed, and retry.");
            }

            var transition = await transitionJournalStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (transition is null)
            {
                throw Invalid("No takeover task transition is waiting for attended recovery.");
            }

            var expectedInProgressStage = transition.Operation switch
            {
                TakeoverTransitionOperation.Commit => TakeoverTransitionStage.TaskDisableInProgress,
                TakeoverTransitionOperation.Rollback => TakeoverTransitionStage.TaskRestoreInProgress,
                _ => throw Invalid("The takeover task transition operation is unsupported."),
            };
            if (transition.Stage != expectedInProgressStage)
            {
                throw Invalid(
                    "Attended task recovery is allowed only for an existing " +
                    "TaskDisableInProgress or TaskRestoreInProgress journal.");
            }

            var backup = await RequireMatchingBackupAsync(transition, cancellationToken).ConfigureAwait(false);
            var persistedConfiguration = await configurationStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            var persistedRuntimeState = await runtimeStateStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (!RecoveryConfigurationMatches(configuration, persistedConfiguration) ||
                runtimeState != persistedRuntimeState ||
                persistedConfiguration.Commissioned ||
                persistedRuntimeState != ServiceRuntimeState.Initial)
            {
                await AuditTaskRecoveryAsync(
                    "refused",
                    "controller-state-mismatch",
                    transition,
                    inspection: null,
                    peer,
                    CancellationToken.None).ConfigureAwait(false);
                throw new ControllerOperationException(
                    PipeErrorCodes.InternalError,
                    "Configuration and runtime state are not the exact decommissioned state required " +
                    "for attended task recovery. The journal was preserved.");
            }

            var inspection = await taskManager.InspectCurrentAgainstBackupAsync(backup, cancellationToken)
                .ConfigureAwait(false);
            string action;
            TakeoverTransitionAuditResult auditResult;
            TakeoverTransitionAuditAction auditAction;
            switch (transition.Stage)
            {
                case TakeoverTransitionStage.TaskDisableInProgress
                    when transition.TaskOriginallyEnabled &&
                         inspection.Enabled &&
                         inspection.ExactBackupXmlMatch:
                    // The intended /Disable produced no externally observable change. The exact
                    // original task and both decommissioned state files prove takeover never began.
                    action = "commitAbandonedOriginalState";
                    auditResult = TakeoverTransitionAuditResult.NotApplied;
                    auditAction = TakeoverTransitionAuditAction.CommitAbandonedOriginalState;
                    break;

                case TakeoverTransitionStage.TaskDisableInProgress
                    when !inspection.Enabled && inspection.DefinitionMatchesBackup:
                    // /Disable completed, including the originally-disabled idempotent case. Advance
                    // only the local journal, then use the read-only attended commit closure.
                    transition = await transitionJournalStore.AdvanceAsync(
                            transition,
                            transition with { Stage = TakeoverTransitionStage.TaskDisabled },
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    transition = await CompleteConfirmedCommitRecoveryAsync(
                            transition,
                            backup,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    action = "commitCompleted";
                    auditResult = TakeoverTransitionAuditResult.Success;
                    auditAction = TakeoverTransitionAuditAction.CommitCompleted;
                    break;

                case TakeoverTransitionStage.TaskRestoreInProgress
                    when inspection.Enabled == transition.TaskOriginallyEnabled &&
                         inspection.DefinitionMatchesBackup:
                    // The verified definition plus Enabled state proves restoration completed.
                    // Never replay /Run, even if the interrupted rollback had requested it.
                    transition = await transitionJournalStore.AdvanceAsync(
                            transition,
                            transition with { Stage = TakeoverTransitionStage.TaskRestored },
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    transition = await CompleteConfirmedRollbackRecoveryAsync(
                            transition,
                            backup,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    action = "rollbackCompleted";
                    auditResult = TakeoverTransitionAuditResult.Success;
                    auditAction = TakeoverTransitionAuditAction.RollbackCompleted;
                    break;

                case TakeoverTransitionStage.TaskRestoreInProgress
                    when transition.TaskOriginallyEnabled &&
                         !inspection.Enabled &&
                         inspection.DefinitionMatchesBackup:
                    var conflict = conflictDetector.Inspect();
                    if (conflict.HasConflict)
                    {
                        await AuditTaskRecoveryAsync(
                            "refused",
                            conflict.InspectionFailed
                                ? "vendor-process-inspection-failed"
                                : "vendor-controller-running",
                            transition,
                            inspection,
                            peer,
                            CancellationToken.None).ConfigureAwait(false);
                        throw new ControllerOperationException(
                            PipeErrorCodes.MythCoolConflict,
                            FormatMythCoolConflictMessage(
                                conflict,
                                "Task restoration remains unconfirmed; compensation to controller ownership was refused."));
                    }

                    // Restoration did not change the exact task definition and the task remains
                    // disabled. Convert only the durable local transaction direction; automatic
                    // recovery can now safely re-establish controller ownership without a task write.
                    transition = await transitionJournalStore.AdvanceAsync(
                            transition,
                            transition with
                            {
                                Operation = TakeoverTransitionOperation.Commit,
                                Stage = TakeoverTransitionStage.TaskDisabled,
                            },
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    transition = await CompleteConfirmedCommitRecoveryAsync(
                            transition,
                            backup,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    action = "rollbackCompensatedToController";
                    auditResult = TakeoverTransitionAuditResult.Compensated;
                    auditAction = TakeoverTransitionAuditAction.RollbackCompensatedToController;
                    break;

                default:
                    await AuditTaskRecoveryAsync(
                        "refused",
                        "task-definition-or-state-mismatch",
                        transition,
                        inspection,
                        peer,
                        CancellationToken.None).ConfigureAwait(false);
                    throw new ControllerOperationException(
                        PipeErrorCodes.InternalError,
                        "The current Myth.Cool task XML or enabled state cannot prove either completion " +
                        "or safe compensation. The journal was preserved without mutation.");
            }

            var finalConfiguration = await configurationStore.LoadAsync(CancellationToken.None)
                .ConfigureAwait(false);
            var finalRuntime = await runtimeStateStore.LoadAsync(CancellationToken.None)
                .ConfigureAwait(false);
            var finalInspection = await taskManager.InspectCurrentAgainstBackupAsync(
                    backup,
                    CancellationToken.None)
                .ConfigureAwait(false);
            var currentAuditSnapshot = CreateTakeoverAuditSnapshot(
                finalConfiguration,
                finalRuntime,
                finalInspection.Enabled,
                finalInspection.CurrentXmlSha256,
                finalInspection.ExactBackupXmlMatch,
                finalInspection.DefinitionMatchesBackup);
            transition = await ResolveTakeoverTransitionAsync(
                    transition,
                    auditResult,
                    auditAction,
                    currentAuditSnapshot,
                    peer,
                    CancellationToken.None)
                .ConfigureAwait(false);
            await FinishTakeoverTransitionAsync(
                    transition,
                    backup,
                    "attended-task-transition-recovery",
                    CancellationToken.None)
                .ConfigureAwait(false);

            configurationError = null;
            hardwareAccessBlocked = false;
            requiresProcessRestartForRecovery = false;
            policy.Reset();
            if (configuration.Commissioned)
            {
                UpdateStatus("Starting");
            }
            else
            {
                UpdateStatus("NotCommissioned", FailSafeReason.NotCommissioned);
            }

            return new
            {
                recovered = true,
                action,
                commissioned = configuration.Commissioned,
                startedMythCool = false,
                task = new
                {
                    enabled = inspection.Enabled,
                    currentXmlSha256 = inspection.CurrentXmlSha256,
                    exactBackupXmlMatch = inspection.ExactBackupXmlMatch,
                    definitionMatchesBackup = inspection.DefinitionMatchesBackup,
                },
            };
        }
        catch (ControllerOperationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new ControllerOperationException(
                PipeErrorCodes.InternalError,
                "Attended task recovery could not establish a safe result. The journal was preserved; " +
                "no compensating task command was issued.",
                exception);
        }
        finally
        {
            MarkHeartbeat();
            gate.Release();
        }
    }

    private async Task<object> RecoverForcedUninstallAsync(
        ForcedUninstallRecoveryMarker expectedMarker,
        bool startMythCool,
        bool installer,
        PipePeerIdentity peer,
        CancellationToken cancellationToken)
    {
        if (startMythCool)
        {
            throw Invalid(
                "Forced-uninstall recovery never starts Myth.Cool. Complete and verify rollback first, " +
                "then start it separately if the backed-up task was originally enabled.");
        }

        if (configurationLoadInvalid || runtimeStateLoadInvalid || commissioningStateMismatch ||
            invalidConfigurationWithPendingJournal || auditMutationBlocked ||
            activeAuditMutation is not null || requiresProcessRestartForRecovery ||
            configurationError is not null)
        {
            throw new ControllerOperationException(
                PipeErrorCodes.InternalError,
                "Forced-uninstall recovery cannot bypass invalid configuration/runtime state, an " +
                "audit WAL, a takeover ambiguity, or another retained safety barrier.");
        }

        if (everOpened || commissioningSession is not null || device is not null || writerLease.IsHeld ||
            hidDispatcher.HasPendingOperations || IsHardwarePoisoned)
        {
            throw new ControllerOperationException(
                PipeErrorCodes.InternalError,
                "Forced-uninstall recovery requires a fresh process that has never opened HID. " +
                "The recovery path itself does not perform a fail-safe write or any device I/O.");
        }

        var durableMarker = await forcedUninstallRecoveryMarkerStore.LoadAsync(cancellationToken)
            .ConfigureAwait(false);
        if (durableMarker != expectedMarker)
        {
            throw new InvalidDataException(
                "The forced-uninstall recovery marker changed or disappeared during recovery.");
        }

        var pendingAuditMutation = await auditMutationJournalStore.LoadAsync(cancellationToken)
            .ConfigureAwait(false);
        if (pendingAuditMutation is not null)
        {
            activeAuditMutation = pendingAuditMutation;
            throw new InvalidDataException(
                "A durable audit mutation is pending; forced-uninstall rollback cannot bypass it.");
        }

        var persistedConfiguration = await configurationStore.LoadAsync(cancellationToken)
            .ConfigureAwait(false);
        var persistedRuntime = await runtimeStateStore.LoadAsync(cancellationToken)
            .ConfigureAwait(false);
        RequireExactForcedRecoveryControllerState(persistedConfiguration, persistedRuntime);

        var transition = await transitionJournalStore.LoadAsync(cancellationToken)
            .ConfigureAwait(false);
        if (transition is not null &&
            (transition.TransitionId != expectedMarker.MarkerId ||
             transition.Operation != TakeoverTransitionOperation.Rollback ||
             transition.StartMythCoolRequested ||
             !string.Equals(
                 transition.Audit.EventName,
                 "commission.rollback.forced-uninstall-recovery",
                 StringComparison.Ordinal)))
        {
            throw new InvalidDataException(
                "An unrelated or ambiguous takeover transition journal is present. It was preserved; " +
                "forced-uninstall recovery cannot bypass it.");
        }

        var backup = await taskManager.TryReadVerifiedBackupAsync(cancellationToken)
            .ConfigureAwait(false);
        if (backup is null)
        {
            if (transition is not null)
            {
                throw new InvalidDataException(
                    "The forced-uninstall rollback journal has no verified task backup.");
            }

            if (persistedConfiguration.Commissioned || persistedRuntime != ServiceRuntimeState.Initial)
            {
                throw new InvalidDataException(
                    "Controller ownership is still recorded, but no verified Myth.Cool task backup exists.");
            }

            await CompleteForcedUninstallRecoveryAsync(
                    expectedMarker,
                    persistedConfiguration,
                    persistedRuntime,
                    restoredTask: false,
                    taskMutationPerformed: false,
                    installer,
                    peer,
                    CancellationToken.None)
                .ConfigureAwait(false);
            return new
            {
                commissioned = false,
                restoredTask = false,
                startedMythCool = false,
                forcedUninstallRecoveryCleared = true,
            };
        }

        if (transition is not null &&
            (!string.Equals(transition.TaskBackupSha256, backup.XmlSha256,
                 StringComparison.OrdinalIgnoreCase) ||
             transition.TaskOriginallyEnabled != backup.OriginallyEnabled))
        {
            throw new InvalidDataException(
                "The verified task backup no longer matches the forced-uninstall rollback journal.");
        }

        var inspection = await taskManager.InspectCurrentAgainstBackupAsync(backup, cancellationToken)
            .ConfigureAwait(false);
        if (!inspection.DefinitionMatchesBackup)
        {
            throw new InvalidDataException(
                "The current Myth.Cool task definition drifted from the verified backup. " +
                "No task mutation or marker deletion was attempted.");
        }

        var forcedAuditAlreadyExists = await auditLog.ContainsTransactionAsync(
                expectedMarker.MarkerId,
                cancellationToken)
            .ConfigureAwait(false);
        if (forcedAuditAlreadyExists &&
            (transition is not null && transition.Stage is not (
                 TakeoverTransitionStage.OutcomeResolved or TakeoverTransitionStage.AuditAppended) ||
             transition is null &&
             (persistedConfiguration.Commissioned ||
              persistedRuntime != ServiceRuntimeState.Initial ||
              inspection.Enabled != backup.OriginallyEnabled)))
        {
            throw new InvalidDataException(
                "The forced-uninstall transaction identifier is already present in the audit log, " +
                "but the durable operation is not in its exact terminal state. The marker and any " +
                "journal were preserved without further mutation.");
        }

        if (transition is not null &&
            transition.Stage is TakeoverTransitionStage.OutcomeResolved or
                TakeoverTransitionStage.AuditAppended)
        {
            await FinalizeForcedTakeoverTransitionAsync(
                    transition,
                    expectedMarker,
                    backup,
                    "forced-uninstall-terminal-audit-recovery",
                    CancellationToken.None)
                .ConfigureAwait(false);
            return new
            {
                commissioned = false,
                restoredTask = true,
                startedMythCool = false,
                forcedUninstallRecoveryCleared = true,
            };
        }

        var taskMutationPerformed = false;
        if (transition is null &&
            !persistedConfiguration.Commissioned &&
            persistedRuntime == ServiceRuntimeState.Initial &&
            inspection.Enabled == backup.OriginallyEnabled)
        {
            // Already in the exact safe terminal state. The marker still requires a durable,
            // idempotent audit record before exact deletion.
        }
        else
        {
            if (transition is null)
            {
                var previousAuditSnapshot = CreateTakeoverAuditSnapshot(
                    persistedConfiguration,
                    persistedRuntime,
                    inspection.Enabled,
                    inspection.CurrentXmlSha256,
                    inspection.ExactBackupXmlMatch,
                    inspection.DefinitionMatchesBackup);
                transition = new TakeoverTransitionJournal(
                    TakeoverTransitionJournal.CurrentSchemaVersion,
                    expectedMarker.MarkerId,
                    TakeoverTransitionOperation.Rollback,
                    TakeoverTransitionStage.Prepared,
                    persistedRuntime.OwnerSid ?? peer.CallerSid,
                    backup.XmlSha256,
                    backup.OriginallyEnabled,
                    StartMythCoolRequested: false,
                    timeProvider.GetUtcNow().ToUniversalTime(),
                    CreatePendingTakeoverAudit(
                        "commission.rollback.forced-uninstall-recovery",
                        peer.CallerSid,
                        installer ? "installer" : peer.Source,
                        previousAuditSnapshot));
                await transitionJournalStore.PrepareAsync(transition, CancellationToken.None)
                    .ConfigureAwait(false);
            }

            if (transition.Stage == TakeoverTransitionStage.Prepared)
            {
                var decommissionedConfiguration = persistedConfiguration with { Commissioned = false };
                await configurationStore.SaveAsync(decommissionedConfiguration, CancellationToken.None)
                    .ConfigureAwait(false);
                await runtimeStateStore.SaveAsync(ServiceRuntimeState.Initial, CancellationToken.None)
                    .ConfigureAwait(false);
                var advanced = transition with
                {
                    Stage = TakeoverTransitionStage.ControllerStateDecommissioned,
                };
                transition = await transitionJournalStore.AdvanceAsync(
                        transition,
                        advanced,
                        CancellationToken.None)
                    .ConfigureAwait(false);
                configuration = decommissionedConfiguration;
                runtimeState = ServiceRuntimeState.Initial;
                persistedConfiguration = decommissionedConfiguration;
                persistedRuntime = ServiceRuntimeState.Initial;
            }

            if (transition.Stage == TakeoverTransitionStage.ControllerStateDecommissioned)
            {
                persistedConfiguration = await configurationStore.LoadAsync(CancellationToken.None)
                    .ConfigureAwait(false);
                persistedRuntime = await runtimeStateStore.LoadAsync(CancellationToken.None)
                    .ConfigureAwait(false);
                if (persistedConfiguration.Commissioned || persistedRuntime != ServiceRuntimeState.Initial)
                {
                    throw new InvalidDataException(
                        "The controller decommission state did not survive exact readback.");
                }

                inspection = await taskManager.InspectCurrentAgainstBackupAsync(
                        backup,
                        CancellationToken.None)
                    .ConfigureAwait(false);
                if (!inspection.DefinitionMatchesBackup)
                {
                    throw new InvalidDataException(
                        "The Myth.Cool task definition changed during forced-uninstall recovery.");
                }

                if (inspection.Enabled == backup.OriginallyEnabled)
                {
                    transition = await transitionJournalStore.AdvanceAsync(
                            transition,
                            transition with { Stage = TakeoverTransitionStage.TaskRestored },
                            CancellationToken.None)
                        .ConfigureAwait(false);
                }
                else
                {
                    transition = await transitionJournalStore.AdvanceAsync(
                            transition,
                            transition with
                            {
                                Stage = TakeoverTransitionStage.TaskRestoreInProgress,
                            },
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    await taskManager.RestoreAsync(startMythCool: false, CancellationToken.None)
                        .ConfigureAwait(false);
                    taskMutationPerformed = true;
                    inspection = await taskManager.InspectCurrentAgainstBackupAsync(
                            backup,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    if (!inspection.DefinitionMatchesBackup ||
                        inspection.Enabled != backup.OriginallyEnabled)
                    {
                        throw new InvalidDataException(
                            "The Myth.Cool task restore did not reach an exact verified final state.");
                    }

                    transition = await transitionJournalStore.AdvanceAsync(
                            transition,
                            transition with { Stage = TakeoverTransitionStage.TaskRestored },
                            CancellationToken.None)
                        .ConfigureAwait(false);
                }
            }
            else if (transition.Stage == TakeoverTransitionStage.TaskRestoreInProgress)
            {
                // This process did not create the in-progress barrier, so it must never replay
                // the ambiguous task mutation. Only a definitive final readback can advance it.
                if (!inspection.DefinitionMatchesBackup ||
                    inspection.Enabled != backup.OriginallyEnabled)
                {
                    throw new InvalidDataException(
                        "A prior task restore stopped at TaskRestoreInProgress and the final state " +
                        "cannot be proven. The journal and recovery marker were preserved.");
                }

                transition = await transitionJournalStore.AdvanceAsync(
                        transition,
                        transition with { Stage = TakeoverTransitionStage.TaskRestored },
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }

            if (transition.Stage != TakeoverTransitionStage.TaskRestored)
            {
                throw new InvalidDataException(
                    $"Unsupported forced-uninstall rollback stage {transition.Stage}.");
            }

            inspection = await taskManager.InspectCurrentAgainstBackupAsync(
                    backup,
                    CancellationToken.None)
                .ConfigureAwait(false);
            if (!inspection.DefinitionMatchesBackup ||
                inspection.Enabled != backup.OriginallyEnabled)
            {
                throw new InvalidDataException(
                    "The Myth.Cool task no longer matches the verified TaskRestored state.");
            }

            var finalUncommissioned = persistedConfiguration with { Commissioned = false };
            await configurationStore.SaveAsync(finalUncommissioned, CancellationToken.None)
                .ConfigureAwait(false);
            await runtimeStateStore.SaveAsync(ServiceRuntimeState.Initial, CancellationToken.None)
                .ConfigureAwait(false);
            configuration = finalUncommissioned;
            runtimeState = ServiceRuntimeState.Initial;
            persistedConfiguration = finalUncommissioned;
            persistedRuntime = ServiceRuntimeState.Initial;

            var finalInspection = await taskManager.InspectCurrentAgainstBackupAsync(
                    backup,
                    CancellationToken.None)
                .ConfigureAwait(false);
            var currentAuditSnapshot = CreateTakeoverAuditSnapshot(
                finalUncommissioned,
                ServiceRuntimeState.Initial,
                finalInspection.Enabled,
                finalInspection.CurrentXmlSha256,
                finalInspection.ExactBackupXmlMatch,
                finalInspection.DefinitionMatchesBackup);
            transition = await ResolveTakeoverTransitionAsync(
                    transition,
                    TakeoverTransitionAuditResult.Success,
                    TakeoverTransitionAuditAction.ForcedUninstallRollbackCompleted,
                    currentAuditSnapshot,
                    resolver: null,
                    CancellationToken.None)
                .ConfigureAwait(false);
            await FinalizeForcedTakeoverTransitionAsync(
                    transition,
                    expectedMarker,
                    backup,
                    "forced-uninstall-rollback",
                    CancellationToken.None)
                .ConfigureAwait(false);
            return new
            {
                commissioned = false,
                restoredTask = true,
                startedMythCool = false,
                forcedUninstallRecoveryCleared = true,
            };
        }

        await CompleteForcedUninstallRecoveryAsync(
                expectedMarker,
                persistedConfiguration,
                persistedRuntime,
                restoredTask: true,
                taskMutationPerformed,
                installer,
                peer,
                CancellationToken.None)
            .ConfigureAwait(false);
        return new
        {
            commissioned = false,
            restoredTask = true,
            startedMythCool = false,
            forcedUninstallRecoveryCleared = true,
        };
    }

    private async Task CompleteForcedUninstallRecoveryAsync(
        ForcedUninstallRecoveryMarker marker,
        ControllerConfiguration expectedConfiguration,
        ServiceRuntimeState expectedRuntime,
        bool restoredTask,
        bool taskMutationPerformed,
        bool installer,
        PipePeerIdentity peer,
        CancellationToken cancellationToken)
    {
        var finalConfiguration = await configurationStore.LoadAsync(cancellationToken)
            .ConfigureAwait(false);
        var finalRuntime = await runtimeStateStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (!RecoveryConfigurationMatches(expectedConfiguration, finalConfiguration) ||
            finalConfiguration.Commissioned || finalRuntime != ServiceRuntimeState.Initial ||
            expectedRuntime != ServiceRuntimeState.Initial)
        {
            throw new InvalidDataException(
                "The final uncommissioned controller state did not match durable readback.");
        }

        if (!await auditLog.ContainsTransactionAsync(marker.MarkerId, cancellationToken)
            .ConfigureAwait(false))
        {
            await auditLog.WriteAsync(
                    "commission.rollback.forced-uninstall-recovery",
                    "success",
                    peer.CallerSid,
                    installer ? "installer" : peer.Source,
                    new
                    {
                        markerId = marker.MarkerId,
                        markerReason = marker.Reason,
                        restoredTask,
                        taskMutationPerformed,
                        commissioned = false,
                        startedMythCool = false,
                    },
                    cancellationToken,
                    marker.MarkerId)
                .ConfigureAwait(false);
            if (!await auditLog.ContainsTransactionAsync(marker.MarkerId, cancellationToken)
                .ConfigureAwait(false))
            {
                throw new IOException(
                    "The forced-uninstall recovery audit record was not durably discoverable.");
            }
        }

        await forcedUninstallRecoveryMarkerStore.DeleteExactAsync(marker, cancellationToken)
            .ConfigureAwait(false);
        forcedUninstallRecoveryMarker = null;
        forcedUninstallRecoveryMarkerError = null;
        configuration = finalConfiguration;
        runtimeState = finalRuntime;
        hardwareAccessBlocked = false;
        configurationError = null;
        policy.Reset();
        UpdateStatus("NotCommissioned", FailSafeReason.NotCommissioned);
    }

    private async Task FinalizeForcedTakeoverTransitionAsync(
        TakeoverTransitionJournal transition,
        ForcedUninstallRecoveryMarker marker,
        ScheduledTaskBackupMetadata backup,
        string recoveryContext,
        CancellationToken cancellationToken)
    {
        if (transition.TransitionId != marker.MarkerId ||
            transition.Operation != TakeoverTransitionOperation.Rollback ||
            !string.Equals(
                transition.Audit.EventName,
                "commission.rollback.forced-uninstall-recovery",
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The forced-uninstall marker and takeover audit transaction do not match.");
        }

        var finalConfiguration = await configurationStore.LoadAsync(cancellationToken)
            .ConfigureAwait(false);
        var finalRuntime = await runtimeStateStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var finalInspection = await taskManager.InspectCurrentAgainstBackupAsync(backup, cancellationToken)
            .ConfigureAwait(false);
        if (finalConfiguration.Commissioned ||
            finalRuntime != ServiceRuntimeState.Initial ||
            !finalInspection.DefinitionMatchesBackup ||
            finalInspection.Enabled != backup.OriginallyEnabled)
        {
            throw new InvalidDataException(
                "The forced-uninstall rollback terminal state did not survive exact readback.");
        }

        transition = await EnsureTakeoverAuditAppendedAsync(
                transition,
                backup,
                recoveryContext,
                cancellationToken)
            .ConfigureAwait(false);

        // The forced marker is the strongest startup barrier. It is removed only after the
        // terminal audit is durable; the AuditAppended takeover journal remains as the exact
        // recovery proof until marker deletion itself has succeeded.
        await forcedUninstallRecoveryMarkerStore.DeleteExactAsync(marker, CancellationToken.None)
            .ConfigureAwait(false);
        await transitionJournalStore.DeleteExactAsync(transition, CancellationToken.None)
            .ConfigureAwait(false);

        forcedUninstallRecoveryMarker = null;
        forcedUninstallRecoveryMarkerError = null;
        configuration = finalConfiguration;
        runtimeState = finalRuntime;
        hardwareAccessBlocked = false;
        configurationError = null;
        policy.Reset();
        UpdateStatus("NotCommissioned", FailSafeReason.NotCommissioned);
    }

    private void RequireExactForcedRecoveryControllerState(
        ControllerConfiguration persistedConfiguration,
        ServiceRuntimeState persistedRuntime)
    {
        if (!RecoveryConfigurationMatches(configuration, persistedConfiguration) ||
            runtimeState != persistedRuntime)
        {
            throw new InvalidDataException(
                "The controller configuration or runtime state changed after initialization.");
        }

        if (persistedConfiguration.Commissioned != persistedRuntime.Commissioned ||
            (!persistedRuntime.Commissioned && persistedRuntime != ServiceRuntimeState.Initial) ||
            (persistedRuntime.Commissioned && string.IsNullOrWhiteSpace(persistedRuntime.OwnerSid)))
        {
            throw new InvalidDataException(
                "The controller configuration and runtime ownership state are inconsistent.");
        }
    }

    public async Task<object> AbortCommissioningAsync(
        Guid sessionId,
        PipePeerIdentity peer,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureInitialized();
            ThrowIfHardwarePoisoned();
            ThrowIfHardwareAccessBlocked();
            var session = commissioningSession;
            if (session is null || session.Id != sessionId)
            {
                throw Invalid("The commissioning session is missing or does not match.");
            }

            if (!string.Equals(session.OwnerSid, peer.CallerSid, StringComparison.OrdinalIgnoreCase))
            {
                throw Invalid("The commissioning session belongs to a different Windows identity.");
            }

            var operationKind = session.Device is not null && session.OriginalState is not null
                ? SafetyOperationKind.HidWithOn100Recovery
                : SafetyOperationKind.SessionOnly;
            var operationAudit = await BeginSafetyOperationAuditAsync(
                    PipeOperations.CommissionAbort,
                    peer.CallerSid,
                    peer.Source,
                    operationKind,
                    new
                    {
                        sessionId,
                        sessionActive = true,
                        stage = session.Stage.ToString(),
                        deviceAttached = session.Device is not null,
                    },
                    new
                    {
                        sessionId,
                        sessionActive = false,
                        requestedPercent = 100,
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            var cleanup = await CleanupCommissioningSessionAsync(session).ConfigureAwait(false);
            var failSafe = cleanup.FailSafe;
            policy.Reset();
            UpdateStatus("CommissioningAborted", FailSafeReason.NotCommissioned, session.ProbedIdentity);
            var dispositionConfirmed = operationKind == SafetyOperationKind.SessionOnly ||
                failSafe.ReadbackConfirmed;
            if (cleanup.SessionReleased && dispositionConfirmed)
            {
                await ResolveAndFinishSafetyOperationAuditAsync(
                    operationAudit,
                    SafetyOperationAuditOutcome.Success,
                    new
                    {
                        sessionId,
                        sessionActive = false,
                        requestedPercent = failSafe.RequestedPercent,
                        writeAttempted = failSafe.WriteAttempted,
                        writeSucceeded = failSafe.WriteSucceeded,
                        readbackConfirmed = failSafe.ReadbackConfirmed,
                        sessionReleased = cleanup.SessionReleased,
                        requiresProbe = true,
                    },
                    CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                var failure = new InvalidDataException(
                    "Commissioning abort could not prove both session release and the required ON-at-100 disposition.");
                LatchSafetyOperationAuditBlocked(
                    failure,
                    activeSafetyOperationAudit ?? operationAudit,
                    "commissioning abort disposition");
            }

            if (!cleanup.SessionReleased || !dispositionConfirmed)
            {
                ThrowIfHardwarePoisoned();
                throw new ControllerOperationException(PipeErrorCodes.HidTimeout,
                    "The commissioning session could not be safely released at a confirmed ON-at-100 state; the service must restart.");
            }

            return new
            {
                sessionId,
                aborted = true,
                requestedPercent = failSafe.RequestedPercent,
                writeAttempted = failSafe.WriteAttempted,
                writeSucceeded = failSafe.WriteSucceeded,
                readbackConfirmed = failSafe.ReadbackConfirmed,
                requiresProbe = true,
            };
        }
        finally
        {
            MarkHeartbeat();
            gate.Release();
        }
    }

    private IV12Device RequireShutdownQualificationPreconditions(IV12Device? expectedTarget = null)
    {
        if (!configuration.Commissioned && !runtimeState.Commissioned)
        {
            throw new ControllerOperationException(
                PipeErrorCodes.NotCommissioned,
                "Complete normal commissioning before qualifying the shutdown behavior.");
        }

        if (!configuration.Commissioned || !runtimeState.Commissioned ||
            string.IsNullOrWhiteSpace(runtimeState.OwnerSid))
        {
            throw new ControllerOperationException(
                PipeErrorCodes.InternalError,
                "Configuration and runtime ownership are not in one confirmed commissioned state.");
        }

        if (configurationError is not null || hardwareAccessBlocked ||
            invalidConfigurationWithPendingJournal || requiresProcessRestartForRecovery)
        {
            throw new ControllerOperationException(
                PipeErrorCodes.InternalError,
                "Controller ownership or configuration is not healthy enough for shutdown qualification.");
        }

        if (commissioningSession is not null)
        {
            throw Invalid("Shutdown qualification cannot run during a commissioning session.");
        }

        var target = device ?? throw new ControllerOperationException(
            PipeErrorCodes.DeviceNotFound,
            "The commissioned V12 Ultra does not currently have an exclusive open handle.");
        if (expectedTarget is not null && !ReferenceEquals(target, expectedTarget))
        {
            throw new ControllerOperationException(
                PipeErrorCodes.HidTimeout,
                "The exclusive V12 Ultra handle changed during shutdown qualification.");
        }

        if (!writerLease.IsHeld)
        {
            throw new ControllerOperationException(
                PipeErrorCodes.HidTimeout,
                "The service does not currently hold the single-writer lease.");
        }

        if (!target.Identity.IsSupportedV12Ultra || !DeviceAllowList.IsSupported(target.Identity))
        {
            throw new ControllerOperationException(
                PipeErrorCodes.UnsupportedDevice,
                "The current device handle is not the exact supported V12 Ultra identity.");
        }

        if (!policy.IsSettledForSystemShutdownOff ||
            lastDecision is not { FailSafeActive: false, FailSafeReason: FailSafeReason.None })
        {
            throw new ControllerOperationException(
                PipeErrorCodes.FailSafeActive,
                "Shutdown qualification requires normal control to have fully exited startup, reconnect, recovery, and emergency fail-safe states.");
        }

        RequireNoMythCoolConflictForShutdownQualification();
        return target;
    }

    private void RequireNoMythCoolConflictForShutdownQualification()
    {
        MythCoolConflictSnapshot conflict;
        try
        {
            conflict = conflictDetector.Inspect();
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception,
                "Myth.Cool conflict inspection failed during shutdown qualification.");
            throw new ControllerOperationException(
                PipeErrorCodes.MythCoolConflict,
                "Myth.Cool process state could not be verified; shutdown qualification was refused.",
                exception);
        }

        if (conflict.HasConflict)
        {
            throw new ControllerOperationException(
                PipeErrorCodes.MythCoolConflict,
                FormatMythCoolConflictMessage(
                    conflict,
                    "Close Myth.Cool before qualifying the shutdown behavior."));
        }
    }

    private void RequireShutdownQualificationThermal(ThermalSnapshot thermal)
    {
        var age = timeProvider.GetUtcNow() - thermal.CapturedAt;
        if (!thermal.CpuIsValid || thermal.ControlCelsius is null ||
            thermal.CapturedAt == DateTimeOffset.MinValue ||
            age < TimeSpan.Zero || age > TimeSpan.FromSeconds(10))
        {
            throw new ControllerOperationException(
                PipeErrorCodes.TelemetryStale,
                "Shutdown qualification requires a fresh, valid CPU and control temperature.");
        }

        if (thermal.ControlCelsius >= 60)
        {
            throw new ControllerOperationException(
                PipeErrorCodes.FailSafeActive,
                "Shutdown qualification is allowed only while the control temperature is below 60 C.");
        }
    }

    private ShutdownQualificationPending? GetActiveShutdownQualificationPending()
    {
        var pending = Volatile.Read(ref shutdownQualificationPending);
        if (pending is null || pending.ExpiresAtMonotonic > GetMonotonicNow())
        {
            return pending;
        }

        _ = Interlocked.CompareExchange(ref shutdownQualificationPending, null, pending);
        return null;
    }

    private void RequirePendingShutdownQualificationStillMatches(
        ShutdownQualificationPending pending,
        IV12Device target)
    {
        if (pending.ExpiresAtMonotonic <= GetMonotonicNow())
        {
            _ = Interlocked.CompareExchange(ref shutdownQualificationPending, null, pending);
            throw Invalid("The pending shutdown qualification expired; run the attended test again.");
        }

        if (!ReferenceEquals(target, pending.Target) || target.Identity != pending.Identity)
        {
            _ = Interlocked.CompareExchange(ref shutdownQualificationPending, null, pending);
            throw new ControllerOperationException(
                PipeErrorCodes.UnsupportedDevice,
                "The exact device handle changed after shutdown qualification; run the test again.");
        }

        if (!string.Equals(runtimeState.OwnerSid, pending.RuntimeOwnerSid,
                StringComparison.OrdinalIgnoreCase) ||
            !RecoveryConfigurationMatches(pending.Configuration, configuration))
        {
            _ = Interlocked.CompareExchange(ref shutdownQualificationPending, null, pending);
            throw new ControllerOperationException(
                PipeErrorCodes.InternalError,
                "Controller ownership or configuration changed after shutdown qualification; run the test again.");
        }
    }

    private async Task<ShutdownQualificationTransactionResult> ExecuteShutdownQualificationTransactionAsync(
        IV12Device target,
        IV12SystemShutdownDevice shutdownDevice,
        ShutdownQualificationCancellationGate cancellationGate,
        DeviceIdentity expectedIdentity,
        string expectedOwnerSid,
        CancellationToken preOffCancellationToken)
    {
        V12DeviceState? lightingBaseline = lastDeviceState;
        try
        {
            preOffCancellationToken.ThrowIfCancellationRequested();
            var before = await ExecuteHostStopHidStageAsync(
                    token => target.ReadStateAsync(token), preOffCancellationToken)
                .ConfigureAwait(false);
            lightingBaseline = before;

            preOffCancellationToken.ThrowIfCancellationRequested();
            await ExecuteHostStopHidStageAsync(
                    async token =>
                    {
                        ThrowIfAutomaticOutputStopped();
                        await target.WriteFanSpeedAsync(100, token).ConfigureAwait(false);
                        return true;
                    },
                    preOffCancellationToken)
                .ConfigureAwait(false);
            preOffCancellationToken.ThrowIfCancellationRequested();
            var initialOn = await ExecuteHostStopHidStageAsync(
                    token => target.ReadStateAsync(token), preOffCancellationToken)
                .ConfigureAwait(false);
            if (!initialOn.PoweredOn || initialOn.FanSpeedPercent != 100 ||
                !LightingMatches(before, initialOn))
            {
                throw new V12ProtocolException(
                    "Shutdown qualification did not confirm its initial ON-at-100 state with lighting preserved.");
            }

            _ = RequireShutdownQualificationPreconditions(target);
            if (target.Identity != expectedIdentity)
            {
                throw new ControllerOperationException(
                    PipeErrorCodes.UnsupportedDevice,
                    "The exact device-handle identity changed before the shutdown qualification OFF boundary.");
            }

            if (!string.Equals(runtimeState.OwnerSid, expectedOwnerSid, StringComparison.OrdinalIgnoreCase))
            {
                throw new ControllerOperationException(
                    PipeErrorCodes.InternalError,
                    "The commissioned runtime owner changed before the shutdown qualification OFF boundary.");
            }

            if (!cancellationGate.TryEnterOff())
            {
                throw new OperationCanceledException(
                    "Shutdown qualification was cancelled before the OFF boundary.",
                    preOffCancellationToken);
            }

            // The OFF boundary is linearized above. From here through the final exact ON-at-100
            // readback, caller cancellation is deliberately ignored. Every operation remains
            // bounded by its own 500 ms HID-stage deadline.
            await ExecuteHostStopHidStageAsync(
                    async token =>
                    {
                        ThrowIfAutomaticOutputStopped();
                        await shutdownDevice.WriteSystemShutdownOffAsync(token).ConfigureAwait(false);
                        return true;
                    },
                    CancellationToken.None)
                .ConfigureAwait(false);
            var offReadback = await ExecuteHostStopHidStageAsync(
                    token => target.ReadStateAsync(token), CancellationToken.None)
                .ConfigureAwait(false);
            if (offReadback.PoweredOn || offReadback.FanSpeedPercent != 100 ||
                !LightingMatches(initialOn, offReadback))
            {
                throw new V12ProtocolException(
                    "Shutdown qualification did not receive an exact powered-off readback with lighting preserved.");
            }

            await ExecuteHostStopHidStageAsync(
                    async token =>
                    {
                        ThrowIfAutomaticOutputStopped();
                        await target.WriteFanSpeedAsync(100, token).ConfigureAwait(false);
                        return true;
                    },
                    CancellationToken.None)
                .ConfigureAwait(false);
            var finalOn = await ExecuteHostStopHidStageAsync(
                    token => target.ReadStateAsync(token), CancellationToken.None)
                .ConfigureAwait(false);
            if (!finalOn.PoweredOn || finalOn.FanSpeedPercent != 100 ||
                !LightingMatches(before, finalOn) || !LightingMatches(offReadback, finalOn))
            {
                throw new V12ProtocolException(
                    "Shutdown qualification did not confirm final ON at 100% with lighting preserved.");
            }

            return ShutdownQualificationTransactionResult.Success(finalOn);
        }
        catch (SafetyOutputSuppressedException) { throw; }
        catch (Exception exception)
        {
            var recovery = await RecoverShutdownQualificationAtOneHundredAsync(target, lightingBaseline)
                .ConfigureAwait(false);
            return ShutdownQualificationTransactionResult.Failed(
                exception,
                recovery.Confirmed,
                recovery.Readback);
        }
    }

    private async Task<(bool Confirmed, V12DeviceState? Readback)>
        RecoverShutdownQualificationAtOneHundredAsync(
            IV12Device target,
            V12DeviceState? lightingBaseline)
    {
        try
        {
            await ExecuteHostStopHidStageAsync(
                    async token =>
                    {
                        ThrowIfAutomaticOutputStopped();
                        await target.WriteFanSpeedAsync(100, token).ConfigureAwait(false);
                        return true;
                    },
                    CancellationToken.None)
                .ConfigureAwait(false);
            var readback = await ExecuteHostStopHidStageAsync(
                    token => target.ReadStateAsync(token), CancellationToken.None)
                .ConfigureAwait(false);
            var confirmed = readback.PoweredOn && readback.FanSpeedPercent == 100 &&
                (lightingBaseline is null || LightingMatches(lightingBaseline, readback));
            return (confirmed, confirmed ? readback : null);
        }
        catch (SafetyOutputSuppressedException) { throw; }
        catch (Exception recoveryFailure)
        {
            logger.LogCritical(
                recoveryFailure,
                "The attended shutdown qualification could not complete same-transaction ON-at-100 recovery.");
            return (false, null);
        }
    }

    private async Task<SafetyOperationAuditJournal> BeginSafetyOperationAuditAsync(
        string eventName,
        string callerSid,
        string source,
        SafetyOperationKind kind,
        object previous,
        object intended,
        CancellationToken cancellationToken)
    {
        if (!SafetyOperationEvents.Contains(eventName))
        {
            throw new InvalidOperationException(
                $"The safety-operation event '{eventName}' is not recovery-compatible.");
        }

        if (activeSafetyOperationAudit is not null || safetyOperationAuditBlocked)
        {
            throw new ControllerOperationException(
                PipeErrorCodes.InternalError,
                "A durable safety-operation audit is already pending; no later safety operation may start.");
        }

        if (!profileLightingApplying)
            SupersedeProfileApplicationUnderGate("已由明确的设备操作取代；原方案不会继续应用灯光。");

        var prepared = safetyOperationAuditJournalStore.Create(
            eventName,
            callerSid,
            source,
            kind,
            previous,
            intended,
            requiresOn100Recovery: kind == SafetyOperationKind.HidWithOn100Recovery);
        activeSafetyOperationAudit = prepared;
        try
        {
            await safetyOperationAuditJournalStore.PrepareAsync(prepared, cancellationToken)
                .ConfigureAwait(false);
            var inProgress = await safetyOperationAuditJournalStore.MarkEffectInProgressAsync(
                    prepared,
                    CancellationToken.None)
                .ConfigureAwait(false);
            activeSafetyOperationAudit = inProgress;
            return inProgress;
        }
        catch (Exception exception) when (!IsFatalPersistenceFailure(exception))
        {
            var retained = await TryLoadSafetyOperationAuditForFailureAsync().ConfigureAwait(false);
            LatchSafetyOperationAuditBlocked(
                exception,
                retained ?? prepared,
                "preparing the durable safety-operation effect barrier");
            await CompensateCommissioningAfterSafetyAuditFailureAsync().ConfigureAwait(false);
            throw CreateSafetyOperationAuditFailure(
                "The safety operation did not start because its durable audit barrier could not be confirmed.",
                exception);
        }
    }

    private async Task ResolveAndFinishSafetyOperationAuditAsync(
        SafetyOperationAuditJournal journal,
        SafetyOperationAuditOutcome outcome,
        object observed,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var resolved = await safetyOperationAuditJournalStore.ResolveAsync(
                    journal,
                    outcome,
                    observed,
                    cancellationToken)
                .ConfigureAwait(false);
            activeSafetyOperationAudit = resolved;
            await FinishResolvedSafetyOperationAuditAsync(resolved, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (!IsFatalPersistenceFailure(exception))
        {
            var retained = await TryLoadSafetyOperationAuditForFailureAsync().ConfigureAwait(false);
            LatchSafetyOperationAuditBlocked(
                exception,
                retained ?? activeSafetyOperationAudit ?? journal,
                "resolving the durable safety-operation audit");
            await CompensateCommissioningAfterSafetyAuditFailureAsync().ConfigureAwait(false);
            throw CreateSafetyOperationAuditFailure(
                "The safety effect reached a known outcome, but its durable audit transaction did not finish. Normal hardware access and shutdown fan-off are blocked until recovery.",
                exception);
        }
    }

    private async Task CompensateCommissioningAfterSafetyAuditFailureAsync()
    {
        if (commissioningSession is not { } session)
        {
            return;
        }

        // Persistence failure must refuse the requested operation, but must not suppress
        // the already-owned session's emergency ON-at-100 attempt. In particular, the
        // normal blocked loop does not own commissioningSession.Device. Keep the audit
        // barrier even after successful cleanup; compensation is not an audited success.
        var cleanup = await CleanupCommissioningSessionAsync(session).ConfigureAwait(false);
        policy.Reset();
        logger.LogWarning(
            "Commissioning cleanup after safety-audit persistence failure: " +
            "ON-at-100 confirmed={ReadbackConfirmed}, session released={SessionReleased}. " +
            "The audit recovery barrier remains active.",
            cleanup.FailSafe.ReadbackConfirmed,
            cleanup.SessionReleased);
        UpdateStatus("SafetyOperationAuditRecoveryRequired", FailSafeReason.ConfigurationInvalid);
    }

    private async Task FinishResolvedSafetyOperationAuditAsync(
        SafetyOperationAuditJournal journal,
        CancellationToken cancellationToken)
    {
        if (journal.Stage == SafetyOperationAuditStage.OutcomeResolved)
        {
            if (!await auditLog.ContainsTransactionAsync(journal.TransactionId, cancellationToken)
                    .ConfigureAwait(false))
            {
                await auditLog.WriteAsync(
                        journal.EventName,
                        SafetyOperationOutcomeToken(journal.Outcome),
                        journal.CallerSid,
                        journal.Source,
                        new
                        {
                            kind = journal.Kind.ToString(),
                            previous = journal.Previous,
                            intended = journal.Intended,
                            observed = journal.Observed,
                            journal.RequiresOn100Recovery,
                            journal.PreparedAtUtc,
                        },
                        cancellationToken,
                        journal.TransactionId)
                    .ConfigureAwait(false);
            }

            if (!await auditLog.ContainsTransactionAsync(journal.TransactionId, cancellationToken)
                    .ConfigureAwait(false))
            {
                throw new IOException(
                    "The safety-operation audit append could not be found by transactionId.");
            }

            journal = await safetyOperationAuditJournalStore.MarkAuditAppendedAsync(
                    journal,
                    cancellationToken)
                .ConfigureAwait(false);
            activeSafetyOperationAudit = journal;
        }

        if (journal.Stage != SafetyOperationAuditStage.AuditAppended)
        {
            throw new InvalidDataException(
                $"Cannot finish safety-operation audit from stage {journal.Stage}.");
        }

        if (!await auditLog.ContainsTransactionAsync(journal.TransactionId, cancellationToken)
                .ConfigureAwait(false))
        {
            throw new InvalidDataException(
                "The safety-operation journal claims AuditAppended, but its transaction is absent from the retained audit log.");
        }

        await safetyOperationAuditJournalStore.DeleteExactAsync(journal, cancellationToken)
            .ConfigureAwait(false);
        activeSafetyOperationAudit = null;
        safetyOperationAuditBlocked = false;
        safetyOperationAuditError = null;
    }

    private async Task RecoverPendingSafetyOperationAuditAsync(CancellationToken cancellationToken)
    {
        var journal = await safetyOperationAuditJournalStore.LoadAsync(cancellationToken)
            .ConfigureAwait(false);
        activeSafetyOperationAudit = journal;
        if (journal is null)
        {
            safetyOperationAuditBlocked = false;
            safetyOperationAuditError = null;
            return;
        }

        if (!SafetyOperationEvents.Contains(journal.EventName))
        {
            throw new InvalidDataException(
                $"Unsupported safety-operation recovery event '{journal.EventName}'.");
        }

        switch (journal.Stage)
        {
            case SafetyOperationAuditStage.Prepared:
                journal = await safetyOperationAuditJournalStore.ResolvePreparedAsNotAppliedAsync(
                        journal,
                        new
                        {
                            recovery = "service-startup",
                            effectApplied = false,
                            classification = "prepared-intent-only",
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
                activeSafetyOperationAudit = journal;
                await FinishResolvedSafetyOperationAuditAsync(journal, cancellationToken)
                    .ConfigureAwait(false);
                return;

            case SafetyOperationAuditStage.EffectInProgress
                when journal.Kind == SafetyOperationKind.SessionOnly:
                journal = await safetyOperationAuditJournalStore.ResolveAsync(
                        journal,
                        SafetyOperationAuditOutcome.Indeterminate,
                        new
                        {
                            recovery = "service-startup",
                            effectApplied = (bool?)null,
                            classification = "volatile-session-state-lost",
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
                activeSafetyOperationAudit = journal;
                await FinishResolvedSafetyOperationAuditAsync(journal, cancellationToken)
                    .ConfigureAwait(false);
                return;

            case SafetyOperationAuditStage.EffectInProgress:
                var recovered = await RecoverSafetyOperationHidAtOneHundredAsync(cancellationToken)
                    .ConfigureAwait(false);
                journal = await safetyOperationAuditJournalStore.ResolveAsync(
                        journal,
                        SafetyOperationAuditOutcome.Compensated,
                        new
                        {
                            recovery = "service-startup",
                            sequence = "GET-ON100-GET",
                            poweredOn = recovered.PoweredOn,
                            fanPercent = recovered.FanSpeedPercent,
                            lightingPreserved = true,
                            identity = FormatIdentity(SupportedDeviceIdentity),
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
                activeSafetyOperationAudit = journal;
                await FinishResolvedSafetyOperationAuditAsync(journal, cancellationToken)
                    .ConfigureAwait(false);
                return;

            case SafetyOperationAuditStage.OutcomeResolved:
            case SafetyOperationAuditStage.AuditAppended:
                await FinishResolvedSafetyOperationAuditAsync(journal, cancellationToken)
                    .ConfigureAwait(false);
                return;

            default:
                throw new InvalidDataException(
                    $"Unsupported safety-operation recovery stage '{journal.Stage}'.");
        }
    }

    private async Task<V12DeviceState> RecoverSafetyOperationHidAtOneHundredAsync(
        CancellationToken cancellationToken, bool attendedUncommissioned = false)
    {
        if (HasForcedUninstallRecoveryBarrier ||
            runtimeStateLoadInvalid ||
            configurationLoadInvalid ||
            commissioningStateMismatch ||
            invalidConfigurationWithPendingJournal ||
            auditMutationBlocked ||
            configurationError is not null ||
            (hardwareAccessBlocked && !attendedUncommissioned) ||
            (!attendedUncommissioned && (!configuration.Commissioned ||
            !runtimeState.Commissioned || string.IsNullOrWhiteSpace(runtimeState.OwnerSid))))
        {
            throw new InvalidDataException(
                "HID safety-operation recovery requires exact commissioned ownership and healthy durable controller state.");
        }

        var conflict = conflictDetector.Inspect();
        if (conflict.HasConflict)
        {
            throw new InvalidDataException(
                "HID safety-operation recovery was refused because Myth.Cool or USB Center is active.");
        }

        var candidates = await FindCandidateIdentitiesAsync(cancellationToken).ConfigureAwait(false);
        if (candidates.Count != 1 ||
            candidates[0] != SupportedDeviceIdentity ||
            !candidates[0].IsSupportedV12Ultra)
        {
            throw new InvalidDataException(
                "HID safety-operation recovery requires one exact allowlisted V12 Ultra identity.");
        }

        if (!writerLease.TryAcquire())
        {
            throw new IOException(
                "HID safety-operation recovery could not acquire the exclusive writer lease.");
        }

        IV12Device? opened = null;
        try
        {
            var openResult = await OpenExpectedDeviceAsync(SupportedDeviceIdentity, cancellationToken)
                .ConfigureAwait(false);
            if (openResult.Status != V12DeviceOpenStatus.Opened || openResult.Device is null)
            {
                throw new IOException(
                    $"HID safety-operation recovery could not open the exact device ({openResult.Status}).");
            }

            opened = openResult.Device;
            if (opened.Identity != SupportedDeviceIdentity)
            {
                throw new V12ProtocolException(
                    "The HID safety-operation recovery handle identity changed after open.");
            }

            var before = await ReadDeviceStateAsync(opened, cancellationToken).ConfigureAwait(false);
            await WriteFanSpeedAsync(opened, 100, cancellationToken).ConfigureAwait(false);
            var after = await ReadDeviceStateAsync(opened, cancellationToken).ConfigureAwait(false);
            if (attendedUncommissioned && after.RawModeStatus != 0x80 &&
                opened is IV12SoftwareControlDevice softwareControl &&
                (!after.PoweredOn || after.FanSpeedPercent != 100) && LightingMatches(before, after))
            {
                // Explicit attended repair only: vendor's mode-change action, not
                // arbitrary b0 values or an inferred meaning of individual status bits.
                await RunHidOperationAsync(async token =>
                {
                    ThrowIfAutomaticOutputStopped();
                    await softwareControl.SelectSoftwareControlAtOneHundredAsync(token).ConfigureAwait(false);
                    return true;
                }, HidTimeout, cancellationToken, timeoutPoisonResource: "software control takeover at 100",
                    timeoutRecoveryTarget: opened).ConfigureAwait(false);
                after = await ReadDeviceStateAsync(opened, cancellationToken).ConfigureAwait(false);
            }
            if (!after.PoweredOn || after.FanSpeedPercent != 100 || !LightingMatches(before, after))
            {
                throw new V12ProtocolException(
                    "HID safety-operation recovery did not confirm ON at 100% with lighting preserved.");
            }

            device = opened;
            opened = null;
            everOpened = true;
            deviceWasMissing = false;
            lastDeviceState = after;
            var recoveredAt = timeProvider.GetUtcNow();
            lastSuccessfulRead = recoveredAt;
            lastSuccessfulWrite = recoveredAt;
            lastSuccessfulWriteMonotonic = GetMonotonicNow();
            lastWrittenPercent = 100;
            policy.Reset();
            policy.RecordWriteSuccess(lastSuccessfulWriteMonotonic.Value, 100);
            return after;
        }
        catch
        {
            if (opened is not null && !IsHardwarePoisoned)
            {
                await DisposeHardwareResourceAsync(
                        opened,
                        "failed safety-operation startup recovery device",
                        CancellationToken.None,
                        HidTimeout,
                        poisonOnFailure: true)
                    .ConfigureAwait(false);
            }

            if (!IsHardwarePoisoned && device is null)
            {
                writerLease.Release();
            }

            throw;
        }
    }

    private async Task<SafetyOperationAuditJournal?> TryLoadSafetyOperationAuditForFailureAsync()
    {
        try
        {
            return await safetyOperationAuditJournalStore.LoadAsync(CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception loadFailure)
        {
            logger.LogCritical(
                loadFailure,
                "The safety-operation audit could not be reloaded after a persistence failure.");
            return activeSafetyOperationAudit;
        }
    }

    private void LatchSafetyOperationAuditBlocked(
        Exception exception,
        SafetyOperationAuditJournal? journal,
        string operation)
    {
        activeSafetyOperationAudit = journal;
        safetyOperationAuditBlocked = true;
        safetyOperationAuditError =
            $"Safety-operation audit recovery failed during {operation} ({exception.GetType().Name}). " +
            "The durable barrier was preserved; normal hardware access and shutdown fan-off are blocked.";
        hardwareAccessBlocked = true;
        logger.LogCritical(
            exception,
            "Safety-operation audit recovery blocked normal hardware access while {SafetyOperation}. " +
            "The recovery barrier was preserved.",
            operation);
        if (initialized)
        {
            UpdateStatus(
                "SafetyOperationAuditRecoveryRequired",
                FailSafeReason.ConfigurationInvalid);
        }
    }

    private static string SafetyOperationOutcomeToken(SafetyOperationAuditOutcome outcome) =>
        outcome switch
        {
            SafetyOperationAuditOutcome.Success => "success",
            SafetyOperationAuditOutcome.NotApplied => "not-applied",
            SafetyOperationAuditOutcome.Compensated => "compensated",
            SafetyOperationAuditOutcome.Indeterminate => "indeterminate",
            _ => throw new InvalidDataException(
                $"Safety-operation audit outcome '{outcome}' is not terminal."),
        };

    private static ControllerOperationException CreateSafetyOperationAuditFailure(
        string message,
        Exception? inner = null) =>
        new(PipeErrorCodes.InternalError, message, inner);

    private async Task PersistConfigurationMutationAsync(
        ControllerConfiguration intendedConfiguration,
        string eventName,
        PipePeerIdentity peer,
        CancellationToken cancellationToken)
    {
        if (!ConfigurationMutationEvents.Contains(eventName))
        {
            throw new InvalidOperationException(
                $"The configuration mutation event '{eventName}' is not recovery-compatible.");
        }

        intendedConfiguration.Validate();
        if (eventName != PipeOperations.ProfileApply)
            SupersedeProfileApplicationUnderGate("已由新的控制设置取代；原方案不会继续应用灯光。");
        var previousConfiguration = configuration;
        var prepared = auditMutationJournalStore.Create(
            eventName,
            peer.CallerSid,
            peer.Source,
            AuditConfigurationSnapshot.FromConfiguration(previousConfiguration),
            AuditConfigurationSnapshot.FromConfiguration(intendedConfiguration));
        activeAuditMutation = prepared;

        try
        {
            await auditMutationJournalStore.PrepareAsync(prepared, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (!IsFatalPersistenceFailure(exception))
        {
            var existing = await TryLoadAuditMutationForFailureAsync().ConfigureAwait(false);
            LatchAuditMutationBlocked(
                exception,
                existing,
                "preparing the durable configuration-mutation barrier");
            throw CreateAuditMutationFailure(
                "The durable audit-mutation barrier could not be prepared; no configuration write was started.",
                exception);
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException)
        {
            try
            {
                await ResolveAuditMutationAsync(
                        prepared,
                        AuditMutationOutcome.NotApplied,
                        "caller-cancelled-before-mutation")
                    .ConfigureAwait(false);
            }
            catch (Exception recoveryFailure) when (!IsFatalPersistenceFailure(recoveryFailure))
            {
                LatchAuditMutationBlocked(
                    recoveryFailure,
                    activeAuditMutation,
                    "recording a cancelled configuration mutation");
            }

            throw;
        }

        AuditMutationJournal inProgress;
        try
        {
            inProgress = await auditMutationJournalStore.AdvanceAsync(
                    prepared,
                    AuditMutationStage.MutationInProgress,
                    CancellationToken.None)
                .ConfigureAwait(false);
            activeAuditMutation = inProgress;
        }
        catch (Exception exception) when (!IsFatalPersistenceFailure(exception))
        {
            LatchAuditMutationBlocked(
                exception,
                await TryLoadAuditMutationForFailureAsync().ConfigureAwait(false),
                "publishing the configuration-mutation start barrier");
            throw CreateAuditMutationFailure(
                "The configuration mutation did not start because its durable start barrier could not be confirmed.",
                exception);
        }

        Exception? saveFailure = null;
        try
        {
            // Once MutationInProgress is durable, caller cancellation cannot strand an
            // ambiguous replacement. Finish exact readback and WAL resolution first.
            await configurationStore.SaveAsync(intendedConfiguration, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (!IsFatalPersistenceFailure(exception))
        {
            saveFailure = exception;
        }

        ControllerConfiguration persistedConfiguration;
        try
        {
            persistedConfiguration = await configurationStore.LoadAsync(CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception readbackFailure) when (!IsFatalPersistenceFailure(readbackFailure))
        {
            LatchAuditMutationBlocked(
                readbackFailure,
                inProgress,
                "reading back the configuration mutation");
            throw CreateAuditMutationFailure(
                "The configuration replacement outcome could not be read back. The durable barrier was retained and all normal hardware access is blocked.",
                readbackFailure);
        }

        if (RecoveryConfigurationMatches(intendedConfiguration, persistedConfiguration))
        {
            configuration = intendedConfiguration;
            try
            {
                await ResolveAuditMutationAsync(
                        inProgress,
                        AuditMutationOutcome.Applied,
                        saveFailure is null ? "live-confirmed" : "save-threw-but-current-confirmed")
                    .ConfigureAwait(false);
                configurationError = null;
                return;
            }
            catch (Exception resolutionFailure) when (!IsFatalPersistenceFailure(resolutionFailure))
            {
                LatchAuditMutationBlocked(
                    resolutionFailure,
                    activeAuditMutation,
                    "finalizing the applied configuration mutation");
                throw CreateAuditMutationFailure(
                    "The new configuration was read back exactly, but its durable audit transaction did not finish. Normal hardware access and shutdown fan-off are blocked until recovery.",
                    resolutionFailure);
            }
        }

        if (RecoveryConfigurationMatches(previousConfiguration, persistedConfiguration))
        {
            configuration = previousConfiguration;
            try
            {
                await ResolveAuditMutationAsync(
                        inProgress,
                        AuditMutationOutcome.NotApplied,
                        saveFailure is null ? "replacement-not-visible" : "save-failed-not-applied")
                    .ConfigureAwait(false);
            }
            catch (Exception resolutionFailure) when (!IsFatalPersistenceFailure(resolutionFailure))
            {
                LatchAuditMutationBlocked(
                    resolutionFailure,
                    activeAuditMutation,
                    "finalizing the non-applied configuration mutation");
                throw CreateAuditMutationFailure(
                    "The previous configuration remains exact, but the non-applied audit transaction could not be finalized. Normal hardware access is blocked until recovery.",
                    resolutionFailure);
            }

            throw CreateAuditMutationFailure(
                "The requested configuration was not published; the exact previous configuration remains active and the attempt was durably audited as not applied.",
                saveFailure);
        }

        var mismatch = new InvalidDataException(
            "Configuration readback matches neither the complete previous nor intended WAL snapshot.");
        LatchAuditMutationBlocked(
            mismatch,
            inProgress,
            "classifying the configuration mutation result");
        throw CreateAuditMutationFailure(
            "The configuration replacement produced an unrecognized state. The durable barrier was retained and all normal hardware access is blocked.",
            saveFailure is null ? mismatch : new AggregateException(saveFailure, mismatch));
    }

    private async Task RecoverPendingAuditMutationAsync(CancellationToken cancellationToken)
    {
        var journal = await auditMutationJournalStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        activeAuditMutation = journal;
        if (journal is null)
        {
            auditMutationBlocked = false;
            auditMutationError = null;
            return;
        }

        if (!ConfigurationMutationEvents.Contains(journal.EventName))
        {
            throw new InvalidDataException(
                $"The pending audit mutation event '{journal.EventName}' is not supported by this service version.");
        }

        var previous = AuditConfigurationSnapshot.ParseConfiguration(journal.Previous);
        var current = AuditConfigurationSnapshot.ParseConfiguration(journal.Current);
        switch (journal.Stage)
        {
            case AuditMutationStage.Prepared:
                RequireRecoveryConfiguration(previous, "Prepared");
                await ResolveAuditMutationAsync(
                        journal,
                        AuditMutationOutcome.NotApplied,
                        "startup-prepared-not-started")
                    .ConfigureAwait(false);
                break;

            case AuditMutationStage.MutationInProgress:
                if (string.Equals(
                        journal.EventName,
                        PipeOperations.ShutdownEnable,
                        StringComparison.Ordinal))
                {
                    if (previous.SystemShutdownFanBehavior !=
                            SystemShutdownFanBehavior.KeepAt100 ||
                        previous.ShutdownOffQualification is not null)
                    {
                        throw new InvalidDataException(
                            "An in-progress shutdown.enable transaction does not contain a safe KeepAt100 previous snapshot.");
                    }

                    if (RecoveryConfigurationMatches(current, configuration))
                    {
                        await RestoreAmbiguousShutdownEnableToKeepAtOneHundredAsync(previous)
                            .ConfigureAwait(false);
                        configuration = previous;
                    }
                    else if (!RecoveryConfigurationMatches(previous, configuration))
                    {
                        throw new InvalidDataException(
                            "The in-progress shutdown.enable configuration matches neither its safe previous nor intended snapshot.");
                    }

                    await ResolveAuditMutationAsync(
                            journal,
                            AuditMutationOutcome.NotApplied,
                            "startup-shutdown-enable-rolled-back-to-keep-at-100")
                        .ConfigureAwait(false);
                }
                else if (RecoveryConfigurationMatches(current, configuration))
                {
                    configuration = current;
                    await ResolveAuditMutationAsync(
                            journal,
                            AuditMutationOutcome.Applied,
                            "startup-in-progress-current-confirmed")
                        .ConfigureAwait(false);
                }
                else if (RecoveryConfigurationMatches(previous, configuration))
                {
                    configuration = previous;
                    await ResolveAuditMutationAsync(
                            journal,
                            AuditMutationOutcome.NotApplied,
                            "startup-in-progress-previous-confirmed")
                        .ConfigureAwait(false);
                }
                else
                {
                    throw new InvalidDataException(
                        "The configuration matches neither snapshot in the in-progress audit mutation.");
                }

                break;

            case AuditMutationStage.StateApplied:
                RequireRecoveryConfiguration(current, "StateApplied");
                configuration = current;
                await FinishResolvedAuditMutationAsync(
                        journal,
                        "startup-state-applied")
                    .ConfigureAwait(false);
                break;

            case AuditMutationStage.MutationNotApplied:
                RequireRecoveryConfiguration(previous, "MutationNotApplied");
                configuration = previous;
                await FinishResolvedAuditMutationAsync(
                        journal,
                        "startup-not-applied")
                    .ConfigureAwait(false);
                break;

            case AuditMutationStage.AuditAppended:
                var expected = journal.Outcome switch
                {
                    AuditMutationOutcome.Applied => current,
                    AuditMutationOutcome.NotApplied => previous,
                    _ => throw new InvalidDataException(
                        "The appended audit mutation does not contain a resolved outcome."),
                };
                RequireRecoveryConfiguration(expected, "AuditAppended");
                if (!await auditLog.ContainsTransactionAsync(journal.TransactionId, cancellationToken)
                    .ConfigureAwait(false))
                {
                    throw new InvalidDataException(
                        "The audit mutation claims AuditAppended, but its transaction cannot be found in the retained audit log.");
                }

                configuration = expected;
                await auditMutationJournalStore.DeleteExactAsync(journal, cancellationToken)
                    .ConfigureAwait(false);
                activeAuditMutation = null;
                break;

            default:
                throw new InvalidDataException(
                    $"Unsupported audit-mutation recovery stage {journal.Stage}.");
        }

        auditMutationBlocked = false;
        auditMutationError = null;
        logger.LogWarning(
            "Recovered durable configuration audit mutation {TransactionId} ({EventName}).",
            journal.TransactionId,
            journal.EventName);
    }

    private async Task RestoreAmbiguousShutdownEnableToKeepAtOneHundredAsync(
        ControllerConfiguration safeConfiguration)
    {
        Exception? saveFailure = null;
        try
        {
            await configurationStore.SaveAsync(safeConfiguration, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (!IsFatalPersistenceFailure(exception))
        {
            saveFailure = exception;
        }

        ControllerConfiguration readback;
        try
        {
            readback = await configurationStore.LoadAsync(CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception readbackFailure) when (!IsFatalPersistenceFailure(readbackFailure))
        {
            throw new AggregateException(
                "The ambiguous shutdown.enable transaction could not read back its KeepAt100 recovery.",
                saveFailure is null
                    ? [readbackFailure]
                    : [saveFailure, readbackFailure]);
        }

        if (!RecoveryConfigurationMatches(safeConfiguration, readback))
        {
            var mismatch = new InvalidDataException(
                "The ambiguous shutdown.enable transaction did not read back exact KeepAt100 recovery.");
            throw saveFailure is null ? mismatch : new AggregateException(saveFailure, mismatch);
        }
    }

    private void RequireRecoveryConfiguration(
        ControllerConfiguration expected,
        string stage)
    {
        if (!RecoveryConfigurationMatches(expected, configuration))
        {
            throw new InvalidDataException(
                $"The persisted configuration does not match the exact {stage} audit-mutation outcome.");
        }
    }

    private async Task ResolveAuditMutationAsync(
        AuditMutationJournal journal,
        AuditMutationOutcome outcome,
        string recoveryContext)
    {
        var outcomeStage = outcome switch
        {
            AuditMutationOutcome.Applied => AuditMutationStage.StateApplied,
            AuditMutationOutcome.NotApplied => AuditMutationStage.MutationNotApplied,
            _ => throw new ArgumentOutOfRangeException(nameof(outcome)),
        };
        journal = await auditMutationJournalStore.AdvanceAsync(
                journal,
                outcomeStage,
                CancellationToken.None)
            .ConfigureAwait(false);
        activeAuditMutation = journal;
        await FinishResolvedAuditMutationAsync(journal, recoveryContext).ConfigureAwait(false);
    }

    private async Task FinishResolvedAuditMutationAsync(
        AuditMutationJournal journal,
        string recoveryContext)
    {
        if (journal.Stage is not (
                AuditMutationStage.StateApplied or AuditMutationStage.MutationNotApplied))
        {
            throw new InvalidDataException(
                "Only a resolved audit mutation can append its durable audit record.");
        }

        if (!await auditLog.ContainsTransactionAsync(journal.TransactionId, CancellationToken.None)
            .ConfigureAwait(false))
        {
            await auditLog.WriteAsync(
                    journal.EventName,
                    journal.Outcome == AuditMutationOutcome.Applied ? "success" : "not-applied",
                    journal.CallerSid,
                    journal.Source,
                    new
                    {
                        previous = journal.Previous,
                        current = journal.Current,
                        outcome = journal.Outcome.ToString(),
                        recovery = recoveryContext,
                    },
                    CancellationToken.None,
                    journal.TransactionId)
                .ConfigureAwait(false);
            if (!await auditLog.ContainsTransactionAsync(
                    journal.TransactionId,
                    CancellationToken.None).ConfigureAwait(false))
            {
                throw new IOException(
                    "The durable audit append completed without a discoverable transaction record.");
            }
        }

        journal = await auditMutationJournalStore.AdvanceAsync(
                journal,
                AuditMutationStage.AuditAppended,
                CancellationToken.None)
            .ConfigureAwait(false);
        activeAuditMutation = journal;
        await auditMutationJournalStore.DeleteExactAsync(journal, CancellationToken.None)
            .ConfigureAwait(false);
        activeAuditMutation = null;
    }

    private async Task<AuditMutationJournal?> TryLoadAuditMutationForFailureAsync()
    {
        try
        {
            return await auditMutationJournalStore.LoadAsync(CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (!IsFatalPersistenceFailure(exception))
        {
            return activeAuditMutation;
        }
    }

    private void LatchAuditMutationBlocked(
        Exception exception,
        AuditMutationJournal? journal,
        string operation)
    {
        activeAuditMutation = journal;
        auditMutationBlocked = true;
        hardwareAccessBlocked = true;
        _ = Interlocked.Exchange(ref shutdownQualificationPending, null);
        auditMutationError =
            $"Audit-mutation recovery is required after a failure while {operation}: " +
            $"{exception.GetType().Name}. Durable stage: {journal?.Stage.ToString() ?? "unreadable"}. " +
            "The durable barrier was preserved.";
        configurationError ??= auditMutationError;
        policy.Reset();
        logger.LogCritical(
            exception,
            "Audit mutation recovery blocked normal hardware access while {AuditMutationOperation}. " +
            "Transaction: {TransactionId}; event: {EventName}; stage: {Stage}.",
            operation,
            journal?.TransactionId,
            journal?.EventName,
            journal?.Stage);
        if (initialized)
        {
            UpdateStatus("AuditMutationRecoveryRequired", FailSafeReason.ConfigurationInvalid);
        }
    }

    private static ControllerOperationException CreateAuditMutationFailure(
        string message,
        Exception? inner = null) =>
        new(PipeErrorCodes.InternalError, message, inner);

    private static bool IsFatalPersistenceFailure(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or AccessViolationException;

    private static SystemShutdownOffQualification CreateShutdownQualification(
        DeviceIdentity identity,
        DateTimeOffset qualifiedAtUtc) => new()
        {
            ProtocolRevision = SystemShutdownOffQualification.CurrentProtocolRevision,
            VendorId = identity.VendorId,
            ProductId = identity.ProductId,
            FirmwareVersion = identity.Version,
            UsagePage = identity.UsagePage,
            Usage = identity.Usage,
            FeatureReportLength = identity.FeatureReportLength,
            ShutdownOffReadbackConfirmed = true,
            StartupOnAt100ReadbackConfirmed = true,
            LightingStatePreserved = true,
            QualifiedAtUtc = qualifiedAtUtc,
        };

    private static ControllerOperationException CreateShutdownQualificationFailure(Exception? failure) =>
        failure switch
        {
            ControllerOperationException operation => operation,
            V12ProtocolException protocol => new ControllerOperationException(
                PipeErrorCodes.ProtocolChecksum,
                "Shutdown qualification failed an exact protocol readback check; the fan was restored and confirmed ON at 100%.",
                protocol),
            _ => new ControllerOperationException(
                PipeErrorCodes.HidTimeout,
                "Shutdown qualification failed; the fan was restored and confirmed ON at 100%, and no qualification was saved.",
                failure),
        };

    internal Task FinalizeHostStopAsync(ServiceStopReason reason)
    {
        lock (hostStopSync)
        {
            return hostStopFinalization ??= FinalizeHostStopCoreAsync(reason);
        }
    }

    private async Task FinalizeHostStopCoreAsync(ServiceStopReason reason)
    {
        using var overallDeadline = new CancellationTokenSource(HostStopFinalizationBudget);
        using var primaryDeadline = CancellationTokenSource.CreateLinkedTokenSource(overallDeadline.Token);
        primaryDeadline.CancelAfter(TimeSpan.FromMilliseconds(900));
        var gateAcquired = false;
        try
        {
            await gate.WaitAsync(primaryDeadline.Token).ConfigureAwait(false);
            gateAcquired = true;
            SetHostStopDisposition(HostStopDisposition.Finalizing);
            InterruptProfileApplicationUnderGate("后台正在停止；方案剩余操作已取消，不会在重启后重放。");

            if (SafetyProtectionDisabled || (AutomaticAdjustmentsDisabled && commissioningSession is null))
            {
                await ReleaseDeviceAsync(cancellationToken: primaryDeadline.Token).ConfigureAwait(false);
                return;
            }

            var target = device ?? commissioningSession?.Device;
            if (!await FinalizeAtOneHundredAsync(
                    target, primaryDeadline.Token, overallDeadline.Token)
                    .ConfigureAwait(false))
            {
                return;
            }

            // Every host-stop path establishes an exact ON+100 baseline before it performs
            // the potentially blocking final thermal read. A stalled hardware-monitor call
            // therefore cannot leave a previously requested 36..99% fan target behind.
            if (IsHardwarePoisoned ||
                !CanAttemptSystemShutdownOff(reason, target, out var shutdownDevice))
            {
                return;
            }

            ThermalSnapshot finalThermal;
            try
            {
                finalThermal = await RunThermalOperationAsync(
                        token => thermalSource.ReadAsync(token),
                        TimeSpan.FromMilliseconds(250),
                        primaryDeadline.Token)
                    .ConfigureAwait(false);
                lastThermal = finalThermal;
            }
            catch (Exception exception) when (
                exception is TimeoutException or OperationCanceledException)
            {
                // The dedicated hardware thread may still be executing a cancellation-
                // insensitive monitor call. Retain the owned HID handle and writer lease so
                // no other process can start controlling the device while that outcome is
                // unknown. The already-confirmed fan baseline remains ON at 100%.
                MarkHardwarePoisoned(
                    "TERMINAL_TRANSACTION_ABANDONED",
                    "host-stop thermal eligibility read",
                    exception);
                SetHostStopDisposition(HostStopDisposition.KeepAt100Unconfirmed);
                return;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception,
                    "System-shutdown fan-off was refused because fresh thermal telemetry was unavailable.");
                return;
            }

            var now = timeProvider.GetUtcNow();
            var thermalAge = now - finalThermal.CapturedAt;
            if (!finalThermal.CpuIsValid ||
                finalThermal.ControlCelsius is not { } controlCelsius ||
                controlCelsius >= 85 ||
                thermalAge < TimeSpan.Zero ||
                thermalAge > TimeSpan.FromSeconds(10) ||
                HasMythCoolConflictForHostStop())
            {
                return;
            }

            await TryFinalizeSystemShutdownOffAsync(
                    target!, shutdownDevice!, primaryDeadline.Token, overallDeadline.Token)
                .ConfigureAwait(false);
        }
        catch (SafetyOutputSuppressedException)
        {
            SetHostStopDisposition(HostStopDisposition.KeepAt100Unconfirmed);
        }
        catch (OperationCanceledException exception)
        {
            logger.LogCritical(exception,
                "Host-stop finalization exceeded its 1.25-second safety budget.");
            Environment.ExitCode = 7;
            SetHostStopDisposition(HostStopDisposition.KeepAt100Unconfirmed);
        }
        catch (Exception exception)
        {
            logger.LogCritical(exception,
                "Host-stop finalization failed closed at the 100% safety target.");
            Environment.ExitCode = 7;
            SetHostStopDisposition(HostStopDisposition.KeepAt100Unconfirmed);
        }
        finally
        {
            if (gateAcquired)
            {
                gate.Release();
            }
        }
    }

    private bool CanAttemptSystemShutdownOff(
        ServiceStopReason reason,
        IV12Device? target,
        out IV12SystemShutdownDevice? shutdownDevice)
    {
        shutdownDevice = target as IV12SystemShutdownDevice;
        if (reason != ServiceStopReason.SystemShutdown ||
            !configuration.Commissioned ||
            !runtimeState.Commissioned ||
            string.IsNullOrWhiteSpace(runtimeState.OwnerSid) ||
            configurationError is not null ||
            hardwareAccessBlocked ||
            safetyOperationAuditBlocked ||
            activeSafetyOperationAudit is not null ||
            invalidConfigurationWithPendingJournal ||
            requiresProcessRestartForRecovery ||
            !policy.IsSettledForSystemShutdownOff ||
            lastDecision is not { FailSafeActive: false, FailSafeReason: FailSafeReason.None } ||
            configuration.SystemShutdownFanBehavior != SystemShutdownFanBehavior.TurnOffOnSystemShutdown ||
            configuration.ShutdownOffQualification is not { } qualification ||
            target is null ||
            shutdownDevice is null ||
            !writerLease.IsHeld ||
            !target.Identity.IsSupportedV12Ultra ||
            !DeviceAllowList.IsSupported(target.Identity))
        {
            return false;
        }

        try
        {
            qualification.Validate();
        }
        catch (ConfigurationValidationException)
        {
            return false;
        }

        return QualificationMatchesIdentity(qualification, target.Identity) &&
               !HasMythCoolConflictForHostStop();
    }

    private async Task<bool> FinalizeAtOneHundredAsync(
        IV12Device? target,
        CancellationToken executionCancellationToken,
        CancellationToken waitCancellationToken)
    {
        if (target is null || IsHardwarePoisoned)
        {
            SetHostStopDisposition(HostStopDisposition.KeepAt100Unconfirmed);
            return false;
        }

        HostStopHidTransactionResult result;
        try
        {
            result = await RunHidOperationAsync(
                    async dispatcherToken =>
                    {
                        using var executionDeadline = CancellationTokenSource.CreateLinkedTokenSource(
                            dispatcherToken, executionCancellationToken);
                        return await ExecuteHostStopOnTransactionAsync(
                                target, executionDeadline.Token)
                            .ConfigureAwait(false);
                    },
                    HostStopFinalizationBudget,
                    waitCancellationToken)
                .ConfigureAwait(false);
        }
        catch (SafetyOutputSuppressedException)
        {
            SetHostStopDisposition(HostStopDisposition.KeepAt100Unconfirmed);
            return false;
        }
        catch (Exception exception) when (
            exception is TimeoutException or OperationCanceledException)
        {
            // The work item may still own a cancellation-insensitive native HID call. It will
            // append its ON+100 recovery on the same hardware thread if that call returns, but
            // this process must retain the handle/lease and terminate rather than detach it.
            MarkHardwarePoisoned(
                "TERMINAL_TRANSACTION_ABANDONED",
                "host-stop ON+100 HID transaction",
                exception);
            SetHostStopDisposition(HostStopDisposition.KeepAt100Unconfirmed);
            return false;
        }
        catch (Exception exception)
        {
            logger.LogCritical(exception,
                "Host-stop finalization could not execute its serialized ON+100 transaction.");
            Environment.ExitCode = 7;
            SetHostStopDisposition(HostStopDisposition.KeepAt100Unconfirmed);
            return false;
        }

        if (!result.Confirmed || result.Readback is null)
        {
            logger.LogCritical(
                result.Failure,
                "Host-stop ON+100 could not be confirmed. Same-transaction recovery requested: " +
                "{RecoveryWriteAttempted}; recovery write succeeded: {RecoveryWriteSucceeded}.",
                result.RecoveryWriteAttempted,
                result.RecoveryWriteSucceeded);
            if (result.RecoveryWriteSucceeded)
            {
                lastSuccessfulWrite = timeProvider.GetUtcNow();
                lastWrittenPercent = 100;
            }

            Environment.ExitCode = 7;
            SetHostStopDisposition(HostStopDisposition.KeepAt100Unconfirmed);
            return false;
        }

        var now = timeProvider.GetUtcNow();
        lastDeviceState = result.Readback;
        lastSuccessfulRead = now;
        lastSuccessfulWrite = now;
        lastWrittenPercent = 100;
        SetHostStopDisposition(HostStopDisposition.KeepAt100Confirmed);
        return true;
    }

    private async Task TryFinalizeSystemShutdownOffAsync(
        IV12Device target,
        IV12SystemShutdownDevice shutdownDevice,
        CancellationToken primaryCancellationToken,
        CancellationToken overallCancellationToken)
    {
        var commit = new SystemShutdownOffCommit();
        using var primaryAbandonment = primaryCancellationToken.UnsafeRegister(
            static state => _ = ((SystemShutdownOffCommit)state!).TryAbandon(),
            commit);
        HostStopHidTransactionResult result;
        try
        {
            result = await RunHidOperationAsync(
                    async dispatcherToken =>
                    {
                        using var executionDeadline = CancellationTokenSource.CreateLinkedTokenSource(
                            dispatcherToken, primaryCancellationToken);
                        return await ExecuteSystemShutdownOffTransactionAsync(
                                target, shutdownDevice, commit, executionDeadline.Token)
                            .ConfigureAwait(false);
                    },
                    HostStopFinalizationBudget,
                    overallCancellationToken)
                .ConfigureAwait(false);
        }
        catch (SafetyOutputSuppressedException)
        {
            SetHostStopDisposition(HostStopDisposition.KeepAt100Unconfirmed);
            return;
        }
        catch (Exception exception) when (
            exception is TimeoutException or OperationCanceledException)
        {
            if (!commit.TryAbandon() &&
                commit.TryGetCommittedReadback(out var lateCommittedReadback))
            {
                // The exact OFF readback won the linearization race before the caller deadline.
                // Even if Task.WaitAsync observed cancellation first, this is a fully committed
                // terminal state and the worker is no longer allowed to append ON+100.
                AcceptCommittedSystemShutdownOff(lateCommittedReadback);
                return;
            }

            // Never enqueue a second recovery work item behind an abandoned synchronous
            // HidD_SetFeature call: its token could expire before the late OFF returns. The
            // original work item owns the recovery sequence and will append ON+100 before it
            // releases the hardware thread. Until then ownership is poisoned and retained.
            MarkHardwarePoisoned(
                "TERMINAL_TRANSACTION_ABANDONED",
                "system-shutdown terminal HID transaction",
                exception);
            SetHostStopDisposition(HostStopDisposition.KeepAt100Unconfirmed);
            return;
        }
        catch (Exception exception)
        {
            logger.LogCritical(exception,
                "System-shutdown terminal HID transaction could not be executed.");
            Environment.ExitCode = 7;
            SetHostStopDisposition(HostStopDisposition.KeepAt100Unconfirmed);
            return;
        }

        if (!result.Confirmed || result.Readback is null ||
            !commit.TryGetCommittedReadback(out var committedReadback) ||
            !ReferenceEquals(committedReadback, result.Readback))
        {
            _ = commit.TryAbandon();
            logger.LogCritical(
                result.Failure,
                "System-shutdown fan-off was not confirmed. Same-transaction ON+100 recovery " +
                "requested: {RecoveryWriteAttempted}; recovery write succeeded: {RecoveryWriteSucceeded}.",
                result.RecoveryWriteAttempted,
                result.RecoveryWriteSucceeded);
            if (result.RecoveryWriteSucceeded)
            {
                lastSuccessfulWrite = timeProvider.GetUtcNow();
                lastWrittenPercent = 100;
            }

            Environment.ExitCode = 7;
            SetHostStopDisposition(HostStopDisposition.KeepAt100Unconfirmed);
            return;
        }

        AcceptCommittedSystemShutdownOff(committedReadback);
    }

    private void AcceptCommittedSystemShutdownOff(V12DeviceState committedReadback)
    {
        var now = timeProvider.GetUtcNow();
        lastDeviceState = committedReadback;
        lastSuccessfulRead = now;
        lastSuccessfulWrite = now;
        lastWrittenPercent = 100;
        SetHostStopDisposition(HostStopDisposition.SystemShutdownOffConfirmed);
    }

    private async Task<HostStopHidTransactionResult> ExecuteHostStopOnTransactionAsync(
        IV12Device target,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var before = await ExecuteHostStopHidStageAsync(
                    token => target.ReadStateAsync(token), cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            await ExecuteHostStopHidStageAsync(
                    async token =>
                    {
                        ThrowIfAutomaticOutputStopped();
                        await target.WriteFanSpeedAsync(100, token).ConfigureAwait(false);
                        return true;
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var readback = await ExecuteHostStopHidStageAsync(
                    token => target.ReadStateAsync(token), cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!readback.PoweredOn || readback.FanSpeedPercent != 100 ||
                !LightingMatches(before, readback))
            {
                throw new V12ProtocolException(
                    "Host-stop finalization did not confirm ON at 100% with lighting preserved.");
            }

            return HostStopHidTransactionResult.Success(readback);
        }
        catch (SafetyOutputSuppressedException) { throw; }
        catch (Exception exception)
        {
            return await RecoverHostStopTransactionAtOneHundredAsync(target, exception)
                .ConfigureAwait(false);
        }
    }

    private async Task<HostStopHidTransactionResult> ExecuteSystemShutdownOffTransactionAsync(
        IV12Device target,
        IV12SystemShutdownDevice shutdownDevice,
        SystemShutdownOffCommit commit,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var before = await ExecuteHostStopHidStageAsync(
                    token => target.ReadStateAsync(token), cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            await ExecuteHostStopHidStageAsync(
                    async token =>
                    {
                        ThrowIfAutomaticOutputStopped();
                        await target.WriteFanSpeedAsync(100, token).ConfigureAwait(false);
                        return true;
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var onReadback = await ExecuteHostStopHidStageAsync(
                    token => target.ReadStateAsync(token), cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!onReadback.PoweredOn || onReadback.FanSpeedPercent != 100 ||
                !LightingMatches(before, onReadback) ||
                IsHardwarePoisoned ||
                !writerLease.IsHeld ||
                !ReferenceEquals(device, target) ||
                HasMythCoolConflictForHostStop())
            {
                throw new V12ProtocolException(
                    "System-shutdown fan-off preconditions changed before the terminal write.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            await ExecuteHostStopHidStageAsync(
                    async token =>
                    {
                        ThrowIfAutomaticOutputStopped();
                        await shutdownDevice.WriteSystemShutdownOffAsync(token).ConfigureAwait(false);
                        return true;
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            // Windows HidD_SetFeature is synchronous and may ignore cancellation. Always check
            // immediately after it returns so an abandoned late OFF cannot be mistaken for a
            // confirmed terminal state.
            cancellationToken.ThrowIfCancellationRequested();
            var offReadback = await ExecuteHostStopHidStageAsync(
                    token => target.ReadStateAsync(token), cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (offReadback.PoweredOn || offReadback.FanSpeedPercent != 100 ||
                !LightingMatches(onReadback, offReadback))
            {
                throw new V12ProtocolException(
                    "System-shutdown fan-off did not receive an exact powered-off readback.");
            }

            // This CAS is the terminal linearization point. If the caller deadline won first,
            // TryCommit fails and the catch below immediately restores ON+100 on this same
            // dispatcher work item. If this CAS wins, a caller-side cancellation race must
            // accept the published exact readback instead of poisoning a proven OFF state.
            if (!commit.TryCommit(offReadback))
            {
                throw new OperationCanceledException(
                    "The system-shutdown OFF result was abandoned before it could be committed.");
            }

            return HostStopHidTransactionResult.Success(offReadback);
        }
        catch (SafetyOutputSuppressedException) { throw; }
        catch (Exception exception)
        {
            // This recovery is deliberately inside the same dispatcher work item and has an
            // independent 500 ms cancellation deadline. If a synchronous OFF returns after the
            // caller abandoned the transaction, ON+100 is therefore the very next HID operation
            // on this thread and never inherits the expired transaction token.
            return await RecoverHostStopTransactionAtOneHundredAsync(target, exception)
                .ConfigureAwait(false);
        }
    }

    private async Task<HostStopHidTransactionResult> RecoverHostStopTransactionAtOneHundredAsync(
        IV12Device target,
        Exception failure)
    {
        try
        {
            await ExecuteHostStopHidStageAsync(
                    async token =>
                    {
                        ThrowIfAutomaticOutputStopped();
                        await target.WriteFanSpeedAsync(100, token).ConfigureAwait(false);
                        return true;
                    },
                    CancellationToken.None)
                .ConfigureAwait(false);
            return HostStopHidTransactionResult.Failed(
                failure, recoveryWriteAttempted: true, recoveryWriteSucceeded: true);
        }
        catch (SafetyOutputSuppressedException) { throw; }
        catch (Exception recoveryFailure)
        {
            logger.LogCritical(
                recoveryFailure,
                "The same-transaction host-stop ON+100 recovery write failed after the primary " +
                "terminal HID sequence failed.");
            return HostStopHidTransactionResult.Failed(
                failure, recoveryWriteAttempted: true, recoveryWriteSucceeded: false);
        }
    }

    private async Task<T> ExecuteHostStopHidStageAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken transactionCancellationToken)
    {
        using var stageDeadline = CancellationTokenSource.CreateLinkedTokenSource(
            transactionCancellationToken, automaticIoCancellation.Value ?? CancellationToken.None);
        stageDeadline.CancelAfter(HidTimeout);
        hidDispatcher.ReportProgress();
        MarkHeartbeat();
        try
        {
            return await operation(stageDeadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (SafetyProtectionDisabled ||
            (automaticIoEpoch.Value is { } epoch && epoch != Interlocked.Read(ref safetyProtectionEpoch)))
        { throw new SafetyOutputSuppressedException(); }
        finally
        {
            hidDispatcher.ReportProgress();
            MarkHeartbeat();
        }
    }

    private static bool QualificationMatchesIdentity(
        SystemShutdownOffQualification qualification,
        DeviceIdentity identity) =>
        qualification.ProtocolRevision == SystemShutdownOffQualification.CurrentProtocolRevision &&
        qualification.VendorId == identity.VendorId &&
        qualification.ProductId == identity.ProductId &&
        qualification.FirmwareVersion == identity.Version &&
        qualification.UsagePage == identity.UsagePage &&
        qualification.Usage == identity.Usage &&
        qualification.FeatureReportLength == identity.FeatureReportLength;

    private bool HasMythCoolConflictForHostStop()
    {
        try
        {
            return conflictDetector.Inspect().HasConflict;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception,
                "Myth.Cool conflict state could not be read during host-stop finalization; refusing fan-off.");
            return true;
        }
    }

    private static string FormatMythCoolConflictMessage(
        MythCoolConflictSnapshot conflict,
        string confirmedConflictMessage) =>
        conflict.InspectionFailed
            ? "Myth.Cool process state could not be verified; the operation was refused."
            : confirmedConflictMessage;

    private void SetHostStopDisposition(HostStopDisposition disposition) =>
        Volatile.Write(ref hostStopDisposition, (int)disposition);

    public async Task BestEffortWatchdogFailSafeAsync()
    {
        if (IsHardwarePoisoned)
        {
            UpdateStatus("HardwarePoisoned", FailSafeReason.HidFailure);
            return;
        }

        var gateAcquired = false;
        try
        {
            gateAcquired = await gate.WaitAsync(TimeSpan.FromMilliseconds(250)).ConfigureAwait(false);
            if (gateAcquired)
            {
                if (IsHardwarePoisoned)
                {
                    UpdateStatus("HardwarePoisoned", FailSafeReason.HidFailure);
                    return;
                }

                using var ownedDeadline = new CancellationTokenSource(HidTimeout);
                await BestEffortFailSafeAsync(ownedDeadline.Token).ConfigureAwait(false);
                UpdateStatus("ControllerStalled", FailSafeReason.ControllerStalled);
                return;
            }

            // A stalled Tick normally owns the coordinator gate. The HID dispatcher is the
            // actual single-writer serialization boundary, so use an atomic snapshot of the
            // already-trusted target and still attempt ON+100 before process termination.
            using var fallbackDeadline = new CancellationTokenSource(HidTimeout);
            await BestEffortFailSafeAsync(fallbackDeadline.Token).ConfigureAwait(false);
            UpdateStatus("ControllerStalled", FailSafeReason.ControllerStalled);
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or Win32Exception)
        {
            logger.LogCritical(exception, "The watchdog could not confirm a 100% fail-safe write.");
        }
        finally
        {
            if (gateAcquired)
            {
                gate.Release();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposeRequested, 1) != 0)
        {
            return;
        }

        // Stop accepting cache work immediately, but do not wait for filesystem I/O before
        // establishing the shutdown safety state. The bounded cache join occurs after the
        // coordinator gate has been released.
        var cacheWriterStop = StopDeviceStateCacheWriterAsync();
        using var shutdownDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var gateAcquired = false;
        try
        {
            gateAcquired = await gate.WaitAsync(TimeSpan.FromMilliseconds(250), shutdownDeadline.Token)
                .ConfigureAwait(false);
            if (!gateAcquired)
            {
                logger.LogCritical("Controller shutdown could not acquire its state gate within 250 ms.");
                Environment.ExitCode = 7;
                return;
            }

            InterruptProfileApplicationUnderGate("后台正在退出；方案剩余操作已取消，不会在重启后重放。");

            if (IsHardwarePoisoned)
            {
                Environment.ExitCode = 7;
                UpdateStatus("HardwarePoisoned", FailSafeReason.HidFailure);
                return;
            }

            if ((configuration.Commissioned || commissioningSession is not null) &&
                HostStopDisposition != HostStopDisposition.SystemShutdownOffConfirmed)
            {
                await BestEffortFailSafeAsync(shutdownDeadline.Token).ConfigureAwait(false);
            }

            await DisposeCommissioningSessionAsync(shutdownDeadline.Token).ConfigureAwait(false);
            await ReleaseDeviceAsync(cancellationToken: shutdownDeadline.Token).ConfigureAwait(false);
            await DisposeThermalSourceAsync(shutdownDeadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            logger.LogCritical("Controller shutdown exceeded its two-second safety deadline.");
            Environment.ExitCode = 7;
        }
        catch (Exception exception)
        {
            logger.LogCritical(exception, "Controller shutdown failed; the process will exit non-zero.");
            Environment.ExitCode = 7;
        }
        finally
        {
            if (gateAcquired)
            {
                gate.Release();
            }

            await cacheWriterStop.ConfigureAwait(false);
        }
    }

    private async Task RecoverPendingTakeoverTransitionAsync(CancellationToken cancellationToken)
    {
        var transition = await transitionJournalStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (transition is null)
        {
            return;
        }

        if (transition.Stage is TakeoverTransitionStage.TaskDisableInProgress or
            TakeoverTransitionStage.TaskRestoreInProgress)
        {
            throw new InvalidDataException(
                "The takeover journal records an in-progress scheduled-task mutation. Its " +
                "outcome cannot be reconstructed safely; retain the journal and perform an " +
                "explicit attended recovery. No automatic task command is allowed.");
        }

        if (string.Equals(
                transition.Audit.EventName,
                "commission.rollback.forced-uninstall-recovery",
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "A forced-uninstall takeover journal lost its stronger marker barrier.");
        }

        var backup = await RequireMatchingBackupAsync(transition, cancellationToken).ConfigureAwait(false);
        if (transition.Stage is TakeoverTransitionStage.OutcomeResolved or
            TakeoverTransitionStage.AuditAppended)
        {
            transition = await EnsureTakeoverAuditAppendedAsync(
                    transition,
                    backup,
                    "startup-terminal-audit-recovery",
                    cancellationToken)
                .ConfigureAwait(false);
            await transitionJournalStore.DeleteExactAsync(transition, CancellationToken.None)
                .ConfigureAwait(false);
            configuration = await configurationStore.LoadAsync(CancellationToken.None)
                .ConfigureAwait(false);
            runtimeState = await runtimeStateStore.LoadAsync(CancellationToken.None)
                .ConfigureAwait(false);
            logger.LogWarning(
                "Recovered terminal takeover audit transaction {TransitionId}.",
                transition.TransitionId);
            return;
        }

        switch (transition.Operation)
        {
            case TakeoverTransitionOperation.Commit:
                {
                    var taskInspection = await taskManager.InspectCurrentAgainstBackupAsync(
                            backup,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (transition.Stage == TakeoverTransitionStage.Prepared &&
                        !configuration.Commissioned &&
                        !runtimeState.Commissioned &&
                        taskInspection.Enabled == transition.TaskOriginallyEnabled &&
                        taskInspection.ExactBackupXmlMatch)
                    {
                        // The crash happened before takeover became externally observable, or a
                        // compensation already restored the original task and both false states.
                        var abandonedCurrent = CreateTakeoverAuditSnapshot(
                            configuration,
                            runtimeState,
                            taskInspection.Enabled,
                            taskInspection.CurrentXmlSha256,
                            taskInspection.ExactBackupXmlMatch,
                            taskInspection.DefinitionMatchesBackup);
                        transition = await ResolveTakeoverTransitionAsync(
                                transition,
                                TakeoverTransitionAuditResult.NotApplied,
                                TakeoverTransitionAuditAction.CommitAbandonedOriginalState,
                                abandonedCurrent,
                                resolver: null,
                                CancellationToken.None)
                            .ConfigureAwait(false);
                        await FinishTakeoverTransitionAsync(
                                transition,
                                backup,
                                "startup-commit-abandoned",
                                CancellationToken.None)
                            .ConfigureAwait(false);
                        logger.LogWarning(
                            "Abandoned incomplete takeover transition {TransitionId} in the safe original state.",
                            transition.TransitionId);
                        return;
                    }

                    if (taskInspection.Enabled || !taskInspection.DefinitionMatchesBackup)
                    {
                        throw new InvalidDataException(
                            "A commit journal claims takeover progress while the Myth.Cool task is " +
                            "enabled or its definition differs from the verified backup.");
                    }

                    if (transition.Stage == TakeoverTransitionStage.Prepared)
                    {
                        transition = await transitionJournalStore.AdvanceAsync(
                                transition,
                                transition with { Stage = TakeoverTransitionStage.TaskDisabled },
                                cancellationToken)
                            .ConfigureAwait(false);
                    }

                    var recoveredState = new ServiceRuntimeState(
                        true,
                        transition.OwnerSid,
                        transition.StartedAtUtc);
                    if (transition.Stage == TakeoverTransitionStage.TaskDisabled)
                    {
                        await runtimeStateStore.SaveAsync(recoveredState, CancellationToken.None)
                            .ConfigureAwait(false);
                        transition = await transitionJournalStore.AdvanceAsync(
                                transition,
                                transition with { Stage = TakeoverTransitionStage.RuntimeStatePersisted },
                                CancellationToken.None)
                            .ConfigureAwait(false);
                    }

                    var recoveredConfiguration = configuration with { Commissioned = true };
                    if (transition.Stage == TakeoverTransitionStage.RuntimeStatePersisted)
                    {
                        await configurationStore.SaveAsync(recoveredConfiguration, CancellationToken.None)
                            .ConfigureAwait(false);
                        transition = await transitionJournalStore.AdvanceAsync(
                                transition,
                                transition with { Stage = TakeoverTransitionStage.ConfigurationPersisted },
                                CancellationToken.None)
                            .ConfigureAwait(false);
                    }

                    if (transition.Stage != TakeoverTransitionStage.ConfigurationPersisted)
                    {
                        throw new InvalidDataException(
                            $"Unsupported takeover commit recovery stage {transition.Stage}.");
                    }

                    configuration = await configurationStore.LoadAsync(CancellationToken.None)
                        .ConfigureAwait(false);
                    runtimeState = await runtimeStateStore.LoadAsync(CancellationToken.None)
                        .ConfigureAwait(false);
                    taskInspection = await taskManager.InspectCurrentAgainstBackupAsync(
                            backup,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    var current = CreateTakeoverAuditSnapshot(
                        configuration,
                        runtimeState,
                        taskInspection.Enabled,
                        taskInspection.CurrentXmlSha256,
                        taskInspection.ExactBackupXmlMatch,
                        taskInspection.DefinitionMatchesBackup);
                    var recoveredRollbackCompensation = string.Equals(
                        transition.Audit.EventName,
                        "commission.rollback",
                        StringComparison.Ordinal);
                    transition = await ResolveTakeoverTransitionAsync(
                            transition,
                            recoveredRollbackCompensation
                                ? TakeoverTransitionAuditResult.Compensated
                                : TakeoverTransitionAuditResult.Success,
                            recoveredRollbackCompensation
                                ? TakeoverTransitionAuditAction.RollbackCompensatedToController
                                : TakeoverTransitionAuditAction.CommitCompleted,
                            current,
                            resolver: null,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    await FinishTakeoverTransitionAsync(
                            transition,
                            backup,
                            "startup-commit-recovery",
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    logger.LogWarning("Recovered incomplete takeover commit {TransitionId}.", transition.TransitionId);
                    break;
                }

            case TakeoverTransitionOperation.Rollback:
                {
                    if (transition.StartMythCoolRequested && !backup.OriginallyEnabled)
                    {
                        throw new InvalidDataException(
                            "The rollback journal requests starting a task that was originally disabled.");
                    }

                    if (transition.Stage == TakeoverTransitionStage.TaskRestored)
                    {
                        // The mutating command and its readback were already completed before
                        // this durable stage was written. Recovery may only verify the final
                        // state read-only and delete the journal; it must never replay Restore.
                        var restoredInspection = await taskManager.InspectCurrentAgainstBackupAsync(
                                backup,
                                cancellationToken)
                            .ConfigureAwait(false);
                        if (restoredInspection.Enabled != backup.OriginallyEnabled ||
                            !restoredInspection.DefinitionMatchesBackup)
                        {
                            throw new InvalidDataException(
                                "The Myth.Cool task definition/Enabled state no longer matches " +
                                "the durable TaskRestored state.");
                        }

                        // TaskRestored proves only the external task mutation. Re-establish both
                        // durable controller ownership files as decommissioned before deleting the
                        // journal that makes this recovery path unambiguous.
                        var restoredConfiguration = configuration with { Commissioned = false };
                        await configurationStore.SaveAsync(restoredConfiguration, CancellationToken.None)
                            .ConfigureAwait(false);
                        await runtimeStateStore.SaveAsync(ServiceRuntimeState.Initial, CancellationToken.None)
                            .ConfigureAwait(false);
                        configuration = restoredConfiguration;
                        runtimeState = ServiceRuntimeState.Initial;
                        var restoredCurrent = CreateTakeoverAuditSnapshot(
                            restoredConfiguration,
                            ServiceRuntimeState.Initial,
                            restoredInspection.Enabled,
                            restoredInspection.CurrentXmlSha256,
                            restoredInspection.ExactBackupXmlMatch,
                            restoredInspection.DefinitionMatchesBackup);
                        var commitCompensation = string.Equals(
                            transition.Audit.EventName,
                            "commission.commit",
                            StringComparison.Ordinal);
                        transition = await ResolveTakeoverTransitionAsync(
                                transition,
                                commitCompensation
                                    ? TakeoverTransitionAuditResult.Compensated
                                    : TakeoverTransitionAuditResult.Success,
                                commitCompensation
                                    ? TakeoverTransitionAuditAction.CommitFailedRolledBack
                                    : TakeoverTransitionAuditAction.RollbackCompleted,
                                restoredCurrent,
                                resolver: null,
                                CancellationToken.None)
                            .ConfigureAwait(false);
                        await FinishTakeoverTransitionAsync(
                                transition,
                                backup,
                                "startup-restored-rollback-recovery",
                                CancellationToken.None)
                            .ConfigureAwait(false);
                        logger.LogWarning(
                            "Verified completed takeover rollback {TransitionId} without replaying a task mutation.",
                            transition.TransitionId);
                        break;
                    }

                    var recoveredConfiguration = configuration with { Commissioned = false };
                    await configurationStore.SaveAsync(recoveredConfiguration, CancellationToken.None)
                        .ConfigureAwait(false);
                    await runtimeStateStore.SaveAsync(ServiceRuntimeState.Initial, CancellationToken.None)
                        .ConfigureAwait(false);
                    if (transition.Stage == TakeoverTransitionStage.Prepared)
                    {
                        transition = await transitionJournalStore.AdvanceAsync(
                                transition,
                                transition with { Stage = TakeoverTransitionStage.ControllerStateDecommissioned },
                                CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                    else if (transition.Stage != TakeoverTransitionStage.ControllerStateDecommissioned)
                    {
                        throw new InvalidDataException(
                            $"Unsupported takeover rollback recovery stage {transition.Stage}.");
                    }

                    configuration = recoveredConfiguration;
                    runtimeState = ServiceRuntimeState.Initial;
                    if (!await DisposeCommissioningSessionAsync().ConfigureAwait(false))
                    {
                        ThrowIfHardwarePoisoned();
                    }

                    if (!await ReleaseDeviceAsync().ConfigureAwait(false))
                    {
                        ThrowIfHardwarePoisoned();
                    }

                    // A process restart loses the proof that the original caller is still
                    // attended. Restore ownership, but never replay a request to launch Myth.Cool.
                    cancellationToken.ThrowIfCancellationRequested();
                    transition = await transitionJournalStore.AdvanceAsync(
                            transition,
                            transition with
                            {
                                Stage = TakeoverTransitionStage.TaskRestoreInProgress,
                            },
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    await taskManager.RestoreAsync(startMythCool: false, CancellationToken.None)
                        .ConfigureAwait(false);
                    transition = await transitionJournalStore.AdvanceAsync(
                            transition,
                            transition with { Stage = TakeoverTransitionStage.TaskRestored },
                            CancellationToken.None)
                        .ConfigureAwait(false);

                    var finalConfiguration = await configurationStore.LoadAsync(CancellationToken.None)
                        .ConfigureAwait(false);
                    var finalRuntime = await runtimeStateStore.LoadAsync(CancellationToken.None)
                        .ConfigureAwait(false);
                    var finalInspection = await taskManager.InspectCurrentAgainstBackupAsync(
                            backup,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    var rollbackCurrent = CreateTakeoverAuditSnapshot(
                        finalConfiguration,
                        finalRuntime,
                        finalInspection.Enabled,
                        finalInspection.CurrentXmlSha256,
                        finalInspection.ExactBackupXmlMatch,
                        finalInspection.DefinitionMatchesBackup);
                    var recoveredCommitCompensation = string.Equals(
                        transition.Audit.EventName,
                        "commission.commit",
                        StringComparison.Ordinal);
                    transition = await ResolveTakeoverTransitionAsync(
                            transition,
                            recoveredCommitCompensation
                                ? TakeoverTransitionAuditResult.Compensated
                                : TakeoverTransitionAuditResult.Success,
                            recoveredCommitCompensation
                                ? TakeoverTransitionAuditAction.CommitFailedRolledBack
                                : TakeoverTransitionAuditAction.RollbackCompleted,
                            rollbackCurrent,
                            resolver: null,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    await FinishTakeoverTransitionAsync(
                            transition,
                            backup,
                            "startup-rollback-recovery",
                            CancellationToken.None)
                        .ConfigureAwait(false);

                    logger.LogWarning("Recovered incomplete takeover rollback {TransitionId}.", transition.TransitionId);
                    break;
                }

            default:
                throw new InvalidDataException("The takeover transition operation is unsupported.");
        }
    }

    private async Task<TakeoverTransitionJournal> CompleteConfirmedCommitRecoveryAsync(
        TakeoverTransitionJournal expectedTransition,
        ScheduledTaskBackupMetadata backup,
        CancellationToken cancellationToken)
    {
        if (expectedTransition.Operation != TakeoverTransitionOperation.Commit ||
            expectedTransition.Stage != TakeoverTransitionStage.TaskDisabled)
        {
            throw new InvalidDataException(
                "Attended commit recovery requires an exact TaskDisabled local transition.");
        }

        await RequireExactTransitionAsync(expectedTransition, cancellationToken).ConfigureAwait(false);
        _ = await RequireMatchingBackupAsync(expectedTransition, cancellationToken).ConfigureAwait(false);
        var inspection = await taskManager.InspectCurrentAgainstBackupAsync(backup, cancellationToken)
            .ConfigureAwait(false);
        if (inspection.Enabled || !inspection.DefinitionMatchesBackup)
        {
            throw new InvalidDataException(
                "The Myth.Cool task no longer matches the confirmed disabled recovery state.");
        }

        var recoveredState = new ServiceRuntimeState(
            true,
            expectedTransition.OwnerSid,
            expectedTransition.StartedAtUtc);
        await runtimeStateStore.SaveAsync(recoveredState, cancellationToken).ConfigureAwait(false);
        var transition = await transitionJournalStore.AdvanceAsync(
                expectedTransition,
                expectedTransition with { Stage = TakeoverTransitionStage.RuntimeStatePersisted },
                cancellationToken)
            .ConfigureAwait(false);

        var recoveredConfiguration = configuration with { Commissioned = true };
        await configurationStore.SaveAsync(recoveredConfiguration, cancellationToken).ConfigureAwait(false);
        transition = await transitionJournalStore.AdvanceAsync(
                transition,
                transition with { Stage = TakeoverTransitionStage.ConfigurationPersisted },
                cancellationToken)
            .ConfigureAwait(false);

        configuration = recoveredConfiguration;
        runtimeState = recoveredState;
        return transition;
    }

    private async Task<TakeoverTransitionJournal> CompleteConfirmedRollbackRecoveryAsync(
        TakeoverTransitionJournal expectedTransition,
        ScheduledTaskBackupMetadata backup,
        CancellationToken cancellationToken)
    {
        if (expectedTransition.Operation != TakeoverTransitionOperation.Rollback ||
            expectedTransition.Stage != TakeoverTransitionStage.TaskRestored)
        {
            throw new InvalidDataException(
                "Attended rollback recovery requires an exact TaskRestored local transition.");
        }

        await RequireExactTransitionAsync(expectedTransition, cancellationToken).ConfigureAwait(false);
        _ = await RequireMatchingBackupAsync(expectedTransition, cancellationToken).ConfigureAwait(false);
        var inspection = await taskManager.InspectCurrentAgainstBackupAsync(backup, cancellationToken)
            .ConfigureAwait(false);
        if (inspection.Enabled != backup.OriginallyEnabled || !inspection.DefinitionMatchesBackup)
        {
            throw new InvalidDataException(
                "The Myth.Cool task definition/Enabled state no longer matches the confirmed " +
                "restored recovery state.");
        }

        var recoveredConfiguration = configuration with { Commissioned = false };
        await configurationStore.SaveAsync(recoveredConfiguration, cancellationToken).ConfigureAwait(false);
        await runtimeStateStore.SaveAsync(ServiceRuntimeState.Initial, cancellationToken).ConfigureAwait(false);

        configuration = recoveredConfiguration;
        runtimeState = ServiceRuntimeState.Initial;
        return expectedTransition;
    }

    private async Task RequireExactTransitionAsync(
        TakeoverTransitionJournal expected,
        CancellationToken cancellationToken)
    {
        var current = await transitionJournalStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (current is null || !TakeoverTransitionJournalStore.Equivalent(current, expected))
        {
            throw new InvalidDataException(
                "The takeover transition journal changed during attended recovery.");
        }
    }

    private async Task<ScheduledTaskBackupMetadata> RequireMatchingBackupAsync(
        TakeoverTransitionJournal transition,
        CancellationToken cancellationToken)
    {
        var backup = await taskManager.TryReadVerifiedBackupAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException(
                "The takeover transition does not have a verified Myth.Cool task backup.");
        if (!string.Equals(backup.TaskName, ServiceConstants.MythCoolTaskName, StringComparison.Ordinal) ||
            !string.Equals(backup.XmlSha256, transition.TaskBackupSha256,
                StringComparison.OrdinalIgnoreCase) ||
            backup.OriginallyEnabled != transition.TaskOriginallyEnabled)
        {
            throw new InvalidDataException(
                "The verified Myth.Cool task backup does not match the takeover transition journal.");
        }

        return backup;
    }

    private async Task<TakeoverTransitionJournal> ResolveTakeoverTransitionAsync(
        TakeoverTransitionJournal transition,
        TakeoverTransitionAuditResult result,
        TakeoverTransitionAuditAction action,
        JsonElement current,
        PipePeerIdentity? resolver,
        CancellationToken cancellationToken)
    {
        if (transition.Stage is TakeoverTransitionStage.OutcomeResolved or
            TakeoverTransitionStage.AuditAppended)
        {
            throw new InvalidDataException("The takeover transition is already resolved.");
        }

        var resolved = transition with
        {
            Stage = TakeoverTransitionStage.OutcomeResolved,
            Audit = transition.Audit with
            {
                Result = result,
                Action = action,
                Current = current.Clone(),
                ResolvedByCallerSid = resolver?.CallerSid,
                ResolvedBySource = resolver?.Source,
            },
        };
        return await transitionJournalStore.AdvanceAsync(
                transition,
                resolved,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<TakeoverTransitionJournal> EnsureTakeoverAuditAppendedAsync(
        TakeoverTransitionJournal transition,
        ScheduledTaskBackupMetadata backup,
        string recoveryContext,
        CancellationToken cancellationToken)
    {
        if (transition.Stage is not (
                TakeoverTransitionStage.OutcomeResolved or
                TakeoverTransitionStage.AuditAppended))
        {
            throw new InvalidDataException(
                "Only a resolved takeover transition can append its durable audit record.");
        }

        await RequireExactTransitionAsync(transition, cancellationToken).ConfigureAwait(false);
        _ = await RequireMatchingBackupAsync(transition, cancellationToken).ConfigureAwait(false);
        var persistedConfiguration = await configurationStore.LoadAsync(cancellationToken)
            .ConfigureAwait(false);
        var persistedRuntime = await runtimeStateStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var inspection = await taskManager.InspectCurrentAgainstBackupAsync(backup, cancellationToken)
            .ConfigureAwait(false);
        ValidateTakeoverAuditTerminalState(
            transition,
            persistedConfiguration,
            persistedRuntime,
            inspection);
        var exactCurrent = CreateTakeoverAuditSnapshot(
            persistedConfiguration,
            persistedRuntime,
            inspection.Enabled,
            inspection.CurrentXmlSha256,
            inspection.ExactBackupXmlMatch,
            inspection.DefinitionMatchesBackup);
        if (!JsonElement.DeepEquals(exactCurrent, transition.Audit.Current))
        {
            throw new InvalidDataException(
                "The durable controller/task state drifted from the resolved takeover audit snapshot.");
        }

        if (transition.Stage == TakeoverTransitionStage.AuditAppended)
        {
            if (!await auditLog.ContainsTransactionAsync(transition.TransitionId, cancellationToken)
                .ConfigureAwait(false))
            {
                throw new InvalidDataException(
                    "The takeover journal claims AuditAppended but its transaction is not discoverable.");
            }

            return transition;
        }

        if (!await auditLog.ContainsTransactionAsync(transition.TransitionId, cancellationToken)
            .ConfigureAwait(false))
        {
            await auditLog.WriteAsync(
                    transition.Audit.EventName,
                    transition.Audit.Result switch
                    {
                        TakeoverTransitionAuditResult.Success => "success",
                        TakeoverTransitionAuditResult.NotApplied => "not-applied",
                        TakeoverTransitionAuditResult.Compensated => "compensated",
                        _ => throw new InvalidDataException(
                            "A pending takeover audit result cannot be appended."),
                    },
                    transition.Audit.CallerSid,
                    transition.Audit.Source,
                    new
                    {
                        previous = transition.Audit.Previous,
                        current = transition.Audit.Current,
                        action = transition.Audit.Action.ToString(),
                        operation = transition.Operation.ToString(),
                        transitionId = transition.TransitionId,
                        taskBackupSha256 = transition.TaskBackupSha256,
                        taskOriginallyEnabled = transition.TaskOriginallyEnabled,
                        startMythCoolRequested = transition.StartMythCoolRequested,
                        resolvedByCallerSid = transition.Audit.ResolvedByCallerSid,
                        resolvedBySource = transition.Audit.ResolvedBySource,
                        recovery = recoveryContext,
                    },
                    CancellationToken.None,
                    transition.TransitionId)
                .ConfigureAwait(false);
            if (!await auditLog.ContainsTransactionAsync(
                    transition.TransitionId,
                    CancellationToken.None).ConfigureAwait(false))
            {
                throw new IOException(
                    "The takeover audit append completed without a discoverable transaction record.");
            }
        }

        var auditAppended = transition with { Stage = TakeoverTransitionStage.AuditAppended };
        return await transitionJournalStore.AdvanceAsync(
                transition,
                auditAppended,
                CancellationToken.None)
            .ConfigureAwait(false);
    }

    private static void ValidateTakeoverAuditTerminalState(
        TakeoverTransitionJournal transition,
        ControllerConfiguration persistedConfiguration,
        ServiceRuntimeState persistedRuntime,
        ScheduledTaskRecoveryInspection inspection)
    {
        if (!inspection.DefinitionMatchesBackup)
        {
            throw new InvalidDataException(
                "The Myth.Cool task definition drifted from the takeover audit target.");
        }

        var controllerOwnsTarget = transition.Audit.Action is
            TakeoverTransitionAuditAction.CommitCompleted or
            TakeoverTransitionAuditAction.RollbackCompensatedToController;
        if (controllerOwnsTarget)
        {
            if (!persistedConfiguration.Commissioned ||
                !persistedRuntime.Commissioned ||
                !string.Equals(persistedRuntime.OwnerSid, transition.OwnerSid, StringComparison.Ordinal) ||
                inspection.Enabled)
            {
                throw new InvalidDataException(
                    "The commissioned controller/task state does not match the resolved takeover audit action.");
            }

            return;
        }

        if (transition.Audit.Action is not (
                TakeoverTransitionAuditAction.CommitAbandonedOriginalState or
                TakeoverTransitionAuditAction.CommitFailedRolledBack or
                TakeoverTransitionAuditAction.RollbackCompleted or
                TakeoverTransitionAuditAction.ForcedUninstallRollbackCompleted))
        {
            throw new InvalidDataException("The resolved takeover audit action is unsupported.");
        }

        if (persistedConfiguration.Commissioned ||
            persistedRuntime != ServiceRuntimeState.Initial ||
            inspection.Enabled != transition.TaskOriginallyEnabled)
        {
            throw new InvalidDataException(
                "The restored controller/task state does not match the resolved takeover audit action.");
        }
    }

    private async Task FinishTakeoverTransitionAsync(
        TakeoverTransitionJournal transition,
        ScheduledTaskBackupMetadata backup,
        string recoveryContext,
        CancellationToken cancellationToken)
    {
        transition = await EnsureTakeoverAuditAppendedAsync(
                transition,
                backup,
                recoveryContext,
                cancellationToken)
            .ConfigureAwait(false);
        await transitionJournalStore.DeleteExactAsync(transition, CancellationToken.None)
            .ConfigureAwait(false);
    }

    private async Task UpdateConfigurationAsync(
        Func<ControllerConfiguration, ControllerConfiguration> update,
        string eventName,
        PipePeerIdentity peer,
        CancellationToken cancellationToken,
        long? manualRevision = null)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken = ControllerRequestAdmissionScope.Admit(cancellationToken);
            EnsureInitialized();
            ThrowIfHardwarePoisoned();
            ThrowIfHardwareAccessBlocked();
            if (manualRevision is { } revision) RequireCurrentManualRequest(revision);
            var next = update(configuration);
            next.Validate();
            await PersistConfigurationMutationAsync(next, eventName, peer, cancellationToken)
                .ConfigureAwait(false);
            _ = Interlocked.Exchange(ref shutdownQualificationPending, null);
            if (eventName == PipeOperations.SpeedSet) policy.RequestManualWrite();
            else policy.Reset();
            if (eventName == PipeOperations.SpeedSet ||
                eventName == PipeOperations.ModeSet && next.Mode == ControlMode.Auto)
            {
                manualCommandUnconfirmed = false;
                observedManualControlHold = false;
                observedManualPersistenceUnconfirmed = false;
                observedManualSpeedPercent = null;
                observedManualPowerOff = false;
                manualPower.RequestOn();
                // This explicit choice supersedes physical changes that predate
                // its first device transaction. GET after SET confirms newer input.
                lastObservedManualDevice = null;
                lastObservedManualDeviceBaseline = null;
                explicitControlBaselineResetPending = true;
            }
            if (AutomaticAdjustmentsDisabled)
            {
                SetPassiveDecision();
                if (configuration.Mode == ControlMode.Released && !await ReleaseDeviceAsync().ConfigureAwait(false))
                    throw new ControllerOperationException(PipeErrorCodes.HidTimeout, "Release was not confirmed; do not assume ownership is free.");
            }
            UpdateStatus(configuration.Commissioned ? "ConfigurationChanged" : "NotCommissioned");
        }
        catch (ConfigurationValidationException exception)
        {
            throw Invalid(exception.Message, exception);
        }
        catch (ControllerOperationException)
        {
            throw;
        }
        catch (Exception exception) when (!IsFatalPersistenceFailure(exception))
        {
            throw new ControllerOperationException(
                PipeErrorCodes.InternalError,
                "The controller configuration mutation did not complete safely; inspect audit-mutation status before retrying.",
                exception);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task UpdateThermalAsync(CancellationToken cancellationToken)
    {
        try
        {
            lastThermal = await ReadThermalWithTimeoutAsync(cancellationToken).ConfigureAwait(false);
            ObserveCommissioningBackgroundThermal(lastThermal, lastThermalSampleSequence);
        }
        catch (Exception exception) when (IsHardwareFailure(exception))
        {
            ResetCommissioningThermalReadiness();
            lastThermal = new ThermalSnapshot(timeProvider.GetUtcNow(), null, []);
            logger.LogWarning(exception, "Hardware telemetry is unavailable.");
        }
    }

    private void ObserveCommissioningBackgroundThermal(
        ThermalSnapshot thermal,
        long thermalSampleSequence)
    {
        var issue = ClassifyCommissioningThermal(thermal, requireBelowSixty: false);
        if (issue is not null ||
            thermalSampleSequence <= 0 ||
            (lastCommissioningBackgroundSampleSequence is { } previous &&
             thermalSampleSequence <= previous))
        {
            ResetCommissioningThermalReadiness();
            return;
        }

        lastCommissioningBackgroundSampleSequence = thermalSampleSequence;
        commissioningValidCpuSamples = Math.Min(
            CommissioningRequiredValidSamples,
            commissioningValidCpuSamples + 1);
    }

    private void ResetCommissioningThermalReadiness()
    {
        commissioningValidCpuSamples = 0;
        lastCommissioningBackgroundSampleSequence = null;
    }

    private async Task<bool> MonitorCommissioningSessionAsync(CancellationToken cancellationToken)
    {
        var session = commissioningSession
            ?? throw new InvalidOperationException("A commissioning session was expected.");
        var conflict = conflictDetector.Inspect();
        if (conflict.HasConflict)
        {
            await AbortCommissioningForSafetyAsync(
                session,
                new CommissioningSafetyIssue(
                    FailSafeReason.MythCoolConflict,
                    PipeErrorCodes.MythCoolConflict,
                    "Myth.Cool started while commissioning was active.",
                    "CommissioningAbortedMythCoolConflict"),
                cancellationToken).ConfigureAwait(false);
            return true;
        }

        var thermalIssue = ClassifyCommissioningThermal(lastThermal, requireBelowSixty: false);
        if (thermalIssue is not null)
        {
            await AbortCommissioningForSafetyAsync(session, thermalIssue, cancellationToken)
                .ConfigureAwait(false);
            return true;
        }

        if (session.Device is null)
        {
            return false;
        }

        try
        {
            lastDeviceState = await ReadDeviceStateAsync(session.Device, cancellationToken).ConfigureAwait(false);
            if (!lastDeviceState.PoweredOn)
            {
                throw new IOException("The V12 Ultra reported that its fan is not powered on.");
            }

            lastSuccessfulRead = timeProvider.GetUtcNow();
        }
        catch (Exception exception) when (IsHardwareFailure(exception))
        {
            logger.LogError(exception, "The V12 Ultra failed its commissioning-session health check.");
            await AbortCommissioningForSafetyAsync(
                session,
                new CommissioningSafetyIssue(
                    FailSafeReason.HidFailure,
                    PipeErrorCodes.HidTimeout,
                    "The V12 Ultra stopped responding during commissioning.",
                    "CommissioningAbortedHidFailure"),
                cancellationToken).ConfigureAwait(false);
            return true;
        }

        return false;
    }

    private async Task<ThermalSnapshot> RequireSafeCommissioningThermalAsync(
        CommissioningSession session,
        bool requireBelowSixty,
        CancellationToken cancellationToken)
    {
        try
        {
            lastThermal = await ReadThermalWithTimeoutAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsHardwareFailure(exception) || exception is OperationCanceledException)
        {
            var issue = new CommissioningSafetyIssue(
                FailSafeReason.TelemetryStale,
                PipeErrorCodes.TelemetryStale,
                "A fresh, valid CPU temperature is required throughout commissioning.",
                "CommissioningAbortedTelemetryStale");
            await AbortCommissioningForSafetyAsync(session, issue, CancellationToken.None).ConfigureAwait(false);
            throw new ControllerOperationException(issue.ErrorCode, issue.Message, exception);
        }

        var thermalIssue = ClassifyCommissioningThermal(lastThermal, requireBelowSixty);
        if (thermalIssue is not null)
        {
            await AbortCommissioningForSafetyAsync(session, thermalIssue, CancellationToken.None)
                .ConfigureAwait(false);
            throw new ControllerOperationException(thermalIssue.ErrorCode, thermalIssue.Message);
        }

        return lastThermal;
    }

    private async Task<ThermalSnapshot> RequireCommissioningProbeThermalReadyAsync(
        CancellationToken cancellationToken)
    {
        if (commissioningValidCpuSamples < CommissioningRequiredValidSamples)
        {
            throw new ControllerOperationException(
                PipeErrorCodes.TelemetryStale,
                $"Commissioning requires {CommissioningRequiredValidSamples} consecutive valid background CPU temperature samples before probing hardware. " +
                ThermalTroubleshooting.English);
        }

        var cachedIssue = ClassifyCommissioningThermal(lastThermal, requireBelowSixty: false);
        if (cachedIssue is not null)
        {
            ResetCommissioningThermalReadiness();
            throw new ControllerOperationException(cachedIssue.ErrorCode, cachedIssue.Message);
        }

        try
        {
            // This verifies that the source is still live immediately before HID enumeration.
            // It deliberately does not increment the background-sample readiness counter.
            lastThermal = await ReadThermalWithTimeoutAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsHardwareFailure(exception) || exception is OperationCanceledException)
        {
            ResetCommissioningThermalReadiness();
            throw new ControllerOperationException(
                PipeErrorCodes.TelemetryStale,
                "A fresh, valid CPU temperature is required before commissioning can begin.",
                exception);
        }

        var freshIssue = ClassifyCommissioningThermal(lastThermal, requireBelowSixty: false);
        if (freshIssue is not null)
        {
            ResetCommissioningThermalReadiness();
            throw new ControllerOperationException(freshIssue.ErrorCode, freshIssue.Message);
        }

        return lastThermal;
    }

    private CommissioningSafetyIssue? ClassifyCommissioningThermal(
        ThermalSnapshot thermal,
        bool requireBelowSixty)
    {
        var age = timeProvider.GetUtcNow() - thermal.CapturedAt;
        if (!thermal.CpuIsValid || thermal.ControlCelsius is null ||
            thermal.CapturedAt == DateTimeOffset.MinValue ||
            age > TimeSpan.FromSeconds(10) || age < TimeSpan.FromSeconds(-5))
        {
            return new CommissioningSafetyIssue(
                FailSafeReason.TelemetryStale,
                PipeErrorCodes.TelemetryStale,
                "A fresh, valid CPU temperature is required throughout commissioning.",
                "CommissioningAbortedTelemetryStale");
        }

        if (thermal.ControlCelsius >= 90)
        {
            return new CommissioningSafetyIssue(
                FailSafeReason.EmergencyTemperature,
                PipeErrorCodes.FailSafeActive,
                "Commissioning was aborted because the control temperature reached 90 C.",
                "CommissioningAbortedEmergencyTemperature");
        }

        if (requireBelowSixty && thermal.ControlCelsius >= 60)
        {
            return new CommissioningSafetyIssue(
                FailSafeReason.EmergencyTemperature,
                PipeErrorCodes.FailSafeActive,
                "The staged low-speed test was aborted because the control temperature reached 60 C.",
                "CommissioningAbortedUnsafeTestTemperature");
        }

        return null;
    }

    private async Task<CommissioningFailSafeAttempt> AbortCommissioningForSafetyAsync(
        CommissioningSession session,
        CommissioningSafetyIssue issue,
        CancellationToken cancellationToken)
    {
        var operationAudit = activeSafetyOperationAudit;
        if (operationAudit is null)
        {
            var kind = session.Device is not null && session.OriginalState is not null
                ? SafetyOperationKind.HidWithOn100Recovery
                : SafetyOperationKind.SessionOnly;
            operationAudit = await BeginSafetyOperationAuditAsync(
                    "commission.safety-abort",
                    session.OwnerSid,
                    "service",
                    kind,
                    new
                    {
                        sessionId = session.Id,
                        sessionActive = true,
                        stage = session.Stage.ToString(),
                        deviceAttached = session.Device is not null,
                    },
                    new
                    {
                        sessionId = session.Id,
                        sessionActive = false,
                        requestedPercent = 100,
                        reason = issue.ErrorCode,
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var cleanup = await CleanupCommissioningSessionAsync(session).ConfigureAwait(false);
        var failSafe = cleanup.FailSafe;
        policy.Reset();
        UpdateStatus(issue.ServiceState, issue.Reason, session.ProbedIdentity);
        var hidDispositionConfirmed = operationAudit.Kind == SafetyOperationKind.SessionOnly ||
            failSafe.ReadbackConfirmed;
        var effectKnownNotApplied = !SafetyProtectionDisabled && operationAudit.Kind == SafetyOperationKind.HidWithOn100Recovery &&
            !failSafe.WriteAttempted && cleanup.SessionReleased;
        if (cleanup.SessionReleased && (hidDispositionConfirmed || effectKnownNotApplied))
        {
            var outcome = operationAudit.EventName == "commission.safety-abort"
                ? SafetyOperationAuditOutcome.Success
                : effectKnownNotApplied
                    ? SafetyOperationAuditOutcome.NotApplied
                    : SafetyOperationAuditOutcome.Compensated;
            await ResolveAndFinishSafetyOperationAuditAsync(
                operationAudit,
                outcome,
                new
                {
                    sessionId = session.Id,
                    sessionActive = false,
                    stage = (string?)null,
                    reason = issue.ErrorCode,
                    issue.Message,
                    requestedPercent = failSafe.RequestedPercent,
                    writeAttempted = failSafe.WriteAttempted,
                    writeSucceeded = failSafe.WriteSucceeded,
                    readbackConfirmed = failSafe.ReadbackConfirmed,
                    sessionReleased = cleanup.SessionReleased,
                    requiresProbe = true,
                },
                CancellationToken.None).ConfigureAwait(false);
        }
        else
        {
            var failure = new InvalidDataException(
                "The commissioning safety abort could not prove the required ON-at-100 disposition and session release.");
            LatchSafetyOperationAuditBlocked(
                failure,
                activeSafetyOperationAudit ?? operationAudit,
                "commissioning safety abort disposition");
        }

        return failSafe;
    }

    private async Task SafeAuditAsync(
        string eventName,
        string result,
        string? callerSid,
        string? source,
        object? details,
        CancellationToken cancellationToken)
    {
        try
        {
            await auditLog.WriteAsync(eventName, result, callerSid, source, details, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Could not persist audit event {EventName}.", eventName);
        }
    }

    private Task AuditTaskRecoveryAsync(
        string result,
        string action,
        TakeoverTransitionJournal transition,
        ScheduledTaskRecoveryInspection? inspection,
        PipePeerIdentity peer,
        CancellationToken cancellationToken) =>
        SafeAuditAsync(
            PipeOperations.CommissionRecoverTaskTransition,
            result,
            peer.CallerSid,
            peer.Source,
            new
            {
                transitionId = transition.TransitionId,
                operation = transition.Operation.ToString(),
                stage = transition.Stage.ToString(),
                action,
                taskBackupSha256 = transition.TaskBackupSha256,
                taskOriginallyEnabled = transition.TaskOriginallyEnabled,
                currentTaskEnabled = inspection?.Enabled,
                currentTaskXmlSha256 = inspection?.CurrentXmlSha256,
                exactBackupXmlMatch = inspection?.ExactBackupXmlMatch,
                definitionMatchesBackup = inspection?.DefinitionMatchesBackup,
                configurationCommissioned = configuration.Commissioned,
                runtimeCommissioned = runtimeState.Commissioned,
                startedMythCool = false,
            },
            cancellationToken);

    private static bool RecoveryConfigurationMatches(
        ControllerConfiguration memory,
        ControllerConfiguration persisted) =>
        memory.SchemaVersion == persisted.SchemaVersion &&
        memory.Commissioned == persisted.Commissioned &&
        memory.Mode == persisted.Mode &&
        memory.ManualSpeedPercent == persisted.ManualSpeedPercent &&
        memory.StartupFanPercent == persisted.StartupFanPercent &&
        memory.SystemShutdownFanBehavior == persisted.SystemShutdownFanBehavior &&
        memory.ShutdownOffQualification == persisted.ShutdownOffQualification &&
        memory.Curve.Points.SequenceEqual(persisted.Curve.Points);

    private static object CreateConfigurationAuditSnapshot(ControllerConfiguration value) => new
    {
        schemaVersion = value.SchemaVersion,
        commissioned = value.Commissioned,
        mode = value.Mode.ToString(),
        manualSpeedPercent = value.ManualSpeedPercent,
        startupFanPercent = value.StartupFanPercent,
        curve = value.Curve.Points.Select(point => new
        {
            temperatureCelsius = point.TemperatureCelsius,
            fanPercent = point.FanPercent,
        }).ToArray(),
        systemShutdownFanBehavior = value.SystemShutdownFanBehavior.ToString(),
        shutdownOffQualified = value.ShutdownOffQualification is not null,
    };

    private static JsonElement CreateTakeoverAuditSnapshot(
        ControllerConfiguration snapshotConfiguration,
        ServiceRuntimeState snapshotRuntime,
        bool mythCoolTaskEnabled,
        string currentTaskXmlSha256,
        bool exactBackupXmlMatch,
        bool definitionMatchesBackup) =>
        JsonSerializer.SerializeToElement(
            new
            {
                configurationCommissioned = snapshotConfiguration.Commissioned,
                runtimeCommissioned = snapshotRuntime.Commissioned,
                ownerSid = snapshotRuntime.OwnerSid,
                mythCoolTaskEnabled,
                currentTaskXmlSha256,
                exactBackupXmlMatch,
                definitionMatchesBackup,
                configuration = AuditConfigurationSnapshot.FromConfiguration(snapshotConfiguration),
                runtime = new
                {
                    commissioned = snapshotRuntime.Commissioned,
                    ownerSid = snapshotRuntime.OwnerSid,
                    commissionedAtUtc = snapshotRuntime.CommissionedAtUtc,
                },
                task = new
                {
                    enabled = mythCoolTaskEnabled,
                    currentXmlSha256 = currentTaskXmlSha256,
                    exactBackupXmlMatch,
                    definitionMatchesBackup,
                },
            },
            AuditMutationJournalStore.SerializerOptions);

    private static TakeoverTransitionAudit CreatePendingTakeoverAudit(
        string eventName,
        string callerSid,
        string source,
        JsonElement previous)
    {
        var audit = new TakeoverTransitionAudit(
            eventName,
            callerSid,
            source,
            TakeoverTransitionAuditResult.Pending,
            TakeoverTransitionAuditAction.Pending,
            previous.Clone(),
            previous.Clone(),
            ResolvedByCallerSid: null,
            ResolvedBySource: null);
        return audit;
    }

    private async Task<ThermalSnapshot> ReadThermalWithTimeoutAsync(
        CancellationToken cancellationToken)
    {
        var thermal = await RunThermalOperationAsync(
                token => thermalSource.ReadAsync(token),
                ThermalTimeout,
                cancellationToken)
            .ConfigureAwait(false);
        lastThermalSampleSequence = checked(lastThermalSampleSequence + 1);
        return thermal;
    }

    private async Task<V12DeviceState> ReadDeviceStateAsync(
        IV12Device target,
        CancellationToken cancellationToken)
    {
        var state = await RunHidOperationAsync(
                token => target.ReadStateAsync(token), HidTimeout, cancellationToken,
                timeoutPoisonResource: "device feature read",
                timeoutRecoveryTarget: target)
            .ConfigureAwait(false);
        Volatile.Write(ref deviceStateCacheSnapshot, new DeviceStateCacheSnapshot(target.Identity, state));
        QueueDeviceStateCachePersistence(target.Identity, state);
        return state;
    }

    private async Task<V12DeviceState> ReadInitialStateOrRestoreCacheAsync(
        IV12Device target,
        CancellationToken cancellationToken)
    {
        try
        {
            return await ReadDeviceStateAsync(target, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception liveReadFailure) when (
            IsHardwareFailure(liveReadFailure) &&
            liveReadFailure is not TimeoutException and not OperationCanceledException)
        {
            // Disk I/O is deliberately not awaited while the coordinator gate and the freshly
            // opened device are held. Only an already-validated in-memory snapshot may recover
            // lighting bytes after the live read fails.
            var snapshot = Volatile.Read(ref deviceStateCacheSnapshot);
            if (snapshot is null || snapshot.Identity != target.Identity)
            {
                throw new IOException(
                    "The new V12 Ultra handle failed its first read and no prevalidated exact " +
                    "identity-bound state cache is ready; no safety write was attempted.",
                    liveReadFailure);
            }

            var cachedState = snapshot.State;

            if (target is not IV12TrustedStateInitializer initializer)
            {
                throw new IOException(
                    "The new V12 Ultra handle cannot consume the validated safety cache; no write was attempted.",
                    liveReadFailure);
            }

            await RunHidOperationAsync(async token =>
            {
                await initializer.InitializeTrustedStateAsync(cachedState, token).ConfigureAwait(false);
                return true;
            }, HidTimeout, cancellationToken).ConfigureAwait(false);

            logger.LogWarning(
                liveReadFailure,
                "The V12 Ultra first read failed. A validated state cache for the exact device identity " +
                "was restored only to preserve lighting during the mandatory ON+100 write.");
            return cachedState;
        }
    }

    private async Task PreloadDeviceStateCacheAsync()
    {
        try
        {
            var cachedState = await RunCacheIoAsync(
                    token => deviceStateCacheStore.LoadAsync(SupportedDeviceIdentity, token),
                    CancellationToken.None)
                .ConfigureAwait(false);
            if (cachedState is not null)
            {
                _ = Interlocked.CompareExchange(
                    ref deviceStateCacheSnapshot,
                    new DeviceStateCacheSnapshot(SupportedDeviceIdentity, cachedState),
                    null);
            }
        }
        catch (Exception exception)
        {
            Volatile.Write(ref deviceStateCacheError, ClassifyDeviceStateCacheFailure(exception));
            logger.LogWarning(exception,
                "The identity-bound V12 Ultra state cache could not be preloaded. Live device reads remain authoritative.");
        }
    }

    private void QueueDeviceStateCachePersistence(DeviceIdentity identity, V12DeviceState state)
    {
        var pending = new PendingDeviceStateCacheWrite(identity, state, timeProvider.GetUtcNow());
        lock (deviceStateCacheSync)
        {
            if (deviceStateCacheWriterStopping)
            {
                return;
            }

            // Only the newest fully validated report matters. If a filesystem call stalls,
            // subsequent reads replace this single pending value instead of accumulating work.
            pendingDeviceStateCacheWrite = pending;
            if (deviceStateCacheWriterRunning)
            {
                return;
            }

            deviceStateCacheWriterRunning = true;
            deviceStateCacheWriter = Task.Run(DrainDeviceStateCacheWritesAsync);
        }
    }

    private async Task DrainDeviceStateCacheWritesAsync()
    {
        while (true)
        {
            PendingDeviceStateCacheWrite? pending;
            lock (deviceStateCacheSync)
            {
                pending = pendingDeviceStateCacheWrite;
                pendingDeviceStateCacheWrite = null;
                if (pending is null || deviceStateCacheWriterStopping)
                {
                    deviceStateCacheWriterRunning = false;
                    return;
                }
            }

            await PersistDeviceStateCacheWriteAsync(pending).ConfigureAwait(false);
        }
    }

    private async Task PersistDeviceStateCacheWriteAsync(PendingDeviceStateCacheWrite pending)
    {
        using var deadline = new CancellationTokenSource(CacheIoTimeout);
        var saveTask = Task.Run(() => deviceStateCacheStore.SaveAsync(
            pending.Identity,
            pending.State,
            pending.CapturedAtUtc,
            deadline.Token));
        try
        {
            await saveTask.WaitAsync(CacheIoTimeout).ConfigureAwait(false);
            Volatile.Write(ref deviceStateCacheError, null);
        }
        catch (Exception exception)
        {
            deadline.Cancel();
            var reported = exception is OperationCanceledException && deadline.IsCancellationRequested
                ? new TimeoutException("The device-state cache write exceeded its 500 ms deadline.", exception)
                : exception;
            Volatile.Write(ref deviceStateCacheError, ClassifyDeviceStateCacheFailure(reported));
            logger.LogError(reported,
                "A valid V12 Ultra state was read, but its identity-bound safety cache could not be persisted.");

            // A synchronous Flush/Replace may ignore cancellation. Keep exactly one background
            // observer attached to it and do not start another cache operation until it returns.
            await ObserveCacheOperationAsync(saveTask).ConfigureAwait(false);
        }
    }

    private async Task<T> RunCacheIoAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(CacheIoTimeout);
        var operationTask = Task.Run(() => operation(deadline.Token));
        try
        {
            return await operationTask.WaitAsync(CacheIoTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            deadline.Cancel();
            _ = ObserveCacheOperationAsync(operationTask);
            throw new TimeoutException("The device-state cache operation exceeded its 500 ms deadline.");
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            _ = ObserveCacheOperationAsync(operationTask);
            throw new TimeoutException(
                "The device-state cache operation exceeded its 500 ms deadline.", exception);
        }
        catch
        {
            deadline.Cancel();
            throw;
        }
    }

    private static async Task ObserveCacheOperationAsync(Task operation)
    {
        try
        {
            await operation.ConfigureAwait(false);
        }
        catch
        {
            // The bounded caller already recorded the cache failure. This observer exists only
            // to consume a late task exception without ever blocking the controller gate.
        }
    }

    private async Task StopDeviceStateCacheWriterAsync()
    {
        Task writer;
        lock (deviceStateCacheSync)
        {
            deviceStateCacheWriterStopping = true;
            pendingDeviceStateCacheWrite = null;
            writer = Task.WhenAll(deviceStateCacheWriter, deviceStateCachePreload);
        }

        try
        {
            await writer.WaitAsync(CacheWriterStopBudget).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is TimeoutException or OperationCanceledException)
        {
            logger.LogWarning(exception,
                "The device-state cache writer did not stop within its bounded shutdown budget.");
        }
    }

    private Task WriteFanSpeedAsync(
        IV12Device target,
        byte percent,
        CancellationToken cancellationToken) =>
        RunHidOperationAsync(async token =>
        {
            ThrowIfAutomaticOutputStopped();
            await target.WriteFanSpeedAsync(percent, token).ConfigureAwait(false);
            return true;
        }, HidTimeout, cancellationToken,
            timeoutPoisonResource: $"device fan write ({percent}%)",
            timeoutRecoveryTarget: target);

    private async Task<CommissioningCleanupResult> CleanupCommissioningSessionAsync(
        CommissioningSession session)
    {
        var result = CommissioningFailSafeAttempt.NotAttempted;
        var sessionReleased = !ReferenceEquals(commissioningSession, session);
        try
        {
            if (ReferenceEquals(commissioningSession, session))
            {
                result = await TryApplyCommissioningFailSafeAsync(session).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            // Cleanup is not allowed to replace the operation that caused it. The attempt helper
            // is intentionally defensive, but retain this outer guard for future implementations.
            logger.LogError(exception, "An unexpected error escaped the commissioning fail-safe attempt.");
        }
        finally
        {
            if (ReferenceEquals(commissioningSession, session))
            {
                sessionReleased = await DisposeCommissioningSessionAsync(CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }

        return new CommissioningCleanupResult(result, sessionReleased);
    }

    private async Task<CommissioningFailSafeAttempt> TryApplyCommissioningFailSafeAsync(
        CommissioningSession session)
    {
        if (SafetyProtectionDisabled || session.Device is null || session.OriginalState is null)
        {
            return CommissioningFailSafeAttempt.NotAttempted;
        }

        var result = new CommissioningFailSafeAttempt(
            RequestedPercent: 100,
            WriteAttempted: true,
            WriteSucceeded: false,
            ReadbackConfirmed: false);
        try
        {
            await WriteFanSpeedAsync(session.Device, 100, CancellationToken.None).ConfigureAwait(false);
            result = result with { WriteSucceeded = true };
            lastSuccessfulWrite = timeProvider.GetUtcNow();
            lastWrittenPercent = 100;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception,
                "The commissioning cleanup could not complete its requested 100% write.");
            return result;
        }

        try
        {
            var readback = await ReadDeviceStateAsync(session.Device, CancellationToken.None)
                .ConfigureAwait(false);
            lastSuccessfulRead = timeProvider.GetUtcNow();
            lastDeviceState = readback;
            result = result with
            {
                ReadbackConfirmed = readback.PoweredOn &&
                    readback.FanSpeedPercent == 100 &&
                    LightingMatches(session.OriginalState, readback),
            };
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception,
                "The commissioning cleanup could not confirm its requested 100% state.");
        }

        return result;
    }

    private async Task BestEffortFailSafeAsync(CancellationToken cancellationToken)
    {
        if (SafetyProtectionDisabled || (AutomaticAdjustmentsDisabled && commissioningSession is null)) return;
        var session = Volatile.Read(ref commissioningSession);
        if (session?.Device is { } commissioningDevice)
        {
            await BestEffortWriteAsync(commissioningDevice, 100, cancellationToken)
                .ConfigureAwait(false);
        }
        else if (Volatile.Read(ref device) is { } activeDevice &&
                 Volatile.Read(ref lastDeviceState) is not null)
        {
            await BestEffortWriteAsync(activeDevice, 100, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task BestEffortWriteAsync(
        IV12Device target,
        byte percent,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await WriteFanSpeedAsync(target, percent, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A best-effort safety action must not hide the original failure.
        }
    }

    private async Task<IReadOnlyList<DeviceIdentity>> FindCandidateIdentitiesAsync(
        CancellationToken cancellationToken)
    {
        var identities = await RunHidOperationAsync(
            token => deviceLocator.EnumerateAsync(token), HidTimeout, cancellationToken,
            timeoutPoisonResource: "device enumeration").ConfigureAwait(false);
        return FindV12Candidates(identities);
    }

    private static IReadOnlyList<DeviceIdentity> FindV12Candidates(IEnumerable<DeviceIdentity> identities) =>
        identities.Where(static identity =>
            identity.VendorId == DeviceAllowList.V12UltraVendorId &&
            identity.ProductId == DeviceAllowList.V12UltraProductId).ToArray();

    private static string ClassifyDeviceStateCacheFailure(Exception exception) => exception switch
    {
        TimeoutException or OperationCanceledException => "CACHE_TIMEOUT",
        UnauthorizedAccessException => "CACHE_ACCESS_DENIED",
        InvalidDataException or JsonException => "CACHE_INVALID_DATA",
        IOException => "CACHE_IO_ERROR",
        _ => "CACHE_INTERNAL_ERROR",
    };

    private async Task<CommissioningSession> RequireSessionAsync(
        Guid sessionId,
        PipePeerIdentity peer,
        CommissioningStage requiredStage)
    {
        var session = commissioningSession;
        if (session is null || session.Id != sessionId)
        {
            throw Invalid("The commissioning session is missing or does not match.");
        }

        if (!string.Equals(session.OwnerSid, peer.CallerSid, StringComparison.OrdinalIgnoreCase))
        {
            throw new ControllerOperationException(PipeErrorCodes.InvalidArgument,
                "The commissioning session belongs to a different Windows identity.");
        }

        if (session.ExpiresAtUtc <= timeProvider.GetUtcNow())
        {
            var issue = new CommissioningSafetyIssue(
                FailSafeReason.NotCommissioned,
                PipeErrorCodes.InvalidArgument,
                "The commissioning session expired; start again with commission probe.",
                "CommissioningExpired");
            await AbortCommissioningForSafetyAsync(session, issue, CancellationToken.None)
                .ConfigureAwait(false);
            throw Invalid("The commissioning session expired; start again with commission probe.");
        }

        var conflict = conflictDetector.Inspect();
        if (conflict.HasConflict)
        {
            var issue = new CommissioningSafetyIssue(
                FailSafeReason.MythCoolConflict,
                PipeErrorCodes.MythCoolConflict,
                FormatMythCoolConflictMessage(
                    conflict,
                    $"Myth.Cool is running: {string.Join(", ", conflict.Processes)}."),
                "CommissioningAbortedMythCoolConflict");
            await AbortCommissioningForSafetyAsync(session, issue, CancellationToken.None)
                .ConfigureAwait(false);
            throw new ControllerOperationException(issue.ErrorCode, issue.Message);
        }

        if (session.Stage != requiredStage)
        {
            throw Invalid(
                $"The commissioning session is at {session.Stage}; expected {requiredStage}.");
        }

        return session;
    }

    private async Task ExpireCommissioningSessionAsync()
    {
        if (commissioningSession is { } session && session.ExpiresAtUtc <= timeProvider.GetUtcNow())
        {
            await AbortCommissioningForSafetyAsync(
                session,
                new CommissioningSafetyIssue(
                    FailSafeReason.NotCommissioned,
                    PipeErrorCodes.InvalidArgument,
                    "The commissioning session expired; start again with commission probe.",
                    "CommissioningExpired"),
                CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task<bool> DisposeCommissioningSessionAsync(CancellationToken cancellationToken = default)
    {
        if (commissioningSession is null)
        {
            return true;
        }

        var session = commissioningSession;
        if (IsHardwarePoisoned)
        {
            return false;
        }

        if (session.Device is { } sessionDevice && !ReferenceEquals(device, sessionDevice) &&
            !await DisposeHardwareResourceAsync(
                sessionDevice,
                "commissioning device",
                cancellationToken,
                HidTimeout,
                poisonOnFailure: true).ConfigureAwait(false))
        {
            return false;
        }

        commissioningSession = null;
        if (device is null)
        {
            writerLease.Release();
        }

        return true;
    }

    private async Task<bool> ReleaseDeviceAsync(
        bool releaseWriterLease = true,
        CancellationToken cancellationToken = default)
    {
        ForgetFanLightingRestore();
        manualPower.RequestOn();
        var current = device;
        if (current is null)
        {
            if (IsHardwarePoisoned)
            {
                return false;
            }

            if (releaseWriterLease && commissioningSession is null)
            {
                writerLease.Release();
            }

            return true;
        }

        if (IsHardwarePoisoned ||
            !await DisposeHardwareResourceAsync(
                current,
                "active device",
                cancellationToken,
                HidTimeout,
                poisonOnFailure: true).ConfigureAwait(false))
        {
            return false;
        }

        device = null;
        lastDeviceState = null;
        _ = Interlocked.Exchange(ref shutdownQualificationPending, null);
        if (releaseWriterLease && commissioningSession is null)
        {
            writerLease.Release();
        }

        return true;
    }

    private async Task<bool> ReleaseDeviceAfterConflictSafetyAsync(
        CancellationToken cancellationToken)
    {
        var target = device;
        if (target is null)
        {
            return await ReleaseDeviceAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        var lightingBaseline = lastDeviceState;
        try
        {
            if (lightingBaseline is null)
            {
                throw new InvalidDataException(
                    "The active V12 Ultra has no validated state for a conflict handoff safety write.");
            }

            await WriteFanSpeedAsync(target, 100, cancellationToken).ConfigureAwait(false);
            var readback = await ReadDeviceStateAsync(target, cancellationToken).ConfigureAwait(false);
            if (!readback.PoweredOn || readback.FanSpeedPercent != 100 ||
                !LightingMatches(lightingBaseline, readback))
            {
                throw new V12ProtocolException(
                    "The V12 Ultra did not confirm ON at 100% with lighting preserved before conflict handoff.");
            }

            var confirmedAt = timeProvider.GetUtcNow();
            var confirmedAtMonotonic = GetMonotonicNow();
            lastDeviceState = readback;
            lastSuccessfulRead = confirmedAt;
            lastSuccessfulWrite = confirmedAt;
            lastSuccessfulWriteMonotonic = confirmedAtMonotonic;
            lastWrittenPercent = 100;
            policy.RecordWriteSuccess(confirmedAtMonotonic, 100);
        }
        catch (SafetyOutputSuppressedException) { throw; }
        catch (Exception exception)
        {
            if (!IsHardwarePoisoned)
            {
                MarkHardwarePoisoned(
                    "CONFLICT_HANDOFF_UNCONFIRMED",
                    "active device conflict handoff",
                    exception);
            }

            return false;
        }

        return await ReleaseDeviceAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> DisposeHardwareResourceAsync(
        IAsyncDisposable resource,
        string description,
        CancellationToken cancellationToken = default,
        TimeSpan? timeout = null,
        bool poisonOnFailure = false)
    {
        if (poisonOnFailure && IsHardwarePoisoned)
        {
            return false;
        }

        try
        {
            await RunHidOperationAsync(async token =>
            {
                token.ThrowIfCancellationRequested();
                await resource.DisposeAsync().ConfigureAwait(false);
                return true;
            }, timeout ?? HidTimeout, cancellationToken, allowWhenProtectionDisabled: true).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception)
        {
            logger.LogCritical(exception,
                "The {HardwareResource} did not dispose within its hard safety deadline.", description);
            if (poisonOnFailure)
            {
                MarkHardwarePoisoned(
                    exception is TimeoutException or OperationCanceledException
                        ? "DEVICE_DISPOSAL_TIMEOUT"
                        : "DEVICE_DISPOSAL_FAILURE",
                    description,
                    exception);
            }
            else
            {
                Environment.ExitCode = 7;
            }

            return false;
        }
    }

    private async Task DisposeThermalSourceAsync(CancellationToken cancellationToken)
    {
        try
        {
            await RunThermalOperationAsync(async token =>
            {
                token.ThrowIfCancellationRequested();
                await thermalSource.DisposeAsync().ConfigureAwait(false);
                return true;
            }, HidTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            logger.LogCritical(exception,
                "The thermal source did not dispose within its hard safety deadline.");
            Environment.ExitCode = 7;
        }
    }

    private void LatchRollbackRecoveryRequired(TakeoverTransitionStage? recoveryStage)
    {
        configuration = configuration with { Commissioned = false };
        runtimeState = ServiceRuntimeState.Initial;
        configurationError = recoveryStage switch
        {
            TakeoverTransitionStage.OutcomeResolved or TakeoverTransitionStage.AuditAppended =>
                "Rollback reached and verified its terminal controller/task state, but durable audit " +
                "publication or exact journal cleanup was interrupted. The retained terminal journal " +
                "is the recovery proof; a new service process may only finish the same idempotent audit.",
            TakeoverTransitionStage.TaskRestored =>
                "Rollback restored and verified the Myth.Cool task, but final journal cleanup was " +
                "interrupted. The retained TaskRestored journal may be verified read-only by a new " +
                "service process; no task mutation will be replayed.",
            TakeoverTransitionStage.TaskRestoreInProgress =>
                "Rollback released controller hardware ownership, but scheduled-task restoration " +
                "did not reach a durable confirmed state. The task state is unknown and the retained " +
                "journal requires explicit attended recovery; restarting the service does not clear it.",
            _ =>
                "Rollback released controller hardware ownership before any scheduled-task mutation " +
                "barrier was written. A new service process may safely resume restoration from the " +
                "retained ControllerStateDecommissioned journal.",
        };
        hardwareAccessBlocked = true;
        requiresProcessRestartForRecovery = true;
        Environment.ExitCode = 7;
        lastDeviceState = null;
        policy.Reset();
        UpdateStatus("RollbackRecoveryRequired", FailSafeReason.ConfigurationInvalid);
    }

    private static ControllerStatus ProjectHardwarePoison(
        ControllerStatus current,
        HardwarePoisonState poison) =>
        current with
        {
            ServiceState = "HardwarePoisoned",
            Device = current.Device with
            {
                Connected = false,
                ProtocolCompatible = false,
            },
            Fan = current.Fan with { RequestedPercent = 100 },
            FailSafe = new FailSafeStatus(true, FailSafeReason.HidFailure, poison.Since),
            HardwareSafety = new HardwareSafetyStatus(true, poison.Reason, poison.Since),
            Shutdown = current.Shutdown is null
                ? null
                : current.Shutdown with { MatchesCurrentHandle = false },
        };

    private void UpdateStatus(
        string serviceState,
        FailSafeReason? forcedReason = null,
        DeviceIdentity? explicitIdentity = null)
    {
        var poison = Volatile.Read(ref hardwarePoison);
        var unsavedManualObservation = observedManualControlHold && observedManualPersistenceUnconfirmed &&
            configuration.Mode != ControlMode.Released;
        if (poison is not null)
        {
            serviceState = "HardwarePoisoned";
            forcedReason = FailSafeReason.HidFailure;
        }
        else if (unsavedManualObservation)
        {
            // The runtime has stopped following the previous curve, while the separate
            // ConfiguredManualPercent below remains the last durably confirmed setting.
            serviceState = "ManualObservationSaveUnconfirmed";
        }
        else if (manualCommandUnconfirmed && configuration.Mode != ControlMode.Released &&
            forcedReason is null && lastDecision?.FailSafeActive != true)
            serviceState = "ManualCommandUnconfirmed";

        var identity = explicitIdentity ?? device?.Identity ?? commissioningSession?.Device?.Identity ??
            commissioningSession?.ProbedIdentity;
        var decision = lastDecision;
        var reason = forcedReason ?? decision?.FailSafeReason ??
            (configuration.Commissioned ? FailSafeReason.StartupWarmup : FailSafeReason.NotCommissioned);
        var failSafeActive = forcedReason is not null || decision?.FailSafeActive != false;
        DateTimeOffset? lastUpdated = lastThermal.CapturedAt == DateTimeOffset.MinValue
            ? null
            : lastThermal.CapturedAt;
        var thermalAge = lastUpdated is null
            ? TimeSpan.MaxValue
            : timeProvider.GetUtcNow() - lastUpdated.Value;
        var thermalFresh = thermalAge >= TimeSpan.Zero && thermalAge <= TimeSpan.FromSeconds(10);

        var next = new ControllerStatus(
            serviceState,
            configuration.Commissioned,
            configuration.Mode == ControlMode.Released || runtimeState.OwnerSid is null ? "none" : "configured-user",
            new DeviceStatus(
                poison is null && identity is not null,
                $"{(identity?.VendorId ?? DeviceAllowList.V12UltraVendorId):X4}",
                $"{(identity?.ProductId ?? DeviceAllowList.V12UltraProductId):X4}",
                $"{(identity?.Version ?? 0):X4}",
                identity?.IsSupportedV12Ultra == true &&
                forcedReason != FailSafeReason.UnsupportedDevice &&
                poison is null),
            new FanStatus(
                unsavedManualObservation && configuration.Mode != ControlMode.PureManual
                    ? ControlMode.Manual : configuration.Mode,
                unsavedManualObservation && observedManualSpeedPercent is { } observedPercent
                    ? observedPercent
                    : forcedReason is not null
                    ? (byte)100
                    : decision?.RequestedPercent ??
                      (configuration.Commissioned ? (byte)100 : configuration.ManualSpeedPercent),
                lastDeviceState?.FanSpeedPercent,
                lastDeviceState?.PoweredOn,
                configuration.ManualSpeedPercent,
                ManualPowerSupported: true,
                OffRequested: manualPower.OffRequested),
            new ThermalStatus(
                lastThermal.CpuCelsius,
                lastThermal.ValidGpuCelsius,
                lastThermal.ControlCelsius,
                lastThermal.CpuIsValid && thermalFresh,
                lastUpdated),
            new FailSafeStatus(
                failSafeActive,
                reason,
                failSafeActive ? poison?.Since ?? decision?.FailSafeSince ?? timeProvider.GetUtcNow() : null),
            lastSuccessfulRead,
            lastSuccessfulWrite,
            new HardwareSafetyStatus(
                poison is not null,
                poison?.Reason,
                poison?.Since),
            new CommissioningStatus(
                commissioningSession is not null,
                commissioningSession?.Id.ToString("D"),
                commissioningSession?.Stage.ToString(),
                commissioningSession?.ExpiresAtUtc),
            CreateShutdownStatus(),
            CreateAuditMutationStatus(),
            CreateForcedUninstallRecoveryStatus(),
            CreateSafetyOperationAuditStatus(),
            new SoftwareStatus(typeof(ControllerCoordinator).Assembly.GetName().Version?.ToString() ?? "unknown",
                UiCompatibility.ContractVersion, true, ProfileApplySupported: true, SafetyDisableSupported: true,
                ManualActionsWhileProtectionDisabledSupported: true, FanLightingLinkSupported: true),
            profileApplication, ReadSafetyProtection());
        Volatile.Write(ref status, next);
    }

    private static ControllerStatus CreateInitialStatus() => new(
        "Initializing",
        false,
        "none",
        new DeviceStatus(false, "374A", "B101", "0000", false),
        new FanStatus(ControlMode.Auto, 100, null, null, 100),
        new ThermalStatus(null, [], null, false, null),
        new FailSafeStatus(true, FailSafeReason.NotCommissioned, DateTimeOffset.UtcNow),
        null,
        null,
        new HardwareSafetyStatus(false, null, null),
        new CommissioningStatus(false, null, null, null),
        new ShutdownStatus(
            false,
            false,
            "keepAt100",
            null,
            null,
            false),
        new AuditMutationStatus(false, false, null, null, null, null),
        new ForcedUninstallRecoveryStatus(false, true, null, null, null),
        new SafetyOperationAuditStatus(
            false,
            false,
            null,
            null,
            null,
            null,
            null,
            false,
            null));

    private static object PublicDeviceState(V12DeviceState state) => new
    {
        state.FanSpeedPercent,
        state.PoweredOn,
        lightingPreserved = true,
    };

    private static bool LightingMatches(V12DeviceState left, V12DeviceState right) =>
        left.LightModeAndPower == right.LightModeAndPower &&
        left.LightColorMode == right.LightColorMode &&
        left.LightSpeed == right.LightSpeed &&
        left.LightPower == right.LightPower;

    private ShutdownStatus CreateShutdownStatus()
    {
        var qualification = configuration.ShutdownOffQualification;
        var pending = GetActiveShutdownQualificationPending();
        var enabled = configuration.SystemShutdownFanBehavior ==
                      SystemShutdownFanBehavior.TurnOffOnSystemShutdown &&
                      qualification is not null;
        var currentIdentity = device?.Identity;
        return new ShutdownStatus(
            enabled,
            qualification is not null,
            enabled ? "turnOffOnSystemShutdown" : "keepAt100",
            qualification is null
                ? null
                : new ShutdownQualificationIdentityStatus(
                    $"{qualification.VendorId:X4}",
                    $"{qualification.ProductId:X4}",
                    $"{qualification.FirmwareVersion:X4}",
                    $"{qualification.UsagePage:X4}",
                    $"{qualification.Usage:X4}",
                    qualification.FeatureReportLength,
                    qualification.ProtocolRevision),
            qualification?.QualifiedAtUtc,
            qualification is not null &&
            currentIdentity is not null &&
            !IsHardwarePoisoned &&
            configuration.Commissioned &&
            runtimeState.Commissioned &&
            !string.IsNullOrWhiteSpace(runtimeState.OwnerSid) &&
            writerLease.IsHeld &&
            currentIdentity.IsSupportedV12Ultra &&
            DeviceAllowList.IsSupported(currentIdentity) &&
            QualificationMatchesIdentity(qualification, currentIdentity),
            pending is not null,
            pending?.Id.ToString("D"),
            pending?.ExpiresAtUtc);
    }

    private AuditMutationStatus CreateAuditMutationStatus()
    {
        var pending = activeAuditMutation;
        return new AuditMutationStatus(
            auditMutationBlocked,
            pending is not null,
            pending?.TransactionId.ToString("D"),
            pending?.EventName,
            pending?.Stage.ToString(),
            auditMutationError);
    }

    private SafetyOperationAuditStatus CreateSafetyOperationAuditStatus()
    {
        var pending = activeSafetyOperationAudit;
        return new SafetyOperationAuditStatus(
            safetyOperationAuditBlocked,
            pending is not null,
            pending?.TransactionId.ToString("D"),
            pending?.EventName,
            pending?.Kind.ToString(),
            pending?.Stage.ToString(),
            pending?.Outcome.ToString(),
            pending?.RequiresOn100Recovery == true,
            safetyOperationAuditError);
    }

    private ForcedUninstallRecoveryStatus CreateForcedUninstallRecoveryStatus()
    {
        var marker = forcedUninstallRecoveryMarker;
        return new ForcedUninstallRecoveryStatus(
            HasForcedUninstallRecoveryBarrier,
            marker is not null && forcedUninstallRecoveryMarkerError is null,
            marker?.MarkerId.ToString("D"),
            forcedUninstallRecoveryMarkerError ??
            (marker is null
                ? null
                : "A forced uninstall skipped verified Myth.Cool rollback. " +
                  "Normal hardware access is blocked until explicit rollback recovery succeeds."),
            marker?.CreatedAtUtc);
    }

    private bool HasForcedUninstallRecoveryBarrier =>
        forcedUninstallRecoveryMarker is not null ||
        forcedUninstallRecoveryMarkerError is not null;

    private static string FormatIdentity(DeviceIdentity identity) =>
        $"VID_{identity.VendorId:X4}/PID_{identity.ProductId:X4}/FW_{identity.Version:X4}/" +
        $"UP_{identity.UsagePage:X4}/U_{identity.Usage:X4}";

    private static string FormatQualificationIdentity(SystemShutdownOffQualification qualification) =>
        $"VID_{qualification.VendorId:X4}/PID_{qualification.ProductId:X4}/" +
        $"FW_{qualification.FirmwareVersion:X4}/UP_{qualification.UsagePage:X4}/" +
        $"U_{qualification.Usage:X4}/FL_{qualification.FeatureReportLength}";

    private void UpdateStatusForOpenFailure(
        V12DeviceOpenStatus openStatus,
        DeviceIdentity expectedIdentity)
    {
        switch (openStatus)
        {
            case V12DeviceOpenStatus.DeviceNotFound:
                deviceWasMissing = everOpened;
                UpdateStatus("DeviceUnavailable", FailSafeReason.DeviceUnavailable, expectedIdentity);
                break;
            case V12DeviceOpenStatus.MultipleCandidates:
                UpdateStatus("DuplicateDevices", FailSafeReason.UnsupportedDevice, expectedIdentity);
                break;
            case V12DeviceOpenStatus.ExpectedIdentityUnsupported:
            case V12DeviceOpenStatus.DescriptorIdentityMismatch:
            case V12DeviceOpenStatus.HandleIdentityMismatch:
                UpdateStatus("UnsupportedDevice", FailSafeReason.UnsupportedDevice, expectedIdentity);
                break;
            case V12DeviceOpenStatus.DeviceBusy:
                UpdateStatus("DeviceBusy", FailSafeReason.HidFailure, expectedIdentity);
                break;
            case V12DeviceOpenStatus.HandleValidationFailed:
                UpdateStatus("HandleValidationFailed", FailSafeReason.HidFailure, expectedIdentity);
                break;
            case V12DeviceOpenStatus.Opened:
            default:
                throw new InvalidOperationException(
                    $"Open status {openStatus} cannot be handled as an open failure.");
        }
    }

    private static ControllerOperationException CreateOpenFailureException(
        V12DeviceOpenStatus openStatus) => openStatus switch
        {
            V12DeviceOpenStatus.DeviceNotFound => new ControllerOperationException(
                PipeErrorCodes.DeviceNotFound,
                "The V12 Ultra disappeared before exclusive open."),
            V12DeviceOpenStatus.MultipleCandidates => new ControllerOperationException(
                PipeErrorCodes.UnsupportedDevice,
                "The V12 Ultra interface family is no longer unique."),
            V12DeviceOpenStatus.ExpectedIdentityUnsupported => new ControllerOperationException(
                PipeErrorCodes.UnsupportedDevice,
                "The expected V12 Ultra identity is not supported."),
            V12DeviceOpenStatus.DescriptorIdentityMismatch => new ControllerOperationException(
                PipeErrorCodes.UnsupportedDevice,
                "The V12 Ultra identity changed before exclusive open."),
            V12DeviceOpenStatus.DeviceBusy => new ControllerOperationException(
                PipeErrorCodes.HidTimeout,
                "The expected V12 Ultra could not be opened exclusively."),
            V12DeviceOpenStatus.HandleValidationFailed => new ControllerOperationException(
                PipeErrorCodes.HidTimeout,
                "The opened V12 Ultra handle could not be validated."),
            V12DeviceOpenStatus.HandleIdentityMismatch => new ControllerOperationException(
                PipeErrorCodes.UnsupportedDevice,
                "The opened HID handle does not match the expected V12 Ultra identity."),
            _ => new ControllerOperationException(
                PipeErrorCodes.InternalError,
                "The V12 Ultra open operation returned an invalid result."),
        };

    private static CommissioningSafetyIssue CreateCommissioningFailureIssue(
        Exception exception,
        string stage)
    {
        var (reason, errorCode) = exception switch
        {
            ControllerOperationException { Code: PipeErrorCodes.MythCoolConflict } operation =>
                (FailSafeReason.MythCoolConflict, operation.Code),
            ControllerOperationException { Code: PipeErrorCodes.TelemetryStale } operation =>
                (FailSafeReason.TelemetryStale, operation.Code),
            ControllerOperationException { Code: PipeErrorCodes.FailSafeActive } operation =>
                (FailSafeReason.EmergencyTemperature, operation.Code),
            ControllerOperationException { Code: PipeErrorCodes.UnsupportedDevice } operation =>
                (FailSafeReason.UnsupportedDevice, operation.Code),
            ControllerOperationException operation => (FailSafeReason.HidFailure, operation.Code),
            _ => (FailSafeReason.HidFailure, PipeErrorCodes.HidTimeout),
        };

        return new CommissioningSafetyIssue(
            reason,
            errorCode,
            $"The commissioning {stage} stage failed; commissioning must restart.",
            $"CommissioningAborted{stage}Failure");
    }

    private static bool IsHardwareFailure(Exception exception) =>
        exception is IOException or Win32Exception or TimeoutException or UnauthorizedAccessException or
            V12ProtocolException;

    private async Task<T> RunHidOperationAsync<T>(
        Func<CancellationToken, Task<T>> action,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        string? timeoutPoisonResource = null,
        IV12Device? timeoutRecoveryTarget = null,
        bool allowWhenProtectionDisabled = false)
    {
        long epoch;
        CancellationToken automaticCancellation;
        lock (safetyProtectionSync)
        {
            epoch = safetyProtectionEpoch;
            automaticCancellation = automaticOutputLifetime.Token;
        }
        var explicitCommand = explicitManualIo.Value;
        var manualRevision = manualOperationRevision.Value;
        var explicitCancellation = explicitCommand ? explicitSequenceCancellation.Value : null;
        // A profile's cancellation stops device pre-send waits but never abandons an
        // already-issued native call while releasing the coordinator gate.
        var dispatcherCancellation = explicitCancellation is not null ? CancellationToken.None : cancellationToken;
        if (!allowWhenProtectionDisabled) ThrowIfAutomaticOutputStopped(epoch);
        if (IsHardwarePoisoned)
        {
            throw new IOException("Hardware access is poisoned until the service process restarts.");
        }

        MarkHeartbeat();
        try
        {
            async Task<T> GuardedAction(CancellationToken token)
            {
                explicitManualIo.Value = explicitCommand;
                manualOperationRevision.Value = manualRevision;
                automaticIoEpoch.Value = epoch;
                automaticIoCancellation.Value = !allowWhenProtectionDisabled && !explicitCommand ? automaticCancellation : null;
                explicitSequenceCancellation.Value = explicitCancellation;
                try
                {
                    if (!allowWhenProtectionDisabled) ThrowIfAutomaticOutputStopped(epoch);
                    // Cancel pre-send waits, while the dispatcher still waits for an issued native call.
                    using var stopped = explicitCancellation is { } explicitToken
                        ? CancellationTokenSource.CreateLinkedTokenSource(token, explicitToken)
                        : !allowWhenProtectionDisabled && !explicitCommand
                            ? CancellationTokenSource.CreateLinkedTokenSource(token, automaticCancellation) : null;
                    return await action(stopped?.Token ?? token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!allowWhenProtectionDisabled && !explicitCommand &&
                    (SafetyProtectionDisabled || epoch != Interlocked.Read(ref safetyProtectionEpoch)))
                { throw new SafetyOutputSuppressedException(); }
                catch (OperationCanceledException) when (explicitCancellation?.IsCancellationRequested == true)
                { throw new SafetyOutputSuppressedException(); }
                finally { explicitManualIo.Value = false; manualOperationRevision.Value = null; automaticIoEpoch.Value = null; automaticIoCancellation.Value = null; explicitSequenceCancellation.Value = null; }
            }
            return timeoutRecoveryTarget is null
                ? await hidDispatcher.InvokeAsync(GuardedAction, timeout, dispatcherCancellation).ConfigureAwait(false)
                : await hidDispatcher.InvokeWithTimeoutRecoveryAsync(
                        GuardedAction,
                        _ => WriteAttachedRecoveryAtOneHundredAsync(timeoutRecoveryTarget, epoch, automaticCancellation),
                        timeout,
                        dispatcherCancellation)
                    .ConfigureAwait(false);
        }
        catch (TimeoutException exception) when (timeoutPoisonResource is not null)
        {
            // The operation may still own a synchronous HidD call after its caller deadline.
            // Its work item has already attached a non-cancellable ON-at-100 recovery when a
            // trusted device is available. Retain every handle and the writer lease until the
            // watchdog terminates this process; never continue control or dispose the possibly
            // active handle in this process.
            MarkHardwarePoisoned("HID_OPERATION_TIMEOUT", timeoutPoisonResource, exception);
            throw;
        }
        finally
        {
            MarkHeartbeat();
        }
    }

    private async Task<T> RunThermalOperationAsync<T>(
        Func<CancellationToken, Task<T>> action,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        MarkHeartbeat();
        try
        {
            return await thermalDispatcher.InvokeAsync(action, timeout, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            MarkHeartbeat();
        }
    }

    private async Task WriteAttachedRecoveryAtOneHundredAsync(IV12Device target, long epoch, CancellationToken automaticCancellation)
    {
        if (SafetyProtectionDisabled || (AutomaticAdjustmentsDisabled && commissioningSession is null)) return;
        if (epoch != Interlocked.Read(ref safetyProtectionEpoch)) return;
        using var recoveryDeadline = CancellationTokenSource.CreateLinkedTokenSource(automaticCancellation);
        recoveryDeadline.CancelAfter(HidTimeout);
        MarkHeartbeat();
        try
        {
            ThrowIfAutomaticOutputStopped();
            await target.WriteFanSpeedAsync(100, recoveryDeadline.Token).ConfigureAwait(false);
        }
        finally
        {
            MarkHeartbeat();
        }
    }

    private async Task<V12DeviceOpenResult> OpenExpectedDeviceAsync(
        DeviceIdentity expectedIdentity,
        CancellationToken cancellationToken,
        bool allowWhenProtectionDisabled = false)
    {
        // Register before dispatch. If WaitAsync loses a completion-vs-timeout race, the
        // hardware thread captures any returned device before publishing the result, while
        // the abandoned holder remains a process-lifetime strong root. Normal handoff removes
        // the holder only after this caller has received its own strong result reference.
        var retentionHolder = AbandonedOpenResourceRoot.Register(abandonedOpenRetentionScope);
        try
        {
            var result = await RunHidOperationAsync(
                    async token =>
                    {
                        var openResult = await deviceLocator.OpenExpectedAsync(expectedIdentity, token)
                            .ConfigureAwait(false);
                        retentionHolder.Capture(openResult);
                        return openResult;
                    },
                    HidTimeout,
                    cancellationToken,
                    allowWhenProtectionDisabled: allowWhenProtectionDisabled)
                .ConfigureAwait(false);
            AbandonedOpenResourceRoot.ReleaseAfterSuccessfulHandoff(retentionHolder);
            return result;
        }
        catch (SafetyOutputSuppressedException)
        {
            // The guard rejected this work, or the locator finished its cancellation cleanup.
            // A native open that has not returned still takes the real timeout branch below.
            AbandonedOpenResourceRoot.ReleaseAfterSuccessfulHandoff(retentionHolder);
            throw;
        }
        catch (Exception exception) when (exception is TimeoutException or OperationCanceledException)
        {
            // A cancellation-ignoring native open may still complete after its caller has
            // abandoned the result. Retain the writer lease and terminate this process so a
            // late exclusive handle can never become detached from controller ownership.
            MarkHardwarePoisoned("DEVICE_OPEN_ABANDONED", "exclusive device open", exception);
            throw;
        }
    }

    private void ThrowIfHardwarePoisoned()
    {
        if (IsHardwarePoisoned)
        {
            throw new ControllerOperationException(
                PipeErrorCodes.HidTimeout,
                "Hardware access is blocked because device ownership could not be released safely; " +
                "the service process must restart.");
        }
    }

    private void ThrowIfHardwareAccessBlocked()
    {
        if (hardwareAccessBlocked || invalidConfigurationWithPendingJournal ||
            requiresProcessRestartForRecovery)
        {
            throw new ControllerOperationException(
                PipeErrorCodes.InternalError,
                safetyOperationAuditBlocked
                    ? "Hardware access is blocked by an unresolved durable safety-operation audit. " +
                      "Inspect status and diagnostics; do not delete its recovery barrier."
                    : auditMutationBlocked
                    ? "Hardware access and shutdown fan-off are blocked by an unresolved durable " +
                      "audit mutation. Inspect status and diagnostics; do not delete its recovery barrier."
                    : "Hardware access is blocked by a retained takeover transition journal. Restarting " +
                      "alone may not resolve an in-progress task mutation; inspect diagnostics and perform " +
                      "explicit attended recovery.");
        }
    }

    private void MarkHardwarePoisoned(string reason, string resourceDescription, Exception exception)
    {
        _ = Interlocked.Exchange(ref shutdownQualificationPending, null);
        var poison = new HardwarePoisonState(reason, timeProvider.GetUtcNow());
        _ = Interlocked.CompareExchange(ref hardwarePoison, poison, null);
        Environment.ExitCode = 7;
        logger.LogCritical(
            exception,
            "Hardware ownership became uncertain while handling {HardwareResource} and could not be " +
            "proven safely released. No new HID operation, open, or disposal may be scheduled in " +
            "this process; only an already-running terminal work item may finish its same-thread " +
            "ON+100 compensation after a late native return.",
            resourceDescription);
        UpdateStatus("HardwarePoisoned", FailSafeReason.HidFailure);
    }

    private static void RequireAttended(bool attended)
    {
        if (!attended)
        {
            throw Invalid("This commissioning operation requires attended=true.");
        }
    }

    private static void RequireAttended(bool attended, string operation)
    {
        if (!attended)
        {
            throw Invalid($"{operation} requires attended=true.");
        }
    }

    private static ControllerOperationException Invalid(string message, Exception? inner = null) =>
        new(PipeErrorCodes.InvalidArgument, message, inner);

    private void EnsureInitialized()
    {
        if (!initialized)
        {
            throw new ControllerOperationException(PipeErrorCodes.ServiceUnavailable,
                "The controller service is still initializing.");
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(
        Volatile.Read(ref disposeRequested) != 0, this);

    private TimeSpan GetMonotonicNow() =>
        timeProvider.GetElapsedTime(monotonicOriginTimestamp, timeProvider.GetTimestamp());

    private void MarkHeartbeat()
    {
        Interlocked.Exchange(ref lastHeartbeatUtcTicks, timeProvider.GetUtcNow().UtcTicks);
        Interlocked.Exchange(ref lastHeartbeatTimestamp, timeProvider.GetTimestamp());
    }

    private sealed record PendingDeviceStateCacheWrite(
        DeviceIdentity Identity,
        V12DeviceState State,
        DateTimeOffset CapturedAtUtc);

    private sealed record DeviceStateCacheSnapshot(
        DeviceIdentity Identity,
        V12DeviceState State);

    private sealed record CommissioningSafetyIssue(
        FailSafeReason Reason,
        string ErrorCode,
        string Message,
        string ServiceState);

    private sealed record CommissioningFailSafeAttempt(
        byte RequestedPercent,
        bool WriteAttempted,
        bool WriteSucceeded,
        bool ReadbackConfirmed)
    {
        public static CommissioningFailSafeAttempt NotAttempted { get; } =
            new(100, WriteAttempted: false, WriteSucceeded: false, ReadbackConfirmed: false);
    }

    private sealed record CommissioningCleanupResult(
        CommissioningFailSafeAttempt FailSafe,
        bool SessionReleased);

    private sealed record HostStopHidTransactionResult(
        bool Confirmed,
        V12DeviceState? Readback,
        Exception? Failure,
        bool RecoveryWriteAttempted,
        bool RecoveryWriteSucceeded)
    {
        public static HostStopHidTransactionResult Success(V12DeviceState readback) =>
            new(true, readback, null, false, false);

        public static HostStopHidTransactionResult Failed(
            Exception failure,
            bool recoveryWriteAttempted,
            bool recoveryWriteSucceeded) =>
            new(false, null, failure, recoveryWriteAttempted, recoveryWriteSucceeded);
    }

    private sealed record HardwarePoisonState(string Reason, DateTimeOffset Since);

    private sealed record ShutdownQualificationPending(
        Guid Id,
        string CallerSid,
        string RuntimeOwnerSid,
        IV12Device Target,
        DeviceIdentity Identity,
        ControllerConfiguration Configuration,
        DateTimeOffset QualifiedAtUtc,
        DateTimeOffset ExpiresAtUtc,
        TimeSpan ExpiresAtMonotonic);

    private sealed record ShutdownQualificationTransactionResult(
        bool Qualified,
        bool FinalOnAtOneHundredConfirmed,
        V12DeviceState? FinalReadback,
        Exception? Failure)
    {
        public static ShutdownQualificationTransactionResult Success(V12DeviceState finalReadback) =>
            new(true, true, finalReadback, null);

        public static ShutdownQualificationTransactionResult Failed(
            Exception failure,
            bool finalOnAtOneHundredConfirmed,
            V12DeviceState? finalReadback) =>
            new(false, finalOnAtOneHundredConfirmed, finalReadback, failure);
    }

    private sealed class ShutdownQualificationCancellationGate : IDisposable
    {
        private readonly CancellationTokenSource preOffCancellation = new();
        private readonly CancellationTokenRegistration callerRegistration;
        private int state;

        public ShutdownQualificationCancellationGate(CancellationToken callerCancellationToken)
        {
            callerRegistration = callerCancellationToken.UnsafeRegister(
                static gate => ((ShutdownQualificationCancellationGate)gate!).CancelBeforeOff(),
                this);
        }

        public CancellationToken PreOffCancellationToken => preOffCancellation.Token;

        public bool TryEnterOff() => Interlocked.CompareExchange(ref state, 1, 0) == 0;

        private void CancelBeforeOff()
        {
            if (Interlocked.CompareExchange(ref state, 2, 0) == 0)
            {
                preOffCancellation.Cancel();
            }
        }

        public void Dispose()
        {
            callerRegistration.Dispose();
            preOffCancellation.Dispose();
        }
    }
}

internal sealed class SystemShutdownOffCommit
{
    private const int Running = 0;
    private const int Committed = 1;
    private const int Abandoned = 2;
    private V12DeviceState? committedReadback;
    private int state;

    public bool TryCommit(V12DeviceState readback)
    {
        Volatile.Write(ref committedReadback, readback);
        return Interlocked.CompareExchange(ref state, Committed, Running) == Running;
    }

    public bool TryAbandon() =>
        Interlocked.CompareExchange(ref state, Abandoned, Running) == Running;

    public bool TryGetCommittedReadback(out V12DeviceState readback)
    {
        if (Volatile.Read(ref state) == Committed &&
            Volatile.Read(ref committedReadback) is { } current)
        {
            readback = current;
            return true;
        }

        readback = null!;
        return false;
    }
}
