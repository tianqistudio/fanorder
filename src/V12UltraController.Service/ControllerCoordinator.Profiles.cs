using V12UltraController.Core.Configuration;
using V12UltraController.Core.Control;
using V12UltraController.Core.Hardware;
using V12UltraController.Core.Ipc;
using V12UltraController.Core.Status;

namespace V12UltraController.Service;

internal sealed partial class ControllerCoordinator
{
    private static readonly TimeSpan ProfileApplyTimeout = TimeSpan.FromSeconds(20);
    private PendingProfileApplication? pendingProfileApplication;
    private ProfileApplicationStatus? profileApplication;
    private bool profileLightingApplying;
    private long profileApplicationEpoch;

    public async Task<ProfileApplyResult> ApplyProfileAsync(ProfileApplyRequest request, string operationId,
        PipePeerIdentity peer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Profile);
        request.Profile.Validate();
        if (!Guid.TryParseExact(operationId, "D", out var identifier) || identifier == Guid.Empty)
            throw Invalid("方案操作需要有效的请求 ID。");
        long requestEpoch;
        CancellationToken requestEpochCancellation;
        lock (safetyProtectionSync)
        {
            requestEpoch = safetyProtectionEpoch;
            requestEpochCancellation = automaticOutputLifetime.Token;
        }

        using var deadline = new CancellationTokenSource(ProfileApplyTimeout, timeProvider);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        PendingProfileApplication pending;
        await gate.WaitAsync(lifetime.Token).ConfigureAwait(false);
        try
        {
            EnsureInitialized();
            if (requestEpoch != Interlocked.Read(ref safetyProtectionEpoch))
                throw new SafetyOutputSuppressedException();
            if (SafetyProtectionDisabled)
            {
                if (request.Profile.Mode != ControlMode.Manual)
                    throw Invalid("自动调节已暂停；自动方案可以编辑保存，恢复自动控制后才能应用。");
                return await ApplyExplicitManualProfileUnderGateAsync(request.Profile, operationId, peer,
                    requestEpoch, requestEpochCancellation, lifetime.Token).ConfigureAwait(false);
            }
            ThrowIfDisposed();
            ThrowIfHardwarePoisoned();
            ThrowIfHardwareAccessBlocked();
            if (!configuration.Commissioned || !runtimeState.Commissioned || string.IsNullOrWhiteSpace(runtimeState.OwnerSid))
                throw new ControllerOperationException(PipeErrorCodes.NotCommissioned, "完成正式接管后才能应用方案。");
            if (commissioningSession is not null || HostStopDisposition != HostStopDisposition.None)
                throw Invalid("接管流程或后台停止过程中不能应用方案。");
            if (AutomaticAdjustmentsDisabled && !request.ConfirmResumeControl)
                throw Invalid("请明确确认恢复常规温控控制后再应用方案。");
            RequireNoMythCoolConflictForShutdownQualification();
            if (request.Profile.Lighting is not null && device is not null && device is not IV12LightingDevice)
                throw new ControllerOperationException(PipeErrorCodes.UnsupportedDevice, "当前设备不支持方案中的灯光预设。");

            SupersedeProfileApplicationUnderGate("已由另一个方案取代；不会继续应用原方案的灯光。");
            var now = timeProvider.GetUtcNow();
            pending = new PendingProfileApplication(request.Profile, peer, lifetime.Token, now + ProfileApplyTimeout,
                new ProfileApplicationStatus(operationId, request.Profile.Id, request.Profile.Name,
                    ProfileApplyStage.SavingConfiguration, ProfileApplyOutcome.Applying,
                    ProfileConfigurationState.Pending, ProfileFanState.Pending,
                    request.Profile.Lighting is null ? ProfileLightingState.NotRequested : ProfileLightingState.Pending,
                    "正在保存风扇方案。", now, now));
            pendingProfileApplication = pending;
            profileApplicationEpoch = Interlocked.Read(ref safetyProtectionEpoch);
            PublishProfileApplicationUnderGate(pending);
            var next = configuration with
            {
                Mode = request.Profile.Mode,
                ManualSpeedPercent = request.Profile.ManualSpeedPercent,
                Curve = request.Profile.Curve,
            };
            try
            {
                // This is the only persistent phase. RGB is an explicit, runtime-only second phase.
                await PersistConfigurationMutationAsync(next, PipeOperations.ProfileApply, peer, lifetime.Token)
                    .ConfigureAwait(false);
                if (request.Profile.Lighting is not null || request.Profile.Mode == ControlMode.Auto)
                    ForgetFanLightingRestore();
                // A newly selected profile is a fresh explicit control choice.
                // Do not let a previous unacknowledged speed suppress its target.
                manualCommandUnconfirmed = false;
                observedManualControlHold = false;
                observedManualPersistenceUnconfirmed = false;
                observedManualSpeedPercent = null;
                observedManualPowerOff = false;
                lastObservedManualDevice = null;
                lastObservedManualDeviceBaseline = null;
                explicitControlBaselineResetPending = true;
                manualPower.RequestOn();
                policy.Reset();
                _ = Interlocked.Exchange(ref shutdownQualificationPending, null);
                pending.Status = pending.Status with
                {
                    Configuration = ProfileConfigurationState.Saved,
                    Stage = ProfileApplyStage.WaitingForFan,
                    Reason = "风扇方案已保存，正在等待温控恢复和设备回读。",
                };
                UpdateStatus("ConfigurationChanged", FailSafeReason.StartupWarmup);
                PublishProfileApplicationUnderGate(pending);
            }
            catch (Exception exception) when (!IsFatalPersistenceFailure(exception))
            {
                // A throwing save may still have persisted. The existing WAL/readback path is authoritative.
                var saved = ReferenceEquals(configuration, next);
                pending.Status = pending.Status with
                {
                    Configuration = saved ? ProfileConfigurationState.Saved :
                        auditMutationBlocked ? ProfileConfigurationState.Unknown : ProfileConfigurationState.Unchanged,
                    Fan = ProfileFanState.Unknown,
                };
                UpdateStatus(GetStatus().ServiceState);
                FinishProfileApplicationUnderGate(pending,
                    lifetime.IsCancellationRequested ? ProfileApplyOutcome.Interrupted :
                    saved || auditMutationBlocked ? ProfileApplyOutcome.Partial : ProfileApplyOutcome.Rejected,
                    saved ? "方案配置已回读，但审计未完成；已停止后续灯光操作，请查看恢复状态。" :
                    auditMutationBlocked ? "配置保存结果未确认；已停止后续操作，请查看审计恢复状态。" :
                    "方案未应用，原配置保留；请查看后台诊断。");
            }
        }
        finally { gate.Release(); }

        try
        {
            await pending.Completion.Task.WaitAsync(lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            // Never wait while owning the control gate. A cancelled request cannot strand a later RGB write.
            await gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                if (ReferenceEquals(pendingProfileApplication, pending))
                    FinishProfileApplicationUnderGate(pending, ProfileApplyOutcome.Interrupted,
                        cancellationToken.IsCancellationRequested
                            ? "方案操作已取消；已保存的风扇配置保留，未开始的灯光不会继续应用。"
                            : "方案等待已超时；已保存的风扇配置保留，未开始的灯光不会自动重试。");
            }
            finally { gate.Release(); }
        }
        return new ProfileApplyResult(pending.Status, GetStatus());
    }

    // Called by the normal loop while its gate is held. No extra control loop, samples, or recovery bypass.
    private async Task AdvanceProfileApplicationUnderGateAsync(CancellationToken tickCancellationToken)
    {
        if (pendingProfileApplication is not { } pending) return;
        // A newer explicit request is waiting for this gate. Let it supersede or
        // cancel before starting another transaction for the older profile.
        if (ManualRequestPending) return;
        if (SafetyProtectionDisabled || profileApplicationEpoch != Interlocked.Read(ref safetyProtectionEpoch) ||
            pending.CancellationToken.IsCancellationRequested || tickCancellationToken.IsCancellationRequested ||
            timeProvider.GetUtcNow() >= pending.ExpiresAt || Volatile.Read(ref disposeRequested) != 0 ||
            HostStopDisposition != HostStopDisposition.None)
        {
            FinishProfileApplicationUnderGate(pending, ProfileApplyOutcome.Interrupted,
                "方案等待已结束；保留已保存的风扇配置，灯光不会自动重放。");
            return;
        }
        if (commissioningSession is not null || configuration.Mode != pending.Profile.Mode ||
            configuration.ManualSpeedPercent != pending.Profile.ManualSpeedPercent ||
            !configuration.Curve.Points.SequenceEqual(pending.Profile.Curve.Points))
        {
            SupersedeProfileApplicationUnderGate("控制设置已改变，原方案不再继续应用灯光。");
            return;
        }
        if (IsHardwarePoisoned || hardwareAccessBlocked || configurationError is not null ||
            auditMutationBlocked || safetyOperationAuditBlocked || HasForcedUninstallRecoveryBarrier ||
            device is null || !writerLease.IsHeld)
        {
            pending.Status = pending.Status with { Fan = ProfileFanState.Unknown };
            FinishProfileApplicationUnderGate(pending, ProfileApplyOutcome.Partial,
                "风扇方案已保存，但设备或恢复状态不允许确认；灯光未应用。");
            return;
        }
        if (lastDecision is null || lastDecision.FailSafeActive)
        {
            pending.Status = pending.Status with { Fan = ProfileFanState.SafetyOverride };
            if (lastDecision?.FailSafeReason is FailSafeReason.StartupWarmup or FailSafeReason.Recovering or FailSafeReason.DeviceReconnected)
                PublishProfileApplicationUnderGate(pending);
            else
                FinishProfileApplicationUnderGate(pending, ProfileApplyOutcome.Partial,
                    "风扇方案已保存，当前由温控保护接管；灯光未应用。");
            return;
        }

        try
        {
            using var tickLifetime = CancellationTokenSource.CreateLinkedTokenSource(pending.CancellationToken, tickCancellationToken);
            var readback = await ReadDeviceStateAsync(device, tickLifetime.Token).ConfigureAwait(false);
            lastDeviceState = readback;
            lastSuccessfulRead = timeProvider.GetUtcNow();
            if (!readback.PoweredOn || readback.RawModeStatus != 0x80 || readback.FanSpeedPercent != lastDecision.RequestedPercent)
            {
                pending.Status = pending.Status with { Fan = ProfileFanState.Unknown };
                UpdateStatus("Running");
                FinishProfileApplicationUnderGate(pending, ProfileApplyOutcome.Partial,
                    "风扇方案已保存，但设备回读未确认当前目标；灯光未应用。");
                return;
            }
            pending.Status = pending.Status with { Fan = ProfileFanState.Confirmed };
            UpdateStatus("Running");
            if (pending.Profile.Lighting is { } lighting)
            {
                tickLifetime.Token.ThrowIfCancellationRequested();
                pending.Status = pending.Status with
                {
                    Stage = ProfileApplyStage.ApplyingLighting,
                    Reason = "风扇目标已回读，正在应用并验证灯光。",
                };
                PublishProfileApplicationUnderGate(pending);
                profileLightingApplying = true;
                var lightingWriteStarted = false;
                try
                {
                    await SetLightingUnderGateAsync(lighting, pending.Peer, tickLifetime.Token,
                        () => lightingWriteStarted = true).ConfigureAwait(false);
                    pending.Status = pending.Status with { Lighting = ProfileLightingState.Confirmed };
                }
                catch
                {
                    // Existing lighting compensation guarantees cooling only, never an RGB rollback.
                    pending.Status = pending.Status with
                    {
                        Lighting = lightingWriteStarted ? ProfileLightingState.Unknown : ProfileLightingState.NotAttempted,
                        Fan = IsHardwarePoisoned ? ProfileFanState.Unknown :
                            GetStatus().FailSafe.Active && lastDeviceState is { PoweredOn: true, FanSpeedPercent: 100 }
                                ? ProfileFanState.SafetyOverride : pending.Status.Fan,
                    };
                    throw;
                }
                finally { profileLightingApplying = false; }
            }
            FinishProfileApplicationUnderGate(pending, ProfileApplyOutcome.Completed,
                pending.Profile.Lighting is null ? "风扇方案已保存，当前目标已由设备回读确认。" :
                    "风扇方案和灯光均已回读确认。");
        }
        catch (Exception exception) when (!IsFatalPersistenceFailure(exception))
        {
            // The lighting helper either confirms compensation or retains its audit barrier.
            // A user stop suppresses compensation; never try the RGB frame again.
            if (pending.Status.Stage != ProfileApplyStage.ApplyingLighting && IsHardwareFailure(exception))
            {
                await BestEffortFailSafeAsync(CancellationToken.None).ConfigureAwait(false);
                pending.Status = pending.Status with { Fan = ProfileFanState.Unknown };
                UpdateStatus("FailSafe", FailSafeReason.HidFailure);
            }
            FinishProfileApplicationUnderGate(pending,
                SafetyProtectionDisabled || exception is SafetyOutputSuppressedException ||
                pending.CancellationToken.IsCancellationRequested || tickCancellationToken.IsCancellationRequested
                    ? ProfileApplyOutcome.Interrupted : ProfileApplyOutcome.Partial,
                pending.Status.Fan == ProfileFanState.SafetyOverride
                    ? "灯光未确认，风扇已恢复 100% 保护；已保存的方案配置保留。"
                    : "方案配置已保存，后续设备操作未确认；请查看当前保护状态。");
        }
    }

    private void SupersedeProfileApplicationUnderGate(string reason)
    {
        if (pendingProfileApplication is { } pending)
            FinishProfileApplicationUnderGate(pending, ProfileApplyOutcome.Superseded, reason);
    }

    private void InterruptProfileApplicationUnderGate(string reason)
    {
        if (pendingProfileApplication is { } pending)
            FinishProfileApplicationUnderGate(pending, ProfileApplyOutcome.Interrupted, reason);
    }

    private void FinishProfileApplicationUnderGate(PendingProfileApplication pending, ProfileApplyOutcome outcome, string reason)
    {
        pending.Status = pending.Status with
        {
            Stage = ProfileApplyStage.Finished,
            Outcome = outcome,
            Lighting = pending.Status.Lighting == ProfileLightingState.Pending
                ? ProfileLightingState.NotAttempted : pending.Status.Lighting,
            Reason = reason,
        };
        if (ReferenceEquals(pendingProfileApplication, pending))
        {
            pendingProfileApplication = null;
            PublishProfileApplicationUnderGate(pending);
        }
        pending.Completion.TrySetResult();
    }

    private void PublishProfileApplicationUnderGate(PendingProfileApplication pending)
    {
        pending.Status = pending.Status with { UpdatedAt = timeProvider.GetUtcNow() };
        profileApplication = pending.Status;
        Volatile.Write(ref status, GetStatus() with { ProfileApplication = profileApplication });
    }

    private sealed class PendingProfileApplication(ControlProfileSnapshot profile, PipePeerIdentity peer,
        CancellationToken cancellationToken, DateTimeOffset expiresAt, ProfileApplicationStatus status)
    {
        public ControlProfileSnapshot Profile { get; } = profile;
        public PipePeerIdentity Peer { get; } = peer;
        public CancellationToken CancellationToken { get; } = cancellationToken;
        public DateTimeOffset ExpiresAt { get; } = expiresAt;
        public ProfileApplicationStatus Status { get; set; } = status;
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
