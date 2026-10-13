using System.Text.Json;
using System.Windows;
using V12UltraController.Core.Control;
using V12UltraController.Core.Ipc;
using V12UltraController.Core.Status;

namespace V12UltraController.Settings;

public partial class MainWindow
{
    private bool trayExitRequested;
    private bool _disableSafetyInProgress;
    private bool _restoringSavedManualSpeed;
    private long _controlRequestGeneration;
    private string? _prioritySafetyMessage;
    private bool PureManualSelected => _currentMode == ControlMode.PureManual;
    private async void PureManualButton_Click(object sender, RoutedEventArgs e) =>
        await RunCommandAsync(async () =>
        {
            await SendCheckedAsync(PipeOperations.ModeSet, new { mode = "puremanual" });
            ActivityText.Text = "纯手动已持久保存：停止温控、自动满速与后台重申；仅明确操作改变风扇。";
        }, refreshAfterSuccess: true);

    private async void DisableSafetyProtectionButton_Click(object sender, RoutedEventArgs e) =>
        await DisableSafetyProtectionAsync();

    private async void ResumeSafetyProtectionButton_Click(object sender, RoutedEventArgs e) =>
        await ResumeSafetyProtectionAsync();

    private bool SafetyProtectionDisabled => _lastStatus?.SafetyProtection?.Disabled == true;
    private bool SafetyAutomaticOutputStopped => _lastStatus?.SafetyProtection is { Disabled: true, AutomaticOutputStopped: true };
    internal Func<bool>? ConfirmResumeSafetyProtection { get; set; }

    // This priority request must remain available while a regular command or
    // profile waits for the service. It does not use the ordinary command gate.
    private bool CanRequestSafetyOverride => _lastStatus is not null &&
        _lastStatus.Software?.UiContractVersion == UiCompatibility.ContractVersion &&
        _lastStatus.Software.SafetyDisableSupported && !_disableSafetyInProgress && !_closing && !_closed;
    private bool CanDisableSafetyProtection => CanRequestSafetyOverride &&
        (!SafetyProtectionDisabled || _lastStatus?.SafetyProtection?.Persisted != true);

    internal async Task DisableSafetyProtectionAsync()
    {
        if (!CanDisableSafetyProtection)
        {
            ActivityText.Text = SafetyProtectionDisabled ? "安全保护已禁用。" : "无法发送禁用请求：" + DisableSafetyUnavailableReason();
            return;
        }

        var restoreSavedSpeed = !SafetyProtectionDisabled;
        var savedManualPercent = _lastStatus!.Fan.ConfiguredManualPercent;
        _disableSafetyInProgress = true;
        _controlRequestGeneration++;
        trayDebounce?.Stop();
        trayPendingPercent = null;
        trayWheel.Reset();
        _profileRequestCancellation?.Cancel();
        var hadPendingProfile = _lastStatus?.ProfileApplication?.Outcome == ProfileApplyOutcome.Applying ||
            _profileResult?.Text.StartsWith("正在应用", StringComparison.Ordinal) == true;
        if (hadPendingProfile && _profileResult is not null)
            _profileResult.Text = "正在停止自动控制，等待后台确认。";
        _prioritySafetyMessage = "正在优先停止自动控制…";
        ActivityText.Text = _prioritySafetyMessage;
        UpdateControlButtonsForBusyState();
        UpdateCommissionButtonsForBusyState();
        UpdateShutdownButtonsForBusyState();
        try
        {
            var response = await SendCheckedAsync(PipeOperations.SafetyDisable, new { }, _windowLifetime.Token, TimeSpan.FromSeconds(10));
            var confirmed = ReadSafetyProtectionResponse(response);
            if (confirmed.SafetyProtection is not { Disabled: true, AutomaticOutputStopped: true } state)
                throw new InvalidOperationException("后台未确认停止新的自动输出，不能确认禁用成功。");
            RenderStatus(confirmed);
            _prioritySafetyMessage = state.Persisted ? "安全保护已禁用。" : "安全保护已临时禁用，但保存失败：" + state.PersistenceError;
            if (state.HasInFlightOperation) _prioritySafetyMessage += " 已发出的操作仍可能完成。";
            if (hadPendingProfile && _profileResult is not null)
                _profileResult.Text = "已优先停止自动输出；先前方案结果不再作为当前控制状态。";
            ActivityText.Text = _prioritySafetyMessage;
            if (restoreSavedSpeed) await RestoreSavedManualSpeedAfterDisableAsync(savedManualPercent);
        }
        catch (Exception exception) when (IsExpectedClientException(exception))
        {
            if (_closing && exception is OperationCanceledException) return;
            _prioritySafetyMessage = "禁用结果未确认：" + ExplainException(exception);
            FailSafeStateText.Text = "禁用未确认";
            FailSafeReasonText.Text = _prioritySafetyMessage;
            if (hadPendingProfile && _profileResult is not null)
                _profileResult.Text = "已取消本地等待；后台方案与禁用结果需重新读取确认。";
        }
        finally
        {
            _disableSafetyInProgress = false;
            _restoringSavedManualSpeed = false;
            ActivityText.Text = _prioritySafetyMessage ?? "禁用结果未确认。";
            UpdateControlButtonsForBusyState();
            UpdateCommissionButtonsForBusyState();
            UpdateShutdownButtonsForBusyState();
        }
    }

    private async Task RestoreSavedManualSpeedAfterDisableAsync(byte? savedManualPercent)
    {
        if (savedManualPercent is not (>= 10 and <= 100))
        {
            _prioritySafetyMessage += " 没有已确认的保存手动目标，未发送调速。";
            return;
        }
        var disableResult = _prioritySafetyMessage;
        _restoringSavedManualSpeed = true;
        _prioritySafetyMessage = disableResult + $" 正在恢复保存的手动转速 {savedManualPercent}%…";
        ActivityText.Text = _prioritySafetyMessage;
        RenderDisableSafetyProtectionButton();
        try
        {
            // This explicit speed transaction begins only after the stop has been
            // acknowledged, and never clears the protection latch.
            var response = await SendCheckedAsync(PipeOperations.SpeedSet, new { percent = savedManualPercent.Value },
                _windowLifetime.Token, TimeSpan.FromSeconds(25));
            var confirmed = ReadSafetyProtectionResponse(response);
            RenderStatus(confirmed);
            if (confirmed.SafetyProtection is not { Disabled: true, AutomaticOutputStopped: true })
                throw new InvalidOperationException("后台未确认安全保护仍保持禁用，请刷新状态。");
            if (!confirmed.Device.Connected || confirmed.HardwareSafety.Poisoned ||
                confirmed.Fan.ConfiguredManualPercent != savedManualPercent ||
                confirmed.Fan.ReportedControlPercent != savedManualPercent || confirmed.Fan.PoweredOn != true ||
                confirmed.LastSuccessfulRead is not { } read || read > DateTimeOffset.UtcNow ||
                DateTimeOffset.UtcNow - read >= TimeSpan.FromSeconds(10))
                throw new InvalidOperationException($"后台回读未确认手动转速 {savedManualPercent}%。");
            _prioritySafetyMessage = (confirmed.SafetyProtection.Persisted ? "安全保护已禁用。" :
                "安全保护已临时禁用，禁用状态尚未保存。") + $" 保存的手动转速 {savedManualPercent}% 已由设备确认。";
        }
        catch (Exception exception) when (IsExpectedClientException(exception))
        {
            if (_closing && exception is OperationCanceledException) return;
            // Keep the acknowledged stop and refresh the observation. Never retry
            // an ambiguous speed write or imply a requested target was reached.
            try
            {
                var response = await SendCheckedAsync(PipeOperations.StatusGet, new { }, _windowLifetime.Token,
                    TimeSpan.FromSeconds(5));
                RenderStatus(ReadSafetyProtectionResponse(response));
            }
            catch (Exception refreshException) when (IsExpectedClientException(refreshException)) { }
            _prioritySafetyMessage = SafetyAutomaticOutputStopped
                ? (_lastStatus?.SafetyProtection?.Persisted == true
                    ? "安全保护已禁用。" : "安全保护已临时禁用，禁用状态尚未保存。")
                : "曾确认禁用；当前安全保护状态需重新确认。";
            _prioritySafetyMessage += $" 手动转速 {savedManualPercent}% 未确认恢复：" + ExplainException(exception);
        }
        finally { _restoringSavedManualSpeed = false; }
    }

    internal async Task ResumeSafetyProtectionAsync()
    {
        if (!CanRequestSafetyOverride || !SafetyProtectionDisabled || _commandInProgress) return;
        if (!(ConfirmResumeSafetyProtection?.Invoke() ?? LocalizedMessageBox.Show(this,
            "恢复安全保护后，后台可按保存的控制模式继续温控、满速保护与状态重申。原有硬件或恢复故障仍需单独处理。确认恢复？",
            "恢复安全保护", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes)) return;
        await RunCommandAsync(async () =>
        {
            var response = await SendCheckedAsync(PipeOperations.SafetyResume, new { confirmed = true });
            var confirmed = ReadSafetyProtectionResponse(response);
            if (confirmed.SafetyProtection?.Disabled != false)
                throw new InvalidOperationException("后台未确认恢复安全保护。");
            _prioritySafetyMessage = null;
            RenderStatus(confirmed);
            ActivityText.Text = confirmed.HardwareSafety.Poisoned ||
                confirmed.AuditMutation is { Blocked: true } or { Pending: true } ||
                confirmed.SafetyOperationAudit is { Blocked: true } or { Pending: true } ||
                confirmed.ForcedUninstallRecovery is { Required: true }
                ? "停止开关已解除；后台仍有故障待恢复。"
                : "安全保护已恢复，后台可按保存的控制模式运行。";
        });
    }

    private static ControllerStatus ReadSafetyProtectionResponse(PipeResponse response)
    {
        var status = response.Data!.Value.Deserialize<ControllerStatus>(JsonOptions);
        if (status is null || status.Fan is null || status.Device is null || status.Thermal is null ||
            status.FailSafe is null || status.HardwareSafety is null || status.SafetyProtection is null)
            throw new InvalidOperationException("后台未返回完整的安全保护状态。");
        return status;
    }

    private string DisableSafetyUnavailableReason() => _lastStatus is null ? "后台未连接。" :
        _closing || _closed ? "窗口正在关闭。" :
        _disableSafetyInProgress ? "正在等待安全保护操作确认。" :
        _lastStatus.Software?.UiContractVersion != UiCompatibility.ContractVersion ? "尚未确认界面与后台协议兼容。" :
        _lastStatus.Software.SafetyDisableSupported != true ? "后台尚不支持独立禁用安全保护。" : "当前无法发送请求。";

    private void RenderDisableSafetyProtectionButton()
    {
        var disabled = SafetyProtectionDisabled;
        var state = _lastStatus?.SafetyProtection;
        DisableSafetyProtectionButton.Content = _restoringSavedManualSpeed ? "正在恢复转速…" : _disableSafetyInProgress ? "正在禁用…" :
            disabled ? state?.Persisted == true ? "已禁用" : "重试保存" : "禁用安全保护";
        DisableSafetyProtectionButton.Visibility = disabled && state?.Persisted == true && !_disableSafetyInProgress
            ? Visibility.Collapsed : Visibility.Visible;
        DisableSafetyProtectionButton.IsEnabled = CanDisableSafetyProtection;
        DisableSafetyProtectionButton.ToolTip = disabled
            ? "自动保护输出已停止；点击恢复保护才会重新启用。" +
                (state?.Persisted == false ? " 本次禁用尚未保存：" + state.PersistenceError + " 重试只保存禁用状态，不再调整转速。" : "") +
                (state?.HasInFlightOperation == true ? " 先前已发送的操作仍可能完成。" : "")
            : CanDisableSafetyProtection
                ? _lastStatus?.Fan.ConfiguredManualPercent is >= 10 and <= 100
                    ? $"禁用保护并恢复保存的手动转速（{_lastStatus.Fan.ConfiguredManualPercent}%）；故障时可能只完成禁用。"
                    : "禁用温控、满速保护和自动重申；无已确认目标时不发送调速。"
                : DisableSafetyUnavailableReason();
        ResumeSafetyProtectionButton.Visibility = disabled ? Visibility.Visible : Visibility.Collapsed;
        ResumeSafetyProtectionButton.IsEnabled = disabled && CanRequestSafetyOverride && !_commandInProgress;
    }

    private void RenderSafetyProtectionState(ControllerStatus status)
    {
        if (status.SafetyProtection is not { Disabled: true } state) return;
        FailSafeStateText.Text = !state.AutomaticOutputStopped ? "停止待确认" : state.Persisted ? "已禁用" : "临时禁用";
        FailSafeStateText.Foreground = WarningBrush;
        FailSafeReasonText.Text = "自动保护输出已停止；手动转速以设备回读为准，仍可主动设速。" +
            (state.HasInFlightOperation ? " 已发出的操作仍可能完成。" : "") +
            (!state.Persisted ? " 禁用尚未持久保存：" + state.PersistenceError : "") +
            " 只有明确恢复安全保护才会清除此状态。";
    }

    private async void ReleaseAndExit()
    {
        if (_commandInProgress || _closing) return;
        trayDebounce?.Stop(); trayPendingPercent = null;
        var released = false;
        await RunCommandAsync(async () =>
        {
            var response = await SendCheckedAsync(PipeOperations.ModeSet, new { mode = "released" });
            if (response.Data is not { } data ||
                data.GetProperty("owner").GetString() != "none" ||
                !string.Equals(data.GetProperty("fan").GetProperty("mode").GetString(), "released", StringComparison.OrdinalIgnoreCase) ||
                data.GetProperty("device").GetProperty("connected").GetBoolean() ||
                data.GetProperty("hardwareSafety").GetProperty("poisoned").GetBoolean())
                throw new InvalidOperationException("后台未确认释放；界面保持打开，不能宣称已退出控制。");
            released = true;
        });
        if (released) { trayExitRequested = true; Close(); }
    }
}
