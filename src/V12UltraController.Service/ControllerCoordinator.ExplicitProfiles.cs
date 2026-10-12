using V12UltraController.Core.Configuration;
using V12UltraController.Core.Control;
using V12UltraController.Core.Hardware;
using V12UltraController.Core.Ipc;
using V12UltraController.Core.Status;

namespace V12UltraController.Service;

internal sealed partial class ControllerCoordinator
{
    private async Task<ProfileApplyResult> ApplyExplicitManualProfileUnderGateAsync(ControlProfileSnapshot profile,
        string operationId, PipePeerIdentity peer, long requestEpoch, CancellationToken epochCancellation,
        CancellationToken cancellationToken)
    {
        using var sequence = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, epochCancellation);
        var previousExplicitScope = explicitManualIo.Value;
        var previousSequence = explicitSequenceCancellation.Value;
        try
        {
            explicitManualIo.Value = true;
            explicitSequenceCancellation.Value = sequence.Token;
            sequence.Token.ThrowIfCancellationRequested();
            // Validate original ownership before saving the profile's desired Manual mode.
            // A Released controller must never be silently adopted by a profile.
            var target = await RequireExplicitDeviceUnderGateAsync(cancellationToken).ConfigureAwait(false);
            if (profile.Lighting is not null && target is not IV12LightingDevice)
                throw new ControllerOperationException(PipeErrorCodes.UnsupportedDevice, "当前设备不支持方案中的灯光预设。");
            SupersedeProfileApplicationUnderGate("新的手动方案已替代旧方案，旧灯光不会继续应用。");
            var now = timeProvider.GetUtcNow();
            var pending = new PendingProfileApplication(profile, peer, sequence.Token, now + ProfileApplyTimeout,
                new ProfileApplicationStatus(operationId, profile.Id, profile.Name,
                    ProfileApplyStage.SavingConfiguration, ProfileApplyOutcome.Applying,
                    ProfileConfigurationState.Pending, ProfileFanState.Pending,
                    profile.Lighting is null ? ProfileLightingState.NotRequested : ProfileLightingState.Pending,
                    "正在保存手动方案；自动控制保持暂停。", now, now));
            pendingProfileApplication = pending;
            profileApplicationEpoch = requestEpoch;
            PublishProfileApplicationUnderGate(pending);
            var next = configuration with { Mode = ControlMode.Manual, ManualSpeedPercent = profile.ManualSpeedPercent, Curve = profile.Curve };
            try
            {
                await PersistConfigurationMutationAsync(next, PipeOperations.ProfileApply, peer, sequence.Token).ConfigureAwait(false);
                if (profile.Lighting is not null) ForgetFanLightingRestore();
                SetPassiveDecision();
                UpdateStatus("SafetyProtectionDisabled");
                pending.Status = pending.Status with { Configuration = ProfileConfigurationState.Saved,
                    Stage = ProfileApplyStage.WaitingForFan, Reason = "方案已保存，正在单次应用手动转速。" };
                PublishProfileApplicationUnderGate(pending);
                sequence.Token.ThrowIfCancellationRequested();
                await ExplicitManualCommandUnderGateAsync(profile.ManualSpeedPercent, true, peer,
                    cancellationToken, persistConfiguration: false).ConfigureAwait(false);
                sequence.Token.ThrowIfCancellationRequested();
                // A direct manual request can adopt a later physical change. That
                // observation does not confirm this profile's exact saved target.
                if (lastDeviceState is not { PoweredOn: true } confirmedFan ||
                    confirmedFan.FanSpeedPercent != profile.ManualSpeedPercent)
                {
                    pending.Status = pending.Status with { Fan = ProfileFanState.Unknown };
                    FinishProfileApplicationUnderGate(pending, ProfileApplyOutcome.Partial,
                        "方案配置已保存，但设备最终转速与方案目标不同；已保留实际回读，灯光未开始，未重试或自动满速。");
                    return new ProfileApplyResult(pending.Status, GetStatus());
                }
                manualPower.RequestOn();
                pending.Status = pending.Status with { Fan = ProfileFanState.Confirmed };
                PublishProfileApplicationUnderGate(pending);
                sequence.Token.ThrowIfCancellationRequested();
                if (profile.Lighting is not null)
                {
                    pending.Status = pending.Status with { Stage = ProfileApplyStage.ApplyingLighting,
                        Reason = "转速已回读确认，正在单次应用灯光。" };
                    PublishProfileApplicationUnderGate(pending);
                    await SetExplicitLightingUnderGateAsync(profile.Lighting, peer, cancellationToken, () =>
                    {
                        pending.Status = pending.Status with { Lighting = ProfileLightingState.Unknown };
                        PublishProfileApplicationUnderGate(pending);
                    }).ConfigureAwait(false);
                    pending.Status = pending.Status with { Lighting = ProfileLightingState.Confirmed };
                    // RGB preserves its own fresh GET, including a later physical knob change.
                    // That preservation cannot prove the earlier profile speed is still active.
                    if (lastDeviceState is not { PoweredOn: true } final || final.FanSpeedPercent != profile.ManualSpeedPercent)
                    {
                        pending.Status = pending.Status with { Fan = ProfileFanState.Unknown };
                        FinishProfileApplicationUnderGate(pending, ProfileApplyOutcome.Partial,
                            "灯光已确认，但转速随后发生变化；未覆盖实体调整、未重试，自动控制仍暂停。");
                        return new ProfileApplyResult(pending.Status, GetStatus());
                    }
                }
                sequence.Token.ThrowIfCancellationRequested();
                FinishProfileApplicationUnderGate(pending, ProfileApplyOutcome.Completed,
                    profile.Lighting is null ? "手动方案已单次应用并回读确认；自动控制仍暂停。" :
                        "手动转速和灯光已单次应用并回读确认；自动控制仍暂停。");
            }
            catch (Exception exception) when (!IsFatalPersistenceFailure(exception))
            {
                var saved = ReferenceEquals(configuration, next);
                if (saved)
                {
                    SetPassiveDecision();
                    UpdateStatus("SafetyProtectionDisabled");
                }
                pending.Status = pending.Status with
                {
                    Configuration = saved ? ProfileConfigurationState.Saved :
                        auditMutationBlocked ? ProfileConfigurationState.Unknown : ProfileConfigurationState.Unchanged,
                    Fan = pending.Status.Fan == ProfileFanState.Pending ? ProfileFanState.Unknown : pending.Status.Fan,
                };
                if (pending.Status.Stage == ProfileApplyStage.ApplyingLighting &&
                    (lastDeviceState is not { PoweredOn: true } actual || actual.FanSpeedPercent != profile.ManualSpeedPercent))
                    pending.Status = pending.Status with { Fan = ProfileFanState.Unknown };
                var interrupted = sequence.IsCancellationRequested || exception is SafetyOutputSuppressedException;
                FinishProfileApplicationUnderGate(pending, interrupted ? ProfileApplyOutcome.Interrupted :
                    saved || auditMutationBlocked ? ProfileApplyOutcome.Partial : ProfileApplyOutcome.Rejected,
                    interrupted ? "手动方案已中断；已保存和已生效部分保留，未开始的操作不会继续，自动控制仍暂停。" :
                        $"手动方案未完整确认：{exception.Message} 自动控制仍暂停，未自动满速或重试。");
            }
            return new ProfileApplyResult(pending.Status, GetStatus());
        }
        finally
        {
            if (profileApplication is { } completed && completed.OperationId == operationId &&
                completed.Outcome != ProfileApplyOutcome.Applying)
                await SafeAuditAsync(PipeOperations.ProfileApply, "explicit-result", peer.CallerSid, peer.Source,
                    new { operationId, profileId = profile.Id, completed.Configuration, completed.Fan,
                        completed.Lighting, completed.Outcome }, CancellationToken.None).ConfigureAwait(false);
            explicitManualIo.Value = previousExplicitScope;
            explicitSequenceCancellation.Value = previousSequence;
        }
    }
}
