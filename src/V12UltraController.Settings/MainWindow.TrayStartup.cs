using System.IO;
using System.Security;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using V12UltraController.Core.Control;
using V12UltraController.Core.Ipc;
using V12UltraController.Core.Status;

namespace V12UltraController.Settings;

public partial class MainWindow
{
    private ITrayStartupPreference? trayStartupPreference;
    private bool renderingTrayStartup;
    private bool runtimeStarted;
    private bool startupTakeoverAttempted;

    internal void InitializeTrayStartup(ITrayStartupPreference preference)
    {
        trayStartupPreference = preference;
        RefreshTrayStartup();
    }

    private void RefreshTrayStartup()
    {
        if (trayStartupPreference is null) return;
        renderingTrayStartup = true;
        try
        {
            var enabled = trayStartupPreference.ReadEnabled();
            TrayStartupCheckBox.IsChecked = enabled;
            TrayStartupCheckBox.IsEnabled = true;
            TrayStartupText.Text = enabled
                ? trayStartupPreference.ReadBlocked() ? "Windows 禁用；请在启动应用中启用 FanOrder。" : "已启用 · 登录后显示托盘并自动接管"
                : "未启用";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException)
        {
            TrayStartupCheckBox.IsEnabled = false;
            TrayStartupText.Text = "读取失败：" + error.Message;
        }
        finally { renderingTrayStartup = false; }
    }

    private void RefreshTrayStartup_Click(object sender, RoutedEventArgs e) => RefreshTrayStartup();

    private void TrayStartup_Changed(object sender, RoutedEventArgs e)
    {
        if (renderingTrayStartup || trayStartupPreference is null) return;
        try
        {
            trayStartupPreference.SetEnabled(TrayStartupCheckBox.IsChecked == true);
            RefreshTrayStartup();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException)
        {
            RefreshTrayStartup();
            TrayStartupText.Text = "保存失败：" + error.Message;
        }
    }

    // Create the native handle without Show, avoiding a frame/taskbar flash.
    internal void StartInTray()
    {
        _ = new WindowInteropHelper(this).EnsureHandle();
        if (tray is not null)
        {
            _ = StartRuntimeAsync();
            return;
        }
        // Show the window if Explorer rejected the icon, avoiding a lost UI.
        Show();
    }

    private async Task StartRuntimeAsync()
    {
        if (runtimeStarted) return;
        runtimeStarted = true;
        _refreshTimer.Start();
        await RefreshStatusAsync(showErrors: false);
        await LoadCurveAsync(showErrors: false);
    }

    /// <summary>
    /// A commissioned installation normally starts in the last saved mode.  A
    /// previous tray exit deliberately saves <see cref="ControlMode.Released"/>,
    /// which used to leave the freshly started UI passive and produced a tray
    /// warning as soon as the user tried to adjust it.  On application startup,
    /// reclaim that explicitly commissioned installation by selecting Auto once.
    /// Manual and pure-manual modes remain untouched so the last explicit manual
    /// choice still has priority.  Any failure is reported inline; startup must
    /// never turn an ambiguous takeover into a notification or an unverified
    /// hardware write.
    /// </summary>
    private async Task AutoTakeoverReleasedControlAsync()
    {
        if (startupTakeoverAttempted || _closing || _closed || _lastStatus is not { } status ||
            !status.Commissioned || status.Fan.Mode != ControlMode.Released ||
            !StateMutationAvailable)
        {
            return;
        }

        startupTakeoverAttempted = true;
        try
        {
            var response = await SendCheckedAsync(PipeOperations.ModeSet, new { mode = "auto" });
            var confirmed = response.Data!.Value.Deserialize<ControllerStatus>(JsonOptions)
                ?? throw new InvalidDataException("后台没有返回启动接管状态。");
            if (!confirmed.Commissioned || confirmed.Fan.Mode != ControlMode.Auto)
                throw new InvalidOperationException("后台未确认启动接管，仍保持原控制方式。");

            RenderStatus(confirmed);
            ActivityText.Text = SafetyProtectionDisabled
                ? "已启动接管；安全保护仍禁用，自动调节保持暂停。"
                : "已启动接管，正在按自动曲线控制。";
        }
        catch (Exception exception) when (IsExpectedClientException(exception))
        {
            // Keep this quiet in the tray.  A later explicit mode action can
            // retry with a current, user-visible status.
            ActivityText.Text = "启动接管未确认：" + ExplainException(exception);
        }
    }

    internal void ActivateExistingWindow() => ShowFromTray();
}
