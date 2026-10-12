using System.ComponentModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using V12UltraController.Core.Control;
using V12UltraController.Core.Ipc;
using V12UltraController.Core.Status;

namespace V12UltraController.Settings;

public partial class MainWindow
{
    private TrayIcon? tray;
    private readonly TrayWheelAccumulator trayWheel = new();
    private DispatcherTimer? trayDebounce;
    private byte? trayPendingPercent;
    private bool traySending;
    private DateTimeOffset trayStatusReceived;
    private byte? traySendingPercent;
    private string? trayFeedback;
    private string? trayObservedServiceWarning;
    private bool trayFeedbackFromService;
    private DateTimeOffset trayLastNotice;
    private bool trayRefreshingStatus;
    internal Action<string>? TrayTextObserved { get; set; }
    internal Action<string, string>? TrayNoticeObserved { get; set; }

    private void InitializeTray()
    {
        try
        {
            tray = new TrayIcon();
            tray.Open += ShowFromTray;
            tray.Menu += OpenTrayMenu;
            tray.Wheel += QueueTrayWheel;
            InitializeTrayTimer();
            if (!tray.WheelAvailable) ActivityText.Text = "托盘滚轮不可用；请用界面滑块。";
        }
        catch (Win32Exception exception)
        {
            ActivityText.Text = exception.Message;
        }
    }

    // Separately initialized so the real dispatcher timer can be checked with a
    // detached fake-client window, without creating a native icon or mouse hook.
    internal void InitializeTrayTimer()
    {
        if (trayDebounce is not null) return;
        trayDebounce = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(180) };
        trayDebounce.Tick += async (_, _) => await FlushTraySpeedAsync();
    }

    private void ShowFromTray()
    {
        if (_closing || _closed) return;
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private void OpenTrayMenu()
    {
        if (_closing || _closed) return;
        var menu = new ContextMenu { Placement = PlacementMode.MousePoint };
        var open = new MenuItem { Header = "打开控制面板" };
        open.Click += (_, _) => ShowFromTray();
        var backend = new MenuItem { Header = "后台状态 / 启动" };
        backend.Click += (_, _) => { ShowFromTray(); NavigateToPage("maintenance"); };
        menu.Items.Add(open);
        menu.Items.Add(backend);
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "滚轮 ±1%（10–100%）", IsEnabled = false });
        var pure = new MenuItem { Header = "纯手动（停自动）" };
        pure.Click += (_, _) => PureManualButton_Click(this, new RoutedEventArgs());
        menu.Items.Add(pure);
        var exit = new MenuItem { Header = "退出并释放（实体按键）" };
        exit.Click += (_, _) => ReleaseAndExit();
        menu.Items.Add(exit);
        Localization.Attach(menu);
        menu.IsOpen = true;
    }

    private bool TraySpeedAvailable => TraySpeedAllowed(allowBusy: false);
    private bool TrayStatusFresh => trayStatusReceived <= DateTimeOffset.UtcNow &&
        DateTimeOffset.UtcNow - trayStatusReceived < TimeSpan.FromSeconds(5);
    private string TraySpeedBlockedReason => !ManualSpeedAllowed(allowBusy: true) ? ControlBlockedReason :
        !ExplicitManualContext && _lastStatus?.Device.ProtocolCompatible != true ? "设备协议尚未确认兼容，请查看后台状态。" :
        !ExplicitManualContext && _lastStatus?.Fan.PoweredOn != true ? "风扇已关闭或开关状态未知，请先确认开启风扇。" :
        !ExplicitManualContext && _lastStatus?.FailSafe.Active == true ? "温控或故障保护正在生效，请查看概览中的保护原因。" :
        !ExplicitManualContext && !TrayStatusFresh ? "后台状态已过期，正在刷新；刷新后请重新滚动。" : ControlBlockedReason;
    private bool TraySpeedAllowed(bool allowBusy) => ManualSpeedAllowed(allowBusy) && (ExplicitManualContext || _lastStatus is
        { Device.ProtocolCompatible: true, Fan.PoweredOn: true, FailSafe.Active: false } &&
        TrayStatusFresh);

    private async Task RefreshTrayStatusAsync()
    {
        if (trayRefreshingStatus || _commandInProgress || _closing || _closed || _disableSafetyInProgress) return;
        trayRefreshingStatus = true;
        try
        {
            await RefreshStatusAsync(showErrors: false, waitForExistingRefresh: true);
            if (TraySpeedAllowed(allowBusy: true))
            {
                trayFeedback = null;
                trayFeedbackFromService = false;
                ActivityText.Text = "后台状态已刷新，请重新滚动调节转速。";
                UpdateTrayText();
            }
        }
        finally { trayRefreshingStatus = false; }
    }

    internal void QueueTrayWheel(int delta)
    {
        if (_closing || _closed || _disableSafetyInProgress)
        {
            trayPendingPercent = null;
            trayWheel.Reset();
            trayDebounce?.Stop();
            return;
        }
        if (!TraySpeedAllowed(allowBusy: true))
        {
            trayPendingPercent = null;
            trayWheel.Reset();
            trayDebounce?.Stop();
            SetTrayFailure("未发送转速目标。" + TraySpeedBlockedReason);
            if (!ExplicitManualContext && !TrayStatusFresh) _ = RefreshTrayStatusAsync();
            return;
        }
        if (_closing || _closed || _lastStatus is null) return;
        var baseline = trayPendingPercent ?? (ExplicitManualContext ? _lastStatus.Fan.ConfiguredManualPercent : _lastStatus.Fan.ReportedControlPercent);
        if (baseline is not (>= 10 and <= 100)) return;
        var next = trayWheel.Apply(delta, baseline.Value);
        if (next == baseline) return;
        trayFeedback = null;
        trayFeedbackFromService = false;
        trayPendingPercent = next;
        UpdateTrayText();
        // Preserve the first tick's deadline; continuous wheel input is merged
        // into fixed intervals instead of postponing transmission until it stops.
        if (trayDebounce?.IsEnabled == false) trayDebounce.Start();
    }

    internal async Task FlushTraySpeedAsync()
    {
        if (traySending) return;
        if (trayPendingPercent is null) { trayDebounce?.Stop(); return; }
        if (!TraySpeedAllowed(allowBusy: true))
        {
            trayPendingPercent = null;
            trayWheel.Reset();
            trayDebounce?.Stop();
            SetTrayFailure("后续转速目标已取消。" + TraySpeedBlockedReason);
            if (!ExplicitManualContext && !TrayStatusFresh) await RefreshTrayStatusAsync();
            return;
        }
        if (_commandInProgress) { UpdateTrayText(); return; }
        var target = trayPendingPercent.Value;
        ++_manualStatusGeneration;
        traySending = true;
        traySendingPercent = target;
        trayFeedback = null;
        UpdateTrayText();
        var accepted = false;
        try
        {
            await RunCommandAsync(async () =>
            {
                var response = await SendCheckedAsync(PipeOperations.SpeedSet, new { percent = target });
                var confirmed = response.Data!.Value.Deserialize<ControllerStatus>(JsonOptions);
                if (confirmed?.Fan is null || confirmed.Device is null || confirmed.HardwareSafety is null ||
                    confirmed.Thermal is null || confirmed.FailSafe is null ||
                    confirmed.Fan.Mode is not (ControlMode.Manual or ControlMode.PureManual) ||
                    confirmed.Fan.ConfiguredManualPercent is not (>= 10 and <= 100))
                    throw new InvalidOperationException("后台尚未确认托盘手动设置，请刷新状态。");
                var actual = confirmed.Fan.ConfiguredManualPercent.Value;
                if (actual != target && (!confirmed.Device.Connected || confirmed.HardwareSafety.Poisoned ||
                    confirmed.Fan.ReportedControlPercent != actual || confirmed.Fan.PoweredOn != true ||
                    confirmed.LastSuccessfulRead is not { } read || read > DateTimeOffset.UtcNow ||
                    DateTimeOffset.UtcNow - read >= TimeSpan.FromSeconds(10)))
                    throw new InvalidOperationException("后台返回了不同目标，但没有有效设备确认，请刷新状态。");
                accepted = true;
                if (actual != target)
                {
                    // A confirmed physical setting wins over wheel deltas queued
                    // against the old target while this request was in flight.
                    trayPendingPercent = null;
                    trayWheel.Reset();
                }
                // A tray response must not discard a newer text/slider draft in the window.
                RenderStatus(confirmed);
                ActivityText.Text = actual == target
                    ? $"托盘手动目标已保存：{actual}%；" +
                        (confirmed.Fan.Mode == ControlMode.PureManual ? "保持纯手动。" : "当前为手动控制。")
                    : $"设备最终为 {actual}%，已保留此手动档位；托盘请求 {target}% 未确认。";
                if (SafetyProtectionDisabled) ActivityText.Text += "安全保护仍禁用。";
                if (actual != target) SetTrayFailure(ActivityText.Text);
            }, refreshAfterSuccess: true, reportError: message =>
            {
                SetTrayFailure($"转速目标 {target}% 未确认。{message}");
                ShowPageFeedback(ActivityText.Text, "fan");
            });
        }
        finally
        {
            traySending = false;
            traySendingPercent = null;
            // Never retry an ambiguous response or replay input after a connection failure.
            if (!accepted || trayPendingPercent == target) trayPendingPercent = null;
            if (trayPendingPercent.HasValue)
            {
                if (trayDebounce?.IsEnabled == false) trayDebounce.Start();
            }
            else { trayDebounce?.Stop(); trayWheel.Reset(); }
            UpdateTrayText();
        }
    }

    private void WriteTrayText(string text)
    {
        TrayTextObserved?.Invoke(text);
        tray?.SetText(text);
    }

    private void SetTrayFailure(string message, bool fromService = false)
    {
        trayFeedback = message;
        trayFeedbackFromService = fromService;
        ActivityText.Text = message;
        WriteTrayText("风序 · " + message);
        if (_closing || _closed || DateTimeOffset.UtcNow - trayLastNotice < TimeSpan.FromSeconds(5)) return;
        trayLastNotice = DateTimeOffset.UtcNow;
        TrayNoticeObserved?.Invoke("转速目标未确认", message);
        tray?.ShowNotice("转速目标未确认", message);
    }

    private void UpdateTrayText()
    {
        var serviceWarning = _lastStatus?.ServiceState == "ManualCommandUnconfirmed" ?
            "调速目标未获设备确认，设备仍保持原档位；请查看实际值后重新调节。" : null;
        if (serviceWarning != trayObservedServiceWarning)
        {
            trayObservedServiceWarning = serviceWarning;
            if (serviceWarning is not null) SetTrayFailure(serviceWarning, fromService: true);
            else if (trayFeedbackFromService) { trayFeedback = null; trayFeedbackFromService = false; }
        }
        if (trayFeedback is not null) { WriteTrayText("风序 · " + trayFeedback); return; }
        if (traySendingPercent is { } sending)
        {
            WriteTrayText($"风序 · 正在发送 {sending}%，等待设备确认" +
                (trayPendingPercent is { } later && later != sending ? $"；后续目标 {later}%" : ""));
            return;
        }
        if (trayPendingPercent is { } pending)
        {
            WriteTrayText($"风序 · 目标 {pending}%，" + (_commandInProgress ? "等待当前操作完成" : "等待发送"));
            return;
        }
        if (ExplicitManualContext && _lastStatus is { } passive)
        {
            var observed = FanDisplay.Readback(passive.Fan.ReportedControlPercent, passive.Fan.PoweredOn,
                passive.Device.Connected && !passive.HardwareSafety.Poisoned && passive.LastSuccessfulRead is { } lastRead &&
                lastRead <= DateTimeOffset.UtcNow && DateTimeOffset.UtcNow - lastRead < TimeSpan.FromSeconds(10));
            WriteTrayText($"风序 · 手动目标 {passive.Fan.ConfiguredManualPercent?.ToString() ?? "未知"}% · {observed} · {(ExplicitManualAvailable ? "滚轮 ±1%" : "需查看后台状态")}");
            return;
        }
        if (_lastStatus is not { Device.Connected: true } status)
        {
            WriteTrayText("风序 · V12 Ultra — 后台或设备不可用");
            return;
        }
        var value = FanDisplay.Readback(status.Fan.ReportedControlPercent, status.Fan.PoweredOn,
            !status.HardwareSafety.Poisoned && status.LastSuccessfulRead is { } read &&
            read <= DateTimeOffset.UtcNow && DateTimeOffset.UtcNow - read < TimeSpan.FromSeconds(10));
        WriteTrayText($"风序 · V12 Ultra — {value} — {(SafetyAutomaticOutputStopped ? "保护已禁用 · 滚轮 ±1%" : status.FailSafe.Active ? "保护中" : "滚轮 ±1%")}");
    }
}
