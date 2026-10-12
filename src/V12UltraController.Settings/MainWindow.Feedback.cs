using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using V12UltraController.Core.Control;
using V12UltraController.Core.Ipc;
using V12UltraController.Core.Status;

namespace V12UltraController.Settings;

public partial class MainWindow
{
    private readonly Dictionary<string, string> _pageFeedback = new();
    private string _globalStatusDestination = "maintenance";
    private bool _syncingManualInput;
    private bool _manualInputValid = true;
    private bool _curveReading;
    private CancellationTokenSource? _curveReadCancellation;
    private long _curveReadSequence;

    private void UpdateCurveReadAvailability()
    {
        if (LoadCurveButton is null) return;
        LoadCurveButton.IsEnabled = PageReadAvailable && !_curveReading;
        LoadCurveButton.Content = _curveReading ? "读取中…" : "读取已保存曲线";
        LoadCurveButton.ToolTip = _curveReading ? "正在读取后台曲线；可继续编辑。" :
            PageReadAvailable ? "读取后台曲线；未保存修改会先询问。" : "等待后台连接后重试。";
    }

    private void ShowPageFeedback(string message, string? page = null)
    {
        if (_closing || _closed) return;
        _pageFeedback[page ?? CurrentPageId] = message;
        RenderPageFeedback();
    }

    private void DismissPageFeedback_Click(object sender, RoutedEventArgs e)
    {
        _pageFeedback.Remove(CurrentPageId);
        RenderPageFeedback();
    }

    private void RenderPageFeedback()
    {
        if (PageFeedbackBorder is null || _closing || _closed) return;
        var message = _pageFeedback.GetValueOrDefault(CurrentPageId);
        PageFeedbackText.Text = message ?? "";
        PageFeedbackText.ToolTip = message;
        PageFeedbackBorder.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void RenderGlobalStatusSummary()
    {
        if (GlobalStatusBanner is null) return;
        var status = _lastStatus;
        var reason = "";
        var severity = 0;
        _globalStatusDestination = "maintenance";
        if (status is null) { reason = "后台未连接"; severity = 2; }
        else if (status.Software is { } software && software.UiContractVersion != UiCompatibility.ContractVersion)
        { reason = "界面与后台版本不兼容"; severity = 2; _globalStatusDestination = "about"; }
        else if (status.ForcedUninstallRecovery?.Required == true)
        { reason = "需要完成恢复后才能继续控制"; severity = 2; }
        else if (status.AuditMutation is { Blocked: true } or { Pending: true } ||
                 status.SafetyOperationAudit is { Blocked: true } or { Pending: true })
        { reason = "控制暂不可用：有待处理的恢复事务"; severity = 2; }
        else if (status.HardwareSafety.Poisoned)
        { reason = "设备通信异常：需要恢复"; severity = 2; }
        else if (status.ServiceState == "ManualObservationSaveUnconfirmed")
        {
            reason = "已保留设备手动设置，但保存未确认；重启后可能恢复原配置";
            severity = 1; _globalStatusDestination = "fan";
        }
        else if (status.ServiceState == "ManualCommandUnconfirmed")
        {
            reason = "调速目标未获设备确认，设备仍保持原档位；请查看实际值后重新调节";
            severity = 1; _globalStatusDestination = "fan";
        }
        else if (status.SafetyProtection is { Disabled: true } safety &&
                 (!safety.AutomaticOutputStopped || !safety.Persisted || safety.HasInFlightOperation))
        {
            reason = !safety.AutomaticOutputStopped ? "停止自动输出尚未确认" : !safety.Persisted
                ? "保护临时禁用，保存失败" : "保护已禁用，先前操作仍在收尾";
            severity = 1; _globalStatusDestination = "overview";
        }
        else if (status.FailSafe.Active)
        { reason = "温控保护正在生效"; severity = 1; _globalStatusDestination = "fan"; }
        else if (status.Commissioning?.Active == true)
        { reason = "正在配对验证"; severity = 1; }
        else if (!status.Commissioned)
        { reason = "尚未接管设备"; severity = 1; }
        else if (status.SafetyProtection?.Disabled == true)
        { reason = "自动保护已禁用"; _globalStatusDestination = "overview"; }
        else if (status.Fan.Mode == ControlMode.PureManual)
        { reason = "纯手动 · 自动调节已停止"; _globalStatusDestination = "fan"; }
        else if (status.Fan.Mode == ControlMode.Released)
        { reason = "控制已释放 · 使用实体按键"; _globalStatusDestination = "fan"; }

        if (severity == 2 && status?.SafetyProtection?.Disabled == true)
            reason += " · 自动保护仍禁用";
        GlobalStatusText.Text = reason;
        GlobalStatusBanner.Visibility = reason.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        GlobalStatusBanner.SetResourceReference(Border.BackgroundProperty, severity == 1 ? "WarningSurfaceBrush" : "SurfaceRaisedBrush");
        GlobalStatusBanner.SetResourceReference(Border.BorderBrushProperty, severity == 1 ? "WarningBrush" : "BorderBrush");
        if (severity == 2)
        {
            GlobalStatusBanner.Background = new SolidColorBrush(Color.FromRgb(58, 30, 36));
            GlobalStatusBanner.BorderBrush = DisconnectedBrush;
        }
        GlobalStatusDetailsButton.Content = _globalStatusDestination switch
        { "overview" => "查看保护", "fan" => "查看控制", "about" => "检查版本", _ => "查看详情" };
        RenderControlStateSummary();
        RenderPageFeedback();
    }

    private void GlobalStatusDetails_Click(object sender, RoutedEventArgs e)
    {
        if (_globalStatusDestination == "maintenance") OpenMaintenance_Click(sender, e);
        else NavigateToPage(_globalStatusDestination);
    }

    private void RenderControlStateSummary()
    {
        if (OverviewControlModeText is null) return;
        var status = _lastStatus;
        var mode = status?.Fan.Mode switch
        {
            ControlMode.Auto => SafetyProtectionDisabled ? "自动曲线（暂停）" : "自动曲线",
            ControlMode.Manual => "手动控制",
            ControlMode.PureManual => "纯手动",
            ControlMode.Released => "已释放控制",
            _ => "等待确认",
        };
        OverviewControlModeText.Text = mode;
        CurrentControlModeText.Text = mode;
        OverviewControlModeText.ToolTip = CurrentControlModeText.ToolTip = status?.Fan.Mode == ControlMode.Auto
            ? (SafetyProtectionDisabled ? "自动模式已保存，自动调节暂停；" : "当前按自动曲线控制；") +
                "应用手动转速后会切到手动；保护设置不变。"
            : "显示后台确认的控制方式；滑块和输入只改草稿，应用后提交。";
        var fresh = status is { Device.Connected: true, HardwareSafety.Poisoned: false } &&
                    status.LastSuccessfulRead is { } read && read <= DateTimeOffset.UtcNow &&
                    DateTimeOffset.UtcNow - read < TimeSpan.FromSeconds(10);
        OverviewFanStateText.Text = !fresh ? "待确认" : status!.Fan.PoweredOn switch
        { true => "已开启", false => "已关闭", _ => "待确认" };
        const string curveBounds = "2–20个点；温度0–100°C严格递增，风扇10–100%非递减。";
        CurveExplanationText.Text = curveBounds + (status is null ? "保护状态尚未确认。" :
            SafetyProtectionDisabled || status.Fan.Mode is ControlMode.PureManual or ControlMode.Released
                ? "自动调节已停止；保存曲线不会启用保护或输出100%。"
                : "自动模式按曲线运行；90°C保护会请求100%。保存曲线不切换模式。");
        ReleaseControlButton.IsEnabled = StateMutationAvailable && status?.Commissioned == true &&
                                         !_serviceCommissioningActive && status.Fan.Mode != ControlMode.Released;
        ReleaseControlButton.ToolTip = ReleaseControlButton.IsEnabled
            ? "停止软件控制，保留当前风扇状态；选择纯手动可重新控制。" : ControlBlockedReason;
        ManualSpeedInput.IsEnabled = StateMutationAvailable;
    }

    private async void ReleaseControlButton_Click(object sender, RoutedEventArgs e) =>
        await RunCommandAsync(async () =>
        {
            trayDebounce?.Stop(); trayPendingPercent = null;
            var response = await SendCheckedAsync(PipeOperations.ModeSet, new { mode = "released" });
            var confirmed = response.Data!.Value.Deserialize<ControllerStatus>(JsonOptions)
                ?? throw new InvalidOperationException("后台没有返回控制状态。");
            if (confirmed.Owner != "none" || confirmed.Fan.Mode != ControlMode.Released ||
                confirmed.Device.Connected || confirmed.HardwareSafety.Poisoned)
                throw new InvalidOperationException("尚未确认释放控制，请刷新状态。");
            RenderStatus(confirmed);
            ActivityText.Text = "控制已释放，界面继续运行；选择纯手动可重新控制。";
        }, refreshAfterSuccess: true);

    private void SyncManualInputFromSlider()
    {
        if (_syncingManualInput || ManualSpeedInput is null) return;
        _syncingManualInput = true;
        try { ManualSpeedInput.Text = Math.Round(ManualSpeedSlider.Value).ToString(CultureInfo.InvariantCulture); }
        finally { _syncingManualInput = false; }
        _manualInputValid = true;
        RenderManualInputValidation();
    }

    private void ManualSpeedInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncingManualInput || ManualSpeedSlider is null || ManualSpeedValidationText is null) return;
        _manualSpeedDirty = true;
        _manualInputValid = int.TryParse(ManualSpeedInput.Text.Trim(), NumberStyles.None,
            CultureInfo.InvariantCulture, out var value) && value is >= 10 and <= 100;
        if (_manualInputValid)
        {
            _syncingManualInput = true;
            try { ManualSpeedSlider.Value = value; }
            finally { _syncingManualInput = false; }
        }
        RenderManualInputValidation();
        UpdateControlButtonsForBusyState();
    }

    private void ManualSpeedInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            if (_manualInputValid && ApplyManualSpeedButton.IsEnabled) ApplyManualSpeedButton_Click(sender, e);
            else RenderManualInputValidation();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            _renderingManualSpeed = true;
            try { ManualSpeedSlider.Value = _lastStatus?.Fan.ConfiguredManualPercent ?? ManualSpeedSlider.Value; }
            finally { _renderingManualSpeed = false; }
            _manualSpeedDirty = false;
            SyncManualInputFromSlider();
            UpdateControlButtonsForBusyState();
        }
    }

    private void ManualSpeedInput_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => RenderManualInputValidation();

    private void RenderManualInputValidation()
    {
        if (ManualSpeedValidationText is null) return;
        ManualSpeedValidationText.Text = _manualInputValid ? "" : "请输入10–100之间的整数。";
        ManualSpeedValidationText.Visibility = _manualInputValid ? Visibility.Collapsed : Visibility.Visible;
        ManualSpeedInput.ToolTip = "输入10–100%；Enter应用，Esc恢复目标。纯手动保留原模式。";
    }
}
