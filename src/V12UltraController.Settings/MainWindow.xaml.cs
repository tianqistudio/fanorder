using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using V12UltraController.Core.Control;
using V12UltraController.Core.Diagnostics;
using V12UltraController.Core.Ipc;
using V12UltraController.Core.Status;
using V12UltraController.Core.Thermal;

namespace V12UltraController.Settings;

public partial class MainWindow : Window
{
    private static readonly TimeSpan PipeTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan CommissioningPipeTimeout = TimeSpan.FromSeconds(60);
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions(writeIndented: false);
    private static readonly SolidColorBrush ConnectedBrush = new(Color.FromRgb(76, 215, 160));
    private static readonly SolidColorBrush DisconnectedBrush = new(Color.FromRgb(255, 107, 120));
    private static readonly SolidColorBrush WarningBrush = new(Color.FromRgb(255, 180, 84));
    private readonly IPipeApiClient _client;
    private readonly IBackendStarter _backendStarter;
    private readonly ISystemShutdownRequester _systemShutdownRequester;
    private readonly IAppearancePreference? _appearancePreference;
    private readonly ILanguagePreference? _languagePreference;
    private readonly DiagnosticsArchiveWriter _diagnosticsArchiveWriter = new();
    private readonly DispatcherTimer _refreshTimer;
    private readonly CancellationTokenSource _windowLifetime = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private bool _commandInProgress;
    private bool _closing;
    private bool _allowClose;
    private bool _closed;
    private string? _commissionSessionId;
    private bool _serviceCommissioningActive;
    private string? _serviceCommissioningSessionId;
    private string? _serviceCommissioningStage;
    private DateTimeOffset? _serviceCommissioningExpiresAt;
    private ControlMode? _currentMode;
    private bool _manualSpeedDirty;
    private bool _renderingManualSpeed;
    private long _manualStatusGeneration;
    private ControllerStatus? _lastStatus;

    public MainWindow()
        : this(new NamedPipeApiClient(), appearancePreference: new LocalAppearancePreference(),
            profileStore: new LocalControlProfileStore(),
            shortcutStore: new LocalShortcutStore(ShortcutStorePaths.ForApplication("TianqiStudio", "V12UltraController")),
            enableNativeShortcuts: true, languagePreference: new LocalLanguagePreference(),
            systemShutdownRequester: new WindowsSystemShutdownRequester())
    {
        InitializeTrayStartup(new TrayStartupPreference());
        SourceInitialized += (_, _) => InitializeTray();
    }

    internal MainWindow(IPipeApiClient client, IBackendStarter? backendStarter = null, IAppearancePreference? appearancePreference = null,
        IControlProfileStore? profileStore = null, IShortcutStore? shortcutStore = null, bool enableNativeShortcuts = false,
        ILanguagePreference? languagePreference = null, ISystemShutdownRequester? systemShutdownRequester = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _backendStarter = backendStarter ?? new WindowsBackendStarter();
        _systemShutdownRequester = systemShutdownRequester ?? new WindowsSystemShutdownRequester();
        _appearancePreference = appearancePreference;
        _languagePreference = languagePreference;
        _renderingStartupFan = true;
        InitializeComponent();
        _renderingStartupFan = false;
        _lightingLastCleanDraft = LightingDraft();
        AppearanceCombo.SelectedIndex = _appearancePreference?.LoadClassic() == true ? 1 : 0;
        var language = _languagePreference?.Load() ?? UiLanguage.SimplifiedChinese;
        LanguageCombo.SelectedIndex = language == UiLanguage.English ? 1 : 0;
        Localization.SetLanguage(language);
        Title = Localization.Translate("风序 · FanOrder 设置");
        ApplyAppearance();
        appearanceReady = true;
        DataContext = this;
        ResetCurveToDefault();
        InitializeProfiles(profileStore);
        InitializeNavigation();
        InitializeResponsiveLayout();
        InitializeShortcuts(enableNativeShortcuts, shortcutStore);
        Localization.Attach(this);
        languageReady = true;
        _manualSpeedDirty = false;
        SyncManualInputFromSlider();
        UpdateControlButtonsForBusyState();

        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(2),
        };
        _refreshTimer.Tick += async (_, _) =>
        {
            await RefreshStatusAsync(showErrors: false);
            await RefreshQuickLightingAsync();
        };

        Loaded += async (_, _) =>
        {
            await StartRuntimeAsync();
        };
        Closing += MainWindow_Closing;
        Closed += (_, _) =>
        {
            _closed = true;
            trayDebounce?.Stop();
            tray?.Dispose();
            _refreshTimer.Stop();
            _windowLifetime.Dispose();
        };
    }

    public ObservableCollection<CurvePointRow> CurvePoints { get; } = new();

    private async void StartBackendButton_Click(object sender, RoutedEventArgs e)
    {
        if (_commandInProgress || _closing || _lastStatus is not null) return;
        _commandInProgress = true;
        UpdateControlButtonsForBusyState();
        try
        {
            ActivityText.Text = "正在请求 Windows 启动后台；已接管时会恢复已配置策略。";
            await _backendStarter.StartAsync(_windowLifetime.Token);
            await RefreshStatusAsync(showErrors: false, waitForExistingRefresh: true, allowDuringCommand: true);
            ActivityText.Text = _lastStatus is null
                ? "启动请求已提交，但尚未连接后台；请稍后刷新，勿反复点击。"
                : "后台已连接。开机启动设置未改变。";
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            ActivityText.Text = "已取消管理员授权，未确认后台启动。";
        }
        catch (OperationCanceledException)
        {
            ActivityText.Text = "后台启动结果未确认，可能仍在启动；请稍后刷新。";
        }
        catch (Exception exception) when (exception is Win32Exception or IOException or InvalidOperationException)
        {
            ActivityText.Text = exception.Message;
        }
        finally
        {
            _commandInProgress = false;
            UpdateControlButtonsForBusyState();
        }
    }
    private async void RefreshButton_Click(object sender, RoutedEventArgs e) =>
        await RefreshStatusAsync(showErrors: true);

    private async void AutoModeButton_Click(object sender, RoutedEventArgs e) =>
        await RunCommandAsync(async () =>
        {
            await SendCheckedAsync(PipeOperations.ModeSet, new { mode = "auto" });
            _manualSpeedDirty = false;
            ActivityText.Text = SafetyProtectionDisabled ? "自动模式已保存；安全保护仍禁用，自动调节暂停。" : "已切换到自动曲线。";
        }, refreshAfterSuccess: true);

    private async void FanOnButton_Click(object sender, RoutedEventArgs e) =>
        await SetFanPowerAsync(true);

    private async void FanOffButton_Click(object sender, RoutedEventArgs e)
    {
        if (!(ConfirmFanOff?.Invoke(FanOffConfirmationText) ?? MessageBox.Show(this,
                FanOffConfirmationText, "关闭风扇", MessageBoxButton.YesNo, MessageBoxImage.Warning,
                MessageBoxResult.No) == MessageBoxResult.Yes))
        {
            return;
        }

        await SetFanPowerAsync(false);
    }

    internal Func<string, bool>? ConfirmFanOff { get; set; }
    private bool FanLightingLinkSupported => _lastStatus?.Software?.FanLightingLinkSupported == true;
    private const string FanLightingLinkHelp = "关闭风扇时同时熄灯；再次开启可恢复关闭前的灯光。设备按键和手动灯光的新设置优先。仅本次后台运行且设备未断开时保留恢复记录。";
    private string FanOffConfirmationText => (FanLightingLinkSupported
        ? "确认关闭风扇并熄灯？关闭前的灯光选择会临时保留，供再次开启时恢复。"
        : ExplicitManualContext ? "确认关闭风扇？" : "确认关闭外置风扇？") + (ExplicitManualContext
        ? "当前自动调节已停止，升温或温度失效都不会自动重新开扇；需要你明确开启。"
        : "后台只在控制温度低于 60°C 且状态正常时执行；达到 85°C 或监控失效会自动开扇。");

    private async Task SetFanPowerAsync(bool poweredOn) =>
        await RunCommandAsync(async () =>
        {
            var response = await SendCheckedAsync(PipeOperations.PowerSet, new { poweredOn, confirmed = true });
            if (response.Data is not { } data ||
                !data.TryGetProperty("readbackConfirmed", out var verified) || verified.ValueKind != JsonValueKind.True ||
                !data.TryGetProperty("poweredOn", out var power) ||
                power.ValueKind is not (JsonValueKind.True or JsonValueKind.False) || power.GetBoolean() != poweredOn)
            {
                throw new InvalidOperationException("后台没有返回一致的设备确认，请刷新状态；不要重复点击开关。");
            }

            var lightingLinked = data.TryGetProperty("lightingLinked", out var linked) && linked.ValueKind == JsonValueKind.True;
            var lightingRestored = data.TryGetProperty("lightingRestored", out var restored) && restored.ValueKind == JsonValueKind.True;
            var offConfirmed = lightingLinked ? "设备已确认关闭并熄灯；" : "设备已确认关闭；";
            ActivityText.Text = (ExplicitManualContext
                ? poweredOn ? "设备已确认按保存的手动目标开启；自动调节仍暂停。" : offConfirmed + "不会因升温或温度失效自动重新开启。"
                : poweredOn ? "设备已确认开启，先以 100% 恢复散热。" : offConfirmed + "后台仍监测温度。")
                + (poweredOn && lightingRestored ? "已恢复关闭前的灯光选择。" : "");
        }, refreshAfterSuccess: true);

    private async void ManualModeButton_Click(object sender, RoutedEventArgs e) =>
        await RunCommandAsync(async () =>
        {
            await SendCheckedAsync(PipeOperations.ModeSet, new { mode = "manual" });
            _manualSpeedDirty = false;
            ActivityText.Text = SafetyProtectionDisabled ? "手动模式已保存；安全保护仍禁用，可明确调整转速。" : "已切换到手动控制；故障安全仍然有效。";
        }, refreshAfterSuccess: true);

    private void ManualSpeedSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        SyncManualInputFromSlider();
        if (ManualSpeedValueText is not null)
        {
            ManualSpeedValueText.Text = FanDisplay.Estimate((byte)Math.Clamp(Math.Round(e.NewValue), 10, 100));
        }

        if (!_renderingManualSpeed)
        {
            _manualSpeedDirty = true;
        }
        if (ApplyManualSpeedButton is not null && OverviewControlModeText is not null)
            UpdateControlButtonsForBusyState();
    }

    private async void ApplyManualSpeedButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_manualInputValid)
        {
            RenderManualInputValidation();
            return;
        }
        if (!ManualSpeedAvailable)
        {
            if (!_commandInProgress && !_disableSafetyInProgress && !_closing && !_closed)
                ShowPageFeedback(ControlBlockedReason);
            return;
        }
        var percent = checked((byte)Math.Round(ManualSpeedSlider.Value));
        var wasAutomatic = _currentMode == ControlMode.Auto;
        var requestPage = CurrentPageId;
        ++_manualStatusGeneration;
        // A later explicit Apply supersedes a wheel value still waiting locally.
        trayDebounce?.Stop();
        trayPendingPercent = null;
        trayWheel.Reset();
        await RunCommandAsync(async () =>
        {
            // The service commits the manual target and Auto -> Manual together.
            // Never precede this with ModeSet, which would create a second ordering point.
            var response = await SendCheckedAsync(PipeOperations.SpeedSet, new { percent });
            var confirmed = response.Data!.Value.Deserialize<ControllerStatus>(JsonOptions);
            if (confirmed?.Fan is null || confirmed.Device is null || confirmed.HardwareSafety is null ||
                confirmed.Thermal is null || confirmed.FailSafe is null)
                throw new InvalidOperationException("后台没有返回完整的手动控制确认，请刷新状态。");
            if (confirmed.Fan.ConfiguredManualPercent is not (>= 10 and <= 100) ||
                confirmed.Fan.Mode is not (ControlMode.Manual or ControlMode.PureManual) ||
                wasAutomatic && confirmed.Fan.Mode != ControlMode.Manual)
                throw new InvalidOperationException("后台尚未确认本次手动设置；当前显示以最近确认状态为准。");
            if (confirmed.Fan.ConfiguredManualPercent != percent)
            {
                var actual = confirmed.Fan.ConfiguredManualPercent.Value;
                if (!confirmed.Device.Connected || confirmed.HardwareSafety.Poisoned ||
                    confirmed.Fan.ReportedControlPercent != actual || confirmed.Fan.PoweredOn != true ||
                    confirmed.LastSuccessfulRead is not { } read || read > DateTimeOffset.UtcNow ||
                    DateTimeOffset.UtcNow - read >= TimeSpan.FromSeconds(10))
                    throw new InvalidOperationException("后台返回了不同目标，但没有有效设备确认；原输入保留，请刷新状态。");
                _manualSpeedDirty = true;
                RenderStatus(confirmed);
                ActivityText.Text = $"设备最终为 {actual}%，" +
                    (confirmed.Fan.Mode == ControlMode.PureManual ? "保持纯手动；" : "已转手动；") +
                    $"原输入 {percent}% 未确认。" + (SafetyProtectionDisabled ? "安全保护仍禁用。" : "") +
                    "当前输入保留为未应用草稿。";
                ShowPageFeedback(ActivityText.Text, requestPage);
                return;
            }
            var draftChanged = !_manualInputValid || Math.Round(ManualSpeedSlider.Value) != percent;
            _manualSpeedDirty = draftChanged;
            RenderStatus(confirmed);
            ActivityText.Text = $"手动目标 {percent}% 已确认。" +
                (confirmed.Fan.Mode == ControlMode.PureManual ? "保持纯手动。" : "当前为手动控制。") +
                (SafetyProtectionDisabled ? "安全保护仍禁用。" : "") +
                (confirmed.Fan.ReportedControlPercent == percent ? "" : "实际转速以后台回读为准。") +
                (draftChanged ? "当前输入还有未应用修改。" : "");
        }, refreshAfterSuccess: true);
    }

    private async void ExportDiagnosticsButton_Click(object sender, RoutedEventArgs e) =>
        await RunCommandAsync(async () =>
        {
            var dialog = new SaveFileDialog
            {
                Title = "导出脱敏诊断",
                FileName = $"V12UltraController-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip",
                DefaultExt = ".zip",
                Filter = "ZIP 诊断包 (*.zip)|*.zip|All files (*.*)|*.*",
                AddExtension = true,
                OverwritePrompt = false,
            };
            if (dialog.ShowDialog(this) != true)
            {
                return;
            }

            var response = await SendCheckedAsync(PipeOperations.DiagnosticsExport, new { });
            try
            {
                await _diagnosticsArchiveWriter.WriteAsync(
                    dialog.FileName,
                    response.Data!.Value,
                    _windowLifetime.Token);
            }
            catch (DiagnosticsArchiveDestinationExistsException)
            {
                throw;
            }
            catch (UnauthorizedAccessException exception)
            {
                throw new LocalDiagnosticsWriteException(
                    "当前用户无权写入所选诊断文件位置；请选择有写入权限的目录。",
                    exception);
            }
            catch (IOException exception)
            {
                throw new LocalDiagnosticsWriteException(
                    $"诊断数据已从服务取得，但无法写入所选文件：{exception.Message}",
                    exception);
            }
            ActivityText.Text = $"脱敏诊断已由设置程序以当前用户权限写入：{dialog.FileName}";
        });

    private async void LoadCurveButton_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmCurveReplacement()) return;
        await LoadCurveAsync(showErrors: true);
    }

    private void AddCurvePointButton_Click(object sender, RoutedEventArgs e)
    {
        if (CurvePoints.Count >= 20)
        {
            ShowWarning("曲线最多包含 20 个点。");
            return;
        }

        var last = CurvePoints.LastOrDefault();
        var temperature = last is null ? 0 : Math.Min(100, last.TemperatureCelsius + 5);
        var fan = last?.FanPercent ?? 36;
        CurvePoints.Add(new CurvePointRow(temperature, fan));
        CurveGrid.SelectedIndex = CurvePoints.Count - 1;
        CurveGrid.ScrollIntoView(CurvePoints[^1]);
    }

    private void RemoveCurvePointButton_Click(object sender, RoutedEventArgs e)
    {
        if (CurveGrid.SelectedItem is not CurvePointRow selected)
        {
            ShowWarning("请先选中要删除的曲线点。");
            return;
        }

        if (CurvePoints.Count <= 2)
        {
            ShowWarning("曲线至少需要 2 个点。");
            return;
        }

        CurvePoints.Remove(selected);
    }

    private void ResetCurveButton_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmCurveReplacement()) return;
        ResetCurveToDefault();
        RenderCurveDraft();
        ActivityText.Text = "已恢复默认曲线草稿；点击“保存曲线”后才写入后台。";
    }

    private async void ApplyCurveButton_Click(object sender, RoutedEventArgs e) =>
        await ApplyCurveDraftAsync();

    private Task ApplyCurveDraftAsync() => RunCommandAsync(async () =>
        {
            var validationError = ValidateCurve();
            if (validationError is not null)
            {
                throw new InvalidOperationException(validationError);
            }

            var points = CurvePoints.Select(static point => new
            {
                temperatureCelsius = point.TemperatureCelsius,
                fanPercent = checked((byte)point.FanPercent),
            }).ToArray();
            var submittedFingerprint = CurveFingerprint();
            _curveReadSequence++;
            _curveReadCancellation?.Cancel();
            _curveReadCancellation = null;
            _curveReading = false;
            UpdateCurveReadAvailability();
            await SendCheckedAsync(PipeOperations.CurveSet, new { points });
            AcceptCurveBaseline(submittedFingerprint);
            ActivityText.Text = CurveDraftChanged ? "提交的曲线已保存；当前草稿另有未保存修改。" :
                _currentMode == ControlMode.Auto && !SafetyProtectionDisabled ? "曲线已保存，自动模式将按新曲线调节。" :
                "曲线已保存；当前控制模式与保护开关保持原状。";
        });

    private async void AttendedCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        var attended = AttendedCheckBox.IsChecked == true;
        UpdateCommissionButtonsForBusyState();
        if (!attended)
        {
            AttendedCheckBox.IsEnabled = false;
            try
            {
                await AbortCommissionSessionBestEffortAsync("取消看护确认");
            }
            finally
            {
                // Removing this window's consent does not prove that the
                // service's (possibly another client's) session has ended.
                // Preserve the remote projection until the next status read.
                ClearLocalCommissionSession();
                UpdateCommissionButtonsForBusyState();
                AttendedCheckBox.IsEnabled = !_commandInProgress && !_closing;
            }
        }
    }

    private async void ProbeButton_Click(object sender, RoutedEventArgs e) =>
        await RunCommandAsync(async () =>
        {
            RequireAttended();
            await AbortCommissionSessionBestEffortAsync("重新探测");
            ResetCommissionSession();
            var response = await SendCheckedAsync(PipeOperations.CommissionProbe, new { attended = true });
            _commissionSessionId = ReadStringProperty(response.Data!.Value, "sessionId")
                ?? throw new InvalidDataException("服务未返回 commissioning sessionId。");
            _serviceCommissioningActive = true;
            _serviceCommissioningSessionId = _commissionSessionId;
            _serviceCommissioningStage = "Probed";
            _serviceCommissioningExpiresAt = ReadDateTimeOffsetProperty(response.Data.Value, "expiresAt");
            RenderCommissioningSessionSummary();
            UpdateCommissionButtonsForBusyState();
            ActivityText.Text = "设备身份验证通过；尚未执行任何接管变更。";
        }, refreshAfterSuccess: true);

    private async void IdempotentButton_Click(object sender, RoutedEventArgs e) =>
        await RunCommandAsync(async () =>
        {
            var sessionId = RequireCommissionSession();
            IdempotentResultText.Text = "正在验证等值写回…";
            TestResultText.Text = "等待等值写回通过";
            CommitResultText.Text = "尚未接管";
            await SendCheckedAsync(PipeOperations.CommissionIdempotent, new { attended = true, sessionId });
            _serviceCommissioningStage = "IdempotentVerified";
            IdempotentResultText.Text = "等值写回与再读已通过，保留字节一致。";
            UpdateCommissionButtonsForBusyState();
            ActivityText.Text = "等值写回验证通过。";
        }, refreshAfterSuccess: true);

    private async void TestButton_Click(object sender, RoutedEventArgs e) =>
        await RunCommandAsync(async () =>
        {
            var sessionId = RequireCommissionSession();
            TestResultText.Text = "正在执行服务端受限分级测试…";
            CommitResultText.Text = "尚未接管";
            var response = await SendCheckedAsync(PipeOperations.CommissionTest, new { attended = true, sessionId });
            var skipped = response.Data is { } testData &&
                testData.TryGetProperty("lowerSpeedStagesSkipped", out var skippedValue) &&
                skippedValue.ValueKind == JsonValueKind.True;
            _serviceCommissioningStage = "TestVerified";
            TestResultText.Text = skipped ? "100% 回读验证通过；因温度较高，未执行降速测试。可以正式接管。" : "50% → 70% → 100% 分级测试已通过。";
            UpdateCommissionButtonsForBusyState();
            ActivityText.Text = "接管前验证通过；尚未接管 Myth.Cool 任务。";
        }, refreshAfterSuccess: true);

    private async void CommitButton_Click(object sender, RoutedEventArgs e) =>
        await RunCommandAsync(async () =>
        {
            var sessionId = RequireCommissionSession();
            var confirmation = MessageBox.Show(
                "服务将备份并禁用 \\Myth.Cool 计划任务，然后成为散热器的唯一写入者。\n\n确定正式接管吗？",
                "确认接管",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);
            if (confirmation != MessageBoxResult.Yes)
            {
                return;
            }

            await SendCheckedAsync(PipeOperations.CommissionCommit, new { attended = true, sessionId });
            ResetCommissionSession();
            CommitResultText.Text = "正式接管已完成。";
            ActivityText.Text = "风序 · FanOrder 已成为唯一写入者。";
        }, refreshAfterSuccess: true);

    private async void RollbackButton_Click(object sender, RoutedEventArgs e) =>
        await RunCommandAsync(async () =>
        {
            if (_lastStatus is null)
            {
                throw new InvalidOperationException(
                    "尚未取得服务状态；界面不会在无法确认强制卸载恢复状态时执行回滚。");
            }

            var forcedRecovery = _lastStatus?.ForcedUninstallRecovery;
            var forcedRecoveryRequired = forcedRecovery?.Required == true;
            var attended = ForcedRecoveryAttendedCheckBox.IsChecked == true;
            if (forcedRecoveryRequired)
            {
                if (forcedRecovery?.MarkerValid != true)
                {
                    throw new InvalidOperationException(
                        "强制卸载恢复标记无法验证；界面不会删除或绕过该标记。");
                }

                if (!attended)
                {
                    throw new InvalidOperationException(
                        "强制卸载恢复前必须显式勾选“我在电脑旁并会观察设备”。");
                }
            }

            var startMythCool = !forcedRecoveryRequired && StartMythCoolCheckBox.IsChecked == true;
            const string recoveryWarning =
                "\n\n若计划任务变更超时或结果无法确认，服务会保留恢复 journal 并阻止继续控制；" +
                "单纯重启不会清除该状态，必须有人看护地核对并恢复计划任务。";
            var confirmation = MessageBox.Show(
                startMythCool
                    ? "将尽力写入 100%，释放设备并恢复原计划任务状态；仅当服务确认存在可恢复且原先启用的任务时，才会立即启动 Myth.Cool。" +
                      recoveryWarning
                    : "将尽力写入 100%，释放设备，并恢复接管前的 Myth.Cool 任务状态。" +
                      recoveryWarning,
                "确认回滚",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);
            if (confirmation != MessageBoxResult.Yes)
            {
                return;
            }

            var response = await SendCheckedAsync(
                PipeOperations.CommissionRollback,
                new { startMythCool, installer = false, attended });
            var restoredTask = ReadRequiredBooleanProperty(response.Data!.Value, "restoredTask");
            var startedMythCool = ReadRequiredBooleanProperty(response.Data.Value, "startedMythCool");
            ResetCommissionSession();
            ActivityText.Text = restoredTask switch
            {
                false => "服务确认当前未接管：没有恢复计划任务，Myth.Cool 也未启动。",
                true when startedMythCool => "已恢复接管前的计划任务状态，并确认已请求启动 Myth.Cool；请核对其运行状态。",
                _ => "已恢复接管前的计划任务状态；Myth.Cool 未启动。",
            };
            if (startMythCool && !startedMythCool)
            {
                ShowWarning("服务没有启动 Myth.Cool。请先核对回滚结果、计划任务状态和诊断日志，再决定是否人工启动。");
            }
        }, refreshAfterSuccess: true);

    private void ForcedRecoveryAttendedCheckBox_Changed(object sender, RoutedEventArgs e) =>
        UpdateCommissionButtonsForBusyState();

    private async void RecoverTaskTransitionButton_Click(object sender, RoutedEventArgs e) =>
        await RunCommandAsync(async () =>
        {
            RequireAttended();
            var confirmation = MessageBox.Show(
                "此操作只适用于服务已经保留的 TaskDisableInProgress 或 TaskRestoreInProgress journal。\n\n" +
                "服务不会执行、启用、禁用或启动任何计划任务；它只读核验当前任务的精确 XML、启用状态和本地接管状态。" +
                "若证据只能证明恢复命令未生效，服务可能把本地状态补偿回自研控制器所有权。\n\n继续吗？",
                "确认有人看护的任务恢复",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);
            if (confirmation != MessageBoxResult.Yes)
            {
                return;
            }

            var response = await SendCheckedAsync(
                PipeOperations.CommissionRecoverTaskTransition,
                new { attended = true });
            var action = ReadStringProperty(response.Data!.Value, "action")
                ?? throw new InvalidDataException("服务未返回任务恢复动作。");
            TaskTransitionRecoveryResultText.Text = action switch
            {
                "commitAbandonedOriginalState" => "已证明禁用未发生：保留原任务状态，接管 journal 已安全清除。",
                "commitCompleted" => "已证明任务处于预期禁用状态：服务已完成接管的本地持久化。",
                "rollbackCompleted" => "已证明任务精确恢复到备份状态：回滚 journal 已完成。Myth.Cool 未启动。",
                "rollbackCompensatedToController" => "已证明恢复未生效且任务仍安全禁用：服务已补偿回自研控制器所有权。",
                _ => throw new InvalidDataException($"服务返回未知任务恢复动作：{action}。"),
            };
            ActivityText.Text = TaskTransitionRecoveryResultText.Text;
        }, refreshAfterSuccess: true);

    private void ShutdownAttendedCheckBox_Changed(object sender, RoutedEventArgs e) =>
        UpdateShutdownButtonsForBusyState();

    private async void QualifyShutdownButton_Click(object sender, RoutedEventArgs e) =>
        await RunCommandAsync(async () =>
        {
            if (ShutdownAttendedCheckBox.IsChecked != true)
            {
                throw new InvalidOperationException(
                    "必须确认正在电脑旁看护、低负载、低于 60°C 且 Myth.Cool 已退出。");
            }

            var confirmation = MessageBox.Show(
                "服务将执行 GET → ON 100% → GET → OFF → GET → ON 100% → GET。\n\n" +
                "风扇会短暂停止；进入 OFF 后，即使关闭窗口或取消请求，服务也会继续到最终 ON 100% 精确回读。" +
                "成功后只生成 2 分钟的内存资格，不会启用关机关闭；必须再进行一次明确确认。\n\n" +
                "Windows Restart 之后也可能短暂关闭；Fast Startup、睡眠和休眠不受保证。\n\n" +
                "请确认你仍在电脑旁、系统低负载且温度低于 60°C。继续吗？",
                "确认关机关闭资格测试",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);
            if (confirmation != MessageBoxResult.Yes)
            {
                return;
            }

            var response = await SendCheckedAsync(
                PipeOperations.ShutdownQualify,
                new { attended = true },
                _windowLifetime.Token,
                CommissioningPipeTimeout);
            var shutdown = ReadShutdownResult(response.Data!.Value);
            if (shutdown.Enabled || shutdown.Qualified || !shutdown.Pending ||
                string.IsNullOrWhiteSpace(shutdown.PendingQualificationId))
            {
                throw new InvalidDataException(
                    "服务没有返回未启用、短时且可明确确认的关机资格状态。");
            }

            ActivityText.Text =
                $"资格测试通过，当前风扇已精确恢复 ON 100%。尚未启用；请在 {FormatTimestamp(shutdown.PendingExpiresAt)} 前点击第二个按钮。";
        }, refreshAfterSuccess: true);

    private async void EnableShutdownButton_Click(object sender, RoutedEventArgs e) =>
        await RunCommandAsync(async () =>
        {
            if (ShutdownAttendedCheckBox.IsChecked != true)
            {
                throw new InvalidOperationException("确认启用时仍必须人在电脑旁看护。");
            }

            var shutdownSnapshot = _lastStatus?.Shutdown;
            if (shutdownSnapshot is not { Pending: true } ||
                !Guid.TryParse(shutdownSnapshot.PendingQualificationId, out var qualificationId) ||
                qualificationId == Guid.Empty)
            {
                throw new InvalidOperationException("没有可确认的短时资格；请重新运行资格测试。");
            }

            var confirmation = MessageBox.Show(
                "你必须已经亲眼观察到风扇在资格测试后恢复运行。\n\n" +
                "确认后服务仍会重新检查身份、所有权、冲突、策略与低于 60°C 的新鲜温度，" +
                "然后才保存关机关闭。Windows Restart 也可能短暂 OFF；Fast Startup、睡眠和休眠不保证。\n\n" +
                "确认已观察恢复并启用吗？",
                "明确启用关机关闭",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);
            if (confirmation != MessageBoxResult.Yes)
            {
                return;
            }

            var response = await SendCheckedAsync(
                PipeOperations.ShutdownEnable,
                new { qualificationId, attended = true },
                _windowLifetime.Token,
                CommissioningPipeTimeout);
            var shutdown = ReadShutdownResult(response.Data!.Value);
            if (!shutdown.Enabled || !shutdown.Qualified ||
                !shutdown.MatchesCurrentHandle || shutdown.Pending)
            {
                throw new InvalidDataException(
                    "服务没有确认已启用、已验证且与当前独占句柄匹配的关机资格。");
            }

            ShutdownAttendedCheckBox.IsChecked = false;
            ActivityText.Text = "关机关闭已明确启用；资格测试与启用是两个独立的人工操作。";
        }, refreshAfterSuccess: true);

    private async void DisableShutdownButton_Click(object sender, RoutedEventArgs e) =>
        await RunCommandAsync(async () =>
        {
            var confirmation = MessageBox.Show(
                "这会原子清除关机关闭资格并恢复 KeepAt100。不会发送任何 HID 命令。\n\n确定禁用吗？",
                "禁用关机关闭",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);
            if (confirmation != MessageBoxResult.Yes)
            {
                return;
            }

            var response = await SendCheckedAsync(PipeOperations.ShutdownDisable, new { });
            var shutdown = ReadShutdownResult(response.Data!.Value);
            if (shutdown.Enabled || shutdown.Qualified || shutdown.Pending ||
                !string.Equals(shutdown.Behavior, "keepAt100", StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "服务没有确认关机策略已恢复为未验证的 KeepAt100。请刷新状态并导出诊断。");
            }

            ShutdownAttendedCheckBox.IsChecked = false;
            ActivityText.Text = "关机关闭已禁用；KeepAt100 已原子保存，未访问 HID。";
        }, refreshAfterSuccess: true);

    private async void ShutdownAndTurnOffButton_Click(object sender, RoutedEventArgs e) =>
        await RunCommandAsync(async () =>
        {
            var shutdown = _lastStatus?.Shutdown;
            if (shutdown is not { Enabled: true, Qualified: true, MatchesCurrentHandle: true })
                throw new InvalidOperationException("请先完成资格测试并明确启用关机停扇，且确认当前设备句柄匹配。");

            var confirmation = MessageBox.Show(
                "Windows 将开始关机。后台会先恢复并确认风扇安全状态，再执行已启用的关机停扇动作。\n\n" +
                "如果条件、设备身份或温度不满足，服务会保持风扇运行并放弃停扇。继续吗？",
                "关机并关闭风扇",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);
            if (confirmation != MessageBoxResult.Yes)
                return;

            if (!_systemShutdownRequester.TryRequestShutdown(out var error))
                throw new Win32Exception(error ?? "Windows 拒绝了关机请求。");

            ActivityText.Text = "已请求 Windows 关机；后台正在按已验证策略处理风扇。";
            await Task.CompletedTask;
        }, refreshAfterSuccess: false);

    private async Task RefreshStatusAsync(
        bool showErrors,
        bool waitForExistingRefresh = false,
        bool allowDuringCommand = false)
    {
        if (_disableSafetyInProgress || _commandInProgress && !allowDuringCommand)
        {
            return;
        }

        var entered = waitForExistingRefresh
            ? await WaitForRefreshGateAsync()
            : await _refreshGate.WaitAsync(0);
        if (!entered)
        {
            return;
        }

        var requestGeneration = _controlRequestGeneration;
        var manualGeneration = _manualStatusGeneration;
        try
        {
            var response = await SendCheckedAsync(PipeOperations.StatusGet, new { });
            if (requestGeneration != _controlRequestGeneration || manualGeneration != _manualStatusGeneration) return;
            var status = response.Data!.Value.Deserialize<ControllerStatus>(JsonOptions)
                ?? throw new InvalidDataException("服务返回的状态为 null。");
            RenderStatus(status);
            await AutoTakeoverReleasedControlAsync();
            SetConnectionState(true, "本地服务已连接");
            LastRefreshText.Text = $"最后刷新 {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception exception) when (IsExpectedClientException(exception))
        {
            if (requestGeneration != _controlRequestGeneration || manualGeneration != _manualStatusGeneration) return;
            _lastStatus = null;
            _currentMode = null;
            ForcedRecoveryAttendedCheckBox.IsChecked = false;
            SetConnectionState(false, "本地服务不可用");
            DeviceStateText.Text = "状态未知";
            DeviceStateText.Foreground = WarningBrush;
            DeviceIdentityText.Text = "无法确认设备连接与控制所有权；不要启动另一个控制器。";
            ControlTemperatureText.Text = "—";
            TemperatureDetailText.Text = "服务断连，温度与新鲜度未知";
            ThermalTroubleshootingBorder.Visibility = Visibility.Collapsed;
            ThermalTroubleshootingText.Text = string.Empty;
            FanPercentText.Text = "—";
            FanModeText.Text = "目标、回读和开关状态未知";
            FailSafeStateText.Text = "无法确认";
            FailSafeStateText.Foreground = WarningBrush;
            FailSafeReasonText.Text = "未收到服务状态；不能确认安全目标已写入。";
            ServiceStateText.Text = "无法连接";
            CommissionedText.Text = "未知";
            LastReadText.Text = "—";
            LastWriteText.Text = "—";
            LastRefreshText.Text = "刷新失败（非实时状态）";
            ShutdownStateText.Text = "服务状态不可用";
            ShutdownStateText.Foreground = WarningBrush;
            ShutdownQualificationText.Text = "无法确认关机策略；不要假定已启用或已禁用。";
            UpdateControlButtonsForBusyState();
            UpdateCommissionButtonsForBusyState();
            UpdateShutdownButtonsForBusyState();
            ActivityText.Text = ExplainException(exception);
            if (showErrors)
            {
                ShowWarning(ExplainException(exception));
            }
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private async Task<bool> WaitForRefreshGateAsync()
    {
        try
        {
            await _refreshGate.WaitAsync(_windowLifetime.Token);
            return true;
        }
        catch (OperationCanceledException) when (_closing)
        {
            return false;
        }
    }

    private async Task LoadCurveAsync(bool showErrors)
    {
        if (!showErrors && CurveDraftChanged) return;
        if (_curveReading || _closing || _closed) return;
        var epoch = _pageReadEpoch;
        var generation = _controlRequestGeneration;
        var sequence = ++_curveReadSequence;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_windowLifetime.Token);
        _curveReadCancellation = cancellation;
        _curveReading = true;
        if (showErrors) { _pageFeedback.Remove("fan"); RenderPageFeedback(); }
        UpdateCurveReadAvailability();
        var readStarted = CurveFingerprint();
        try
        {
            var response = await SendCheckedAsync(PipeOperations.CurveGet, new { }, cancellation.Token);
            if (_closing || _closed || sequence != _curveReadSequence || epoch != _pageReadEpoch || generation != _controlRequestGeneration) return;
            var pointsElement = response.Data!.Value;
            if (TryGetProperty(pointsElement, "curve", out var curveElement))
            {
                pointsElement = curveElement;
            }

            if (!TryGetProperty(pointsElement, "points", out var pointArray)
                || pointArray.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException("服务未返回有效曲线点。");
            }

            var rows = new List<CurvePointRow>();
            foreach (var item in pointArray.EnumerateArray())
            {
                if (!TryReadDouble(item, "temperatureCelsius", out var temperature))
                {
                    throw new InvalidDataException("曲线点缺少温度。");
                }

                if (!TryReadInt32(item, "fanPercent", out var percent))
                {
                    throw new InvalidDataException("曲线点缺少风扇百分比。");
                }

                rows.Add(new CurvePointRow(temperature, percent));
            }

            if (readStarted != CurveFingerprint())
            {
                AcceptCurveBaseline(CurveFingerprint(rows));
                ActivityText.Text = "已读取后台曲线；保留请求期间编辑的草稿。";
                return;
            }
            CurvePoints.Clear();
            foreach (var row in rows)
            {
                CurvePoints.Add(row);
            }

            AcceptCurveBaseline();
            ActivityText.Text = "已从服务读取当前曲线。";
        }
        catch (Exception exception) when (IsExpectedClientException(exception))
        {
            if (_closing || _closed || sequence != _curveReadSequence || epoch != _pageReadEpoch || generation != _controlRequestGeneration) return;
            ActivityText.Text = ExplainException(exception);
            if (showErrors)
            {
                ShowPageFeedback(ExplainException(exception) + " 草稿已保留，可重新读取。", "fan");
            }
        }
        finally
        {
            if (ReferenceEquals(_curveReadCancellation, cancellation))
            {
                _curveReadCancellation = null;
                _curveReading = false;
                UpdateCurveReadAvailability();
            }
        }
    }

    private Task<PipeResponse> SendCheckedAsync<TPayload>(string operation, TPayload payload) =>
        SendCheckedAsync(operation, payload, _windowLifetime.Token);

    private async Task<PipeResponse> SendCheckedAsync<TPayload>(
        string operation,
        TPayload payload,
        CancellationToken cancellationToken,
        TimeSpan? timeoutOverride = null)
    {
        var request = PipeRequest.Create(operation, payload);
        var requestGeneration = _controlRequestGeneration;
        var timeout = timeoutOverride ?? (operation.StartsWith("commission.", StringComparison.Ordinal) ||
                                           operation == PipeOperations.ShutdownQualify ||
                                           operation == PipeOperations.ShutdownEnable
            ? CommissioningPipeTimeout
            : PipeTimeout);
        var response = await _client.SendAsync(request, timeout, cancellationToken);
        if (requestGeneration != _controlRequestGeneration)
            throw new OperationCanceledException("此请求已被优先禁用安全保护取代，旧响应不再用于当前状态。");
        if (response.ApiVersion != PipeProtocol.ApiVersion)
        {
            throw new InvalidDataException($"服务 API 版本 {response.ApiVersion} 与客户端版本 {PipeProtocol.ApiVersion} 不兼容。");
        }

        if (!string.Equals(request.RequestId, response.RequestId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("服务响应的 requestId 与请求不匹配。");
        }

        if (!response.Ok)
        {
            throw new PipeApiException(
                response.Error?.Code ?? PipeErrorCodes.InternalError,
                response.Error?.Message ?? "服务返回未说明的错误。");
        }

        if (response.Data is null)
        {
            throw new InvalidDataException("服务成功响应缺少 data。");
        }

        return response;
    }

    private async Task RunCommandAsync(Func<Task> action, bool refreshAfterSuccess = false, Action<string>? reportError = null)
    {
        if (_commandInProgress || _disableSafetyInProgress || _closing || _closed)
        {
            return;
        }

        var succeeded = false;
        InvalidateQuickLighting();
        var requestGeneration = _controlRequestGeneration;
        var requestPage = CurrentPageId;
        _pageFeedback.Remove(requestPage);
        RenderPageFeedback();
        _commandInProgress = true;
        ActivityText.Text = "正在等待本地服务…";
        UpdateControlButtonsForBusyState();
        UpdateCommissionButtonsForBusyState();
        UpdateShutdownButtonsForBusyState();
        try
        {
            await action();
            succeeded = true;
        }
        catch (Exception exception) when (IsExpectedClientException(exception))
        {
            if (requestGeneration != _controlRequestGeneration) return;
            if (_closing && exception is OperationCanceledException)
            {
                return;
            }

            if (exception is PipeApiException { Code: PipeErrorCodes.DeviceStateChanged or PipeErrorCodes.CommandReadbackMismatch })
            {
                await RefreshStatusAsync(showErrors: false, waitForExistingRefresh: true, allowDuringCommand: true);
                if (_closing || requestGeneration != _controlRequestGeneration) return;
                ActivityText.Text = exception.Message;
                if (reportError is not null) reportError(exception.Message);
                else ShowPageFeedback(exception.Message, requestPage);
            }
            else
            {
                ActivityText.Text = ExplainException(exception);
                if (reportError is not null) reportError(ExplainException(exception));
                else ShowPageFeedback(ExplainException(exception), requestPage);
            }
        }
        finally
        {
            if (succeeded && refreshAfterSuccess && !_closing && requestGeneration == _controlRequestGeneration)
            {
                await RefreshStatusAsync(
                    showErrors: true,
                    waitForExistingRefresh: true,
                    allowDuringCommand: true);
            }

            _commandInProgress = false;
            UpdateControlButtonsForBusyState();
            UpdateCommissionButtonsForBusyState();
            UpdateShutdownButtonsForBusyState();
            if (requestGeneration != _controlRequestGeneration && _prioritySafetyMessage is not null)
                ActivityText.Text = _prioritySafetyMessage;
        }
    }

    private void RenderStatus(ControllerStatus status)
    {
        _startupDisconnected = false;
        trayStatusReceived = DateTimeOffset.UtcNow;
        _lastStatus = status;
        _currentMode = status.Fan.Mode;
        ObservePageServiceConnection();
        RenderHistory(status);
        ServiceStateText.Text = $"{status.ServiceState}  /  owner: {status.Owner}";
        CommissionedText.Text = status.Commissioned ? "已接管" : "未接管（NotCommissioned）";
        if (status.HardwareSafety.Poisoned)
        {
            var poisonReason = string.IsNullOrWhiteSpace(status.HardwareSafety.Reason)
                ? "未提供"
                : status.HardwareSafety.Reason;
            DeviceStateText.Text = "硬件所有权未确认";
            DeviceStateText.Foreground = WarningBrush;
            DeviceIdentityText.Text =
                $"句柄可能仍保留，勿启动 Myth.Cool。\nreason {poisonReason} / 自 {FormatTimestamp(status.HardwareSafety.Since)}";
        }
        else
        {
            var awaitingCommission = !status.Commissioned && status.ServiceState == "NotCommissioned";
            var awaitingExplicitRead = status.Commissioned && ExplicitManualContext && !status.Device.Connected;
            DeviceStateText.Text = status.Device.Connected ? "已连接" : awaitingExplicitRead ? "待操作确认" : awaitingCommission ? "待接管" : "未连接";
            DeviceStateText.Foreground = status.Device.Connected ? ConnectedBrush : WarningBrush;
            DeviceIdentityText.Text = status.Device.Connected
                ? $"VID {status.Device.VendorId} / PID {status.Device.ProductId}\nfirmware {status.Device.Firmware} / 协议{(status.Device.ProtocolCompatible ? "兼容" : "不兼容")}"
                : awaitingExplicitRead ? "后台已连接，但当前没有设备回读。下一次明确手动操作时，由后台确认连接和控制权；这里不代表已确认断连。"
                : awaitingCommission ? "后台已连接；请到“配对、接管与回滚”完成接管，设备尚未持续监测" : "尚未探测或设备当前不可用";
        }
        ControlTemperatureText.Text = FormatTemperature(status.Thermal.ControlCelsius);
        var gpu = status.Thermal.GpuCelsius.Count == 0
            ? "—"
            : string.Join(", ", status.Thermal.GpuCelsius.Select(FormatTemperature));
        var thermalFreshness = status.Thermal.LastUpdated is null
            ? "未更新"
            : $"最近采样尝试 {status.Thermal.LastUpdated.Value.ToLocalTime():HH:mm:ss}";
        TemperatureDetailText.Text = $"CPU {FormatTemperature(status.Thermal.CpuCelsius)}  |  GPU {gpu}\n{(status.Thermal.Healthy ? "信号健康" : "信号无效")} / {thermalFreshness}";
        ThermalTroubleshootingBorder.Visibility = status.Thermal.Healthy ? Visibility.Collapsed : Visibility.Visible;
        ThermalTroubleshootingText.Text = status.Thermal.Healthy ? string.Empty : ThermalTroubleshooting.Chinese;
        FanPercentText.Text = ExplicitManualContext ? status.Fan.ConfiguredManualPercent is { } manualTarget ? $"{manualTarget}%" : "—" : $"{status.Fan.RequestedPercent}%";
        FanTargetLabelText.Text = ExplicitManualContext ? "手动目标" : "风扇目标";
        var freshFanReadback = status.Device.Connected && !status.HardwareSafety.Poisoned &&
            status.LastSuccessfulRead is { } read && read <= DateTimeOffset.UtcNow &&
            DateTimeOffset.UtcNow - read < TimeSpan.FromSeconds(10);
        FanReadbackText.Text = freshFanReadback ? FanDisplay.Readback(status.Fan.ReportedControlPercent, status.Fan.PoweredOn, true) : "设备回读：未知";
        var reported = status.Fan.ReportedControlPercent is null
            ? "回读 —"
            : $"回读 {status.Fan.ReportedControlPercent}%";
        var power = status.Fan.PoweredOn switch
        {
            true => "设备开启",
            false when status.Fan.OffRequested => "手动关闭",
            false => "设备关闭（异常）",
            null => "开关状态未知",
        };
        if (!status.Device.Connected || status.HardwareSafety.Poisoned)
        {
            reported = status.Fan.ReportedControlPercent is { } historicalPercent
                ? $"历史回读 {historicalPercent}%（{FormatTimestamp(status.LastSuccessfulRead)}）"
                : "无有效回读";
            power = "当前转速/开关状态未知";
        }
        var configuredManualPercent = status.Fan.ConfiguredManualPercent ?? status.Fan.RequestedPercent;
        var modeDescription = status.Fan.Mode == ControlMode.Auto ? SafetyProtectionDisabled ? "自动曲线（暂停）" : "自动曲线" :
            status.Fan.Mode == ControlMode.Released ? "已释放控制" : status.Fan.Mode == ControlMode.PureManual ? "纯手动" : "手动控制";
        FanModeText.Text =
            $"{modeDescription}  /  {reported}  /  {power}  /  手动设定 {configuredManualPercent}%";
        if (!_manualSpeedDirty)
        {
            _renderingManualSpeed = true;
            try
            {
                ManualSpeedSlider.Value = configuredManualPercent;
            }
            finally
            {
                _renderingManualSpeed = false;
            }
        }
        UpdateControlButtonsForBusyState();
        AutoModeButton.Background = status.Fan.Mode == ControlMode.Auto
            ? (Brush)FindResource("PrimaryBrush")
            : (Brush)FindResource("SurfaceRaisedBrush");
        ManualModeButton.Background = status.Fan.Mode == ControlMode.Manual
            ? (Brush)FindResource("PrimaryBrush")
            : (Brush)FindResource("SurfaceRaisedBrush");
        AutoModeButton.Foreground = (Brush)FindResource(status.Fan.Mode == ControlMode.Auto ? "PrimaryButtonTextBrush" : "TextPrimaryBrush");
        ManualModeButton.Foreground = (Brush)FindResource(status.Fan.Mode == ControlMode.Manual ? "PrimaryButtonTextBrush" : "TextPrimaryBrush");

        var recoveryBlocked = status.HardwareSafety.Poisoned ||
            status.AuditMutation is { Blocked: true } or { Pending: true } ||
            status.SafetyOperationAudit is { Blocked: true } or { Pending: true } ||
            status.ForcedUninstallRecovery is { Required: true };
        FailSafeStateText.Text = recoveryBlocked ? "恢复阻断" : status.FailSafe.Active ? "已激活" : "正常";
        FailSafeStateText.Foreground = status.FailSafe.Active || recoveryBlocked ? WarningBrush : ConnectedBrush;
        var failSafeDescription = status.FailSafe.Active
            ? $"{status.FailSafe.Reason}  /  安全目标 100%  /  自 {FormatTimestamp(status.FailSafe.Since)}"
            : recoveryBlocked ? "服务存在恢复屏障；不能确认安全目标 100% 已写入。" : "未检测到故障安全条件";
        FailSafeReasonText.Text = status.HardwareSafety.Poisoned
            ? $"{failSafeDescription}\n硬件所有权未确认：{status.HardwareSafety.Reason ?? "未提供"} / 自 {FormatTimestamp(status.HardwareSafety.Since)}"
            : failSafeDescription;
        LastReadText.Text = FormatTimestamp(status.LastSuccessfulRead);
        LastWriteText.Text = FormatTimestamp(status.LastSuccessfulWrite);
        RenderAuditMutation(status.AuditMutation);
        RenderSafetyOperationAudit(status.SafetyOperationAudit);
        RenderForcedUninstallRecovery(status.ForcedUninstallRecovery);
        SynchronizeCommissioningStatus(status.Commissioning);
        RenderShutdownStatus(status.Shutdown);
        if (status.Fan.Mode is ControlMode.PureManual or ControlMode.Released && !recoveryBlocked)
        {
            FailSafeStateText.Text = "自动调节已禁用";
            FailSafeStateText.Foreground = WarningBrush;
            FailSafeReasonText.Text = "温度保护不干预转速；仅明确操作写入。";
        }
        RenderSafetyProtectionState(status);
        UpdateTrayText();
    }

    private bool StateMutationAvailable => StateMutationAllowed(allowBusy: false);
    private bool StateMutationAllowed(bool allowBusy) => _lastStatus is not null &&
        _lastStatus.AuditMutation is not ({ Blocked: true } or { Pending: true }) &&
        _lastStatus.SafetyOperationAudit is not ({ Blocked: true } or { Pending: true }) &&
        _lastStatus.ForcedUninstallRecovery is not { Required: true } &&
        !_lastStatus.HardwareSafety.Poisoned && (allowBusy || !_commandInProgress) && !_disableSafetyInProgress && !_closing && !_closed &&
        (_lastStatus.Software is null || _lastStatus.Software.UiContractVersion == UiCompatibility.ContractVersion);

    private void UpdateControlButtonsForBusyState()
    {
        StartBackendButton.IsEnabled = _lastStatus is null && !_commandInProgress && !_closing;
        UpdateStartupSettingsAvailability();
        var powerAvailable = StateMutationAvailable && _lastStatus?.Commissioned == true &&
                             _lastStatus.Fan.ManualPowerSupported &&
                             _lastStatus.Device.Connected && !_serviceCommissioningActive;
        FanOnButton.IsEnabled = ExplicitManualAvailable || powerAvailable && _currentMode != ControlMode.Released;
        FanOffButton.IsEnabled = ExplicitManualAvailable || powerAvailable && _currentMode != ControlMode.Released &&
            _lastStatus?.FailSafe.Active == false && _lastStatus.Thermal.Healthy && _lastStatus.Thermal.ControlCelsius is < 60;
        AutoModeButton.IsEnabled = StateMutationAvailable && _currentMode != ControlMode.Released;
        AutoModeButton.Content = SafetyProtectionDisabled ? "自动曲线（暂停）" : "自动曲线";
        ManualModeButton.Content = _currentMode == ControlMode.Manual ? "手动控制 · 当前" : "手动控制";
        PureManualButton.IsEnabled = StateMutationAvailable && _lastStatus?.Commissioned == true && !_serviceCommissioningActive;
        RenderDisableSafetyProtectionButton();
        FanPowerHelpText.Text = _currentMode == ControlMode.Released ? "软件已释放控制，不再自动开扇；选择纯手动后可重新控制。"
            : SafetyAutomaticOutputStopped ? "安全保护已禁用：后台不会因温度或故障自动开扇。明确恢复保护后才会恢复自动控制。"
            : PureManualSelected ? "纯手动：关闭后不会自动开扇。窗口收进托盘；退出会释放控制。"
            : "普通模式：关闭仅限低温；升温或监控故障会自动开启。窗口收进托盘。";
        if (FanLightingLinkSupported && _currentMode != ControlMode.Released)
            FanPowerHelpText.Text = FanLightingLinkHelp + "\n" + FanPowerHelpText.Text;
        ControlProtectionText.Text = SafetyProtectionDisabled
            ? "安全保护已禁用；设置不会自动恢复。手动转速仍可调，恢复保护需单独确认。"
            : PureManualSelected
            ? "纯手动已保存：无温控、满速保护或定时重申；重启不自动写入。"
            : _currentMode == ControlMode.Released ? "软件已释放控制；可用实体按键。选择纯手动后可重新调速。"
            : "普通手动受温控保护；选择纯手动会停自动调节。";
        ManualModeButton.IsEnabled = StateMutationAvailable && _currentMode != ControlMode.Released;
        ManualSpeedSlider.IsEnabled = StateMutationAvailable;
        ApplyManualSpeedButton.IsEnabled = _manualInputValid && ManualSpeedAvailable;
        ApplyCurveButton.IsEnabled = StateMutationAvailable;
        RenderUsability();
        UpdatePageReadAvailability();
    }

    private void RenderAuditMutation(AuditMutationStatus? mutation)
    {
        var visible = mutation is { Blocked: true } or { Pending: true };
        AuditMutationBorder.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!visible)
        {
            AuditMutationText.Text = string.Empty;
            return;
        }

        AuditMutationText.Text =
            $"状态：{(mutation!.Blocked ? "已阻断" : "等待收尾")}\n" +
            $"事务：{mutation.TransactionId ?? "未提供"}\n" +
            $"事件 / 阶段：{mutation.EventName ?? "未知"} / {mutation.Stage ?? "未知"}\n" +
            $"原因：{mutation.Reason ?? "等待服务验证持久化状态与审计记录"}\n" +
            "在恢复完成前，服务拒绝新的状态变更和普通 HID 控制；请保留 ProgramData 并导出诊断。";
    }

    private void RenderSafetyOperationAudit(SafetyOperationAuditStatus? operation)
    {
        var visible = operation is { Blocked: true } or { Pending: true };
        SafetyOperationAuditBorder.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!visible)
        {
            SafetyOperationAuditText.Text = string.Empty;
            return;
        }

        var recovery = operation!.RequiresOn100Recovery
            ? "该操作涉及 HID；服务只有在精确接管身份、无 Myth.Cool 冲突并取得独占写租约后，才会尝试 GET→ON100→GET 恢复。"
            : "该操作仅涉及进程内会话；服务会保守记录无法重建的结果。";
        SafetyOperationAuditText.Text =
            $"状态：{(operation.Blocked ? "已阻断" : "等待收尾")}\n" +
            $"事务：{operation.TransactionId ?? "未提供"}\n" +
            $"事件 / 类型 / 阶段：{operation.EventName ?? "未知"} / " +
            $"{operation.Kind ?? "未知"} / {operation.Stage ?? "未知"}\n" +
            $"结果：{operation.Outcome ?? "尚未判定"}\n" +
            $"原因：{operation.Reason ?? "等待服务验证持久化状态与审计记录"}\n" +
            recovery + "\n请勿删除恢复文件、启动 Myth.Cool 或尝试关机停扇；请先导出脱敏诊断。";
    }

    private void RenderForcedUninstallRecovery(ForcedUninstallRecoveryStatus? recovery)
    {
        var required = recovery?.Required == true;
        ForcedUninstallRecoveryBorder.Visibility = required
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (!required)
        {
            ForcedUninstallRecoveryText.Text = string.Empty;
            ForcedRecoveryAttendedCheckBox.IsChecked = false;
            UpdateCommissionButtonsForBusyState();
            return;
        }

        StartMythCoolCheckBox.IsChecked = false;
        if (recovery?.MarkerValid != true)
        {
            ForcedRecoveryAttendedCheckBox.IsChecked = false;
        }

        ForcedUninstallRecoveryText.Text = recovery!.MarkerValid
            ? $"检测到有效恢复标记 {recovery.MarkerId ?? "未提供"}（{FormatTimestamp(recovery.Since)}）。\n" +
              "普通控制和所有 HID 访问已封锁。请留在电脑旁，使用下方“回滚”显式恢复并精确核对 " +
              "Myth.Cool 任务；该路径不会访问散热器，也不会自动启动 Myth.Cool。"
            : "恢复标记损坏、不可读或无法验证。服务已封锁所有 HID 和任务恢复操作，并保留原证据。\n" +
              $"原因：{recovery.Reason ?? "未提供"}\n请先人工修复/取证；界面不会删除该标记。";
        UpdateCommissionButtonsForBusyState();
    }

    private void RenderShutdownStatus(ShutdownStatus? shutdown)
    {
        if (shutdown is null)
        {
            ShutdownStateText.Text = "服务版本不支持";
            ShutdownStateText.Foreground = WarningBrush;
            ShutdownQualificationText.Text =
                "当前服务未返回版本化关机资格状态；本界面不会尝试启用。";
            UpdateShutdownButtonsForBusyState();
            return;
        }

        ShutdownStateText.Text = shutdown.Enabled
            ? "已启用：系统关机时关闭"
            : "已禁用：KeepAt100";
        ShutdownStateText.Foreground = shutdown.Enabled ? WarningBrush : ConnectedBrush;
        if (shutdown.Pending)
        {
            ShutdownQualificationText.Text =
                $"资格测试已通过，但关机关闭尚未启用。\n" +
                $"短时资格 {shutdown.PendingQualificationId ?? "未提供"}\n" +
                $"到期时间 {FormatTimestamp(shutdown.PendingExpiresAt)}；服务重启即失效。\n" +
                "请仅在亲眼观察到风扇已恢复 ON 100% 后进行第二步确认。";
        }
        else if (!shutdown.Qualified || shutdown.QualifiedIdentity is null)
        {
            ShutdownQualificationText.Text = "没有资格记录；关机时保持 100%。";
        }
        else
        {
            var identity = shutdown.QualifiedIdentity;
            ShutdownQualificationText.Text =
                $"资格时间 {FormatTimestamp(shutdown.QualifiedAt)}\n" +
                $"VID {identity.VendorId} / PID {identity.ProductId} / FW {identity.Firmware} / " +
                $"Usage {identity.UsagePage}:{identity.Usage} / Feature {identity.FeatureReportLength} / " +
                $"协议 {identity.ProtocolRevision}\n" +
                $"与当前独占句柄匹配：{(shutdown.MatchesCurrentHandle ? "是" : "否（当前不能安全执行关机关闭）")}";
        }

        UpdateShutdownButtonsForBusyState();
    }

    private void UpdateShutdownButtonsForBusyState()
    {
        var status = _lastStatus;
        var shutdown = status?.Shutdown;
        var eligibleSnapshot = StateMutationAvailable && status is not null &&
            status.Commissioned &&
            status.Device.Connected &&
            status.Device.ProtocolCompatible &&
            !status.FailSafe.Active &&
            !status.HardwareSafety.Poisoned &&
            status.Thermal.Healthy &&
            status.Thermal.ControlCelsius is < 60;
        ShutdownAttendedCheckBox.IsEnabled = !_commandInProgress && !_closing;
        QualifyShutdownButton.IsEnabled = !_commandInProgress && !_closing &&
            ShutdownAttendedCheckBox.IsChecked == true &&
            eligibleSnapshot &&
            shutdown is { Enabled: false, Pending: false };
        EnableShutdownButton.IsEnabled = !_commandInProgress && !_closing &&
            ShutdownAttendedCheckBox.IsChecked == true &&
            eligibleSnapshot &&
            shutdown is { Enabled: false, Pending: true } &&
            !string.IsNullOrWhiteSpace(shutdown.PendingQualificationId);
        DisableShutdownButton.IsEnabled = !_commandInProgress && !_closing &&
            shutdown is { Enabled: true } or { Qualified: true } or { Pending: true };
        ShutdownAndTurnOffButton.IsEnabled = !_commandInProgress && !_closing &&
            shutdown is { Enabled: true, Qualified: true, MatchesCurrentHandle: true };
    }

    private static ShutdownStatus ReadShutdownResult(JsonElement responseData)
    {
        if (!TryGetProperty(responseData, "shutdown", out var shutdownElement))
        {
            throw new InvalidDataException("服务响应缺少 shutdown 状态。");
        }

        return shutdownElement.Deserialize<ShutdownStatus>(JsonOptions)
            ?? throw new InvalidDataException("服务返回的 shutdown 状态为 null。");
    }

    private void ResetCurveToDefault()
    {
        var defaults = new (double Temperature, int Fan)[]
        {
            (0, 36), (30, 36), (40, 43), (50, 50), (60, 57),
            (70, 68), (80, 79), (85, 90), (90, 100), (100, 100),
        };
        CurvePoints.Clear();
        foreach (var point in defaults)
        {
            CurvePoints.Add(new CurvePointRow(point.Temperature, point.Fan));
        }
    }

    private string? ValidateCurve()
    {
        if (!CurveGrid.CommitEdit(DataGridEditingUnit.Cell, exitEditingMode: true)
            || !CurveGrid.CommitEdit(DataGridEditingUnit.Row, exitEditingMode: true))
        {
            return "当前曲线单元格未通过输入验证，不能提交。";
        }
        if (CurvePoints.Count is < 2 or > 20)
        {
            return "曲线必须包含 2–20 个点。";
        }

        double? previousTemperature = null;
        int? previousFan = null;
        for (var index = 0; index < CurvePoints.Count; index++)
        {
            var point = CurvePoints[index];
            if (!double.IsFinite(point.TemperatureCelsius) || point.TemperatureCelsius is < 0 or > 100)
            {
                return $"第 {index + 1} 个点的温度必须在 0–100°C。";
            }
            if (point.FanPercent is < 10 or > 100)
            {
                return $"第 {index + 1} 个点的风扇百分比必须在 10–100。";
            }
            if (previousTemperature is not null && point.TemperatureCelsius <= previousTemperature)
            {
                return "曲线温度必须严格递增。";
            }
            if (previousFan is not null && point.FanPercent < previousFan)
            {
                return "风扇百分比不能随温度升高而下降。";
            }

            previousTemperature = point.TemperatureCelsius;
            previousFan = point.FanPercent;
        }

        return null;
    }

    private void SetConnectionState(bool connected, string message)
    {
        if (!connected)
        {
            RenderHistory(null);
            InvalidateStartupConnection();
            InvalidatePageServiceConnection();
            _lightingSupported = false;
            _lightingBaseline = null;
            RenderLightingPreview();
            LightingStatusText.Text = "连接已断开，请重新读取灯光状态。";
            ApplyLightingButton.IsEnabled = false;
        }
        ConnectionIndicator.Fill = connected ? ConnectedBrush : DisconnectedBrush;
        ConnectionText.Text = message;
    }

    private void RequireAttended()
    {
        if (AttendedCheckBox.IsChecked != true)
        {
            throw new InvalidOperationException("必须确认正在设备旁看护后才能开始。");
        }
    }

    private string RequireCommissionSession()
    {
        RequireAttended();
        if (_commissionSessionId is null || !_serviceCommissioningActive ||
            !string.Equals(
                _commissionSessionId,
                _serviceCommissioningSessionId,
                StringComparison.OrdinalIgnoreCase) ||
            _serviceCommissioningExpiresAt is { } expiresAt && expiresAt <= DateTimeOffset.UtcNow)
        {
            throw new InvalidOperationException("配对会话不存在、已被服务安全终止或已经到期；请重新从探测开始。");
        }

        return _commissionSessionId;
    }

    private void ResetCommissionSession()
    {
        _serviceCommissioningActive = false;
        _serviceCommissioningSessionId = null;
        _serviceCommissioningStage = null;
        _serviceCommissioningExpiresAt = null;
        ClearLocalCommissionSession();
        UpdateCommissionButtonsForBusyState();
    }

    private void ClearLocalCommissionSession()
    {
        _commissionSessionId = null;
        ProbeResultText.Text = "尚未探测";
        IdempotentResultText.Text = "等待设备探测通过";
        TestResultText.Text = "等待等值写回通过";
        CommitResultText.Text = "尚未接管";
    }

    private void SynchronizeCommissioningStatus(CommissioningStatus? serviceStatus)
    {
        if (serviceStatus is null)
        {
            // Supports a brief mixed-version upgrade window. Responses from this
            // client still maintain the local projection until the new service is live.
            UpdateCommissionButtonsForBusyState();
            return;
        }

        var previouslyOwnedSession = _commissionSessionId;
        _serviceCommissioningActive = serviceStatus.Active;
        _serviceCommissioningSessionId = serviceStatus.SessionId;
        _serviceCommissioningStage = serviceStatus.Stage;
        _serviceCommissioningExpiresAt = serviceStatus.ExpiresAt;

        if (!serviceStatus.Active)
        {
            if (previouslyOwnedSession is not null)
            {
                ClearLocalCommissionSession();
                ProbeResultText.Text = "服务确认先前的配对会话已经结束；如需继续，请重新探测。";
            }

            UpdateCommissionButtonsForBusyState();
            return;
        }

        if (serviceStatus.ExpiresAt is { } expiresAt && expiresAt <= DateTimeOffset.UtcNow)
        {
            ClearLocalCommissionSession();
            ProbeResultText.Text = "配对会话已到期，正在等待服务完成安全释放；此时不能继续或重新探测。";
            UpdateCommissionButtonsForBusyState();
            return;
        }

        if (previouslyOwnedSession is null ||
            !string.Equals(previouslyOwnedSession, serviceStatus.SessionId, StringComparison.OrdinalIgnoreCase))
        {
            ClearLocalCommissionSession();
            ProbeResultText.Text =
                $"服务中已有另一个活动配对会话（阶段 {FormatCommissioningStage(serviceStatus.Stage)}，" +
                $"到期 {FormatTimestamp(serviceStatus.ExpiresAt)}）；当前窗口不会接管它。";
            UpdateCommissionButtonsForBusyState();
            return;
        }

        RenderCommissioningSessionSummary();
        switch (serviceStatus.Stage)
        {
            case "IdempotentVerified":
                IdempotentResultText.Text = "等值写回与再读已通过，保留字节一致。";
                TestResultText.Text = "等待执行服务端受限分级测试";
                CommitResultText.Text = "尚未接管";
                break;
            case "TestVerified":
                IdempotentResultText.Text = "等值写回与再读已通过，保留字节一致。";
                TestResultText.Text = "服务端受限分级测试已通过。";
                CommitResultText.Text = "尚未接管";
                break;
            default:
                IdempotentResultText.Text = "等待验证等值写回";
                TestResultText.Text = "等待等值写回通过";
                CommitResultText.Text = "尚未接管";
                break;
        }

        UpdateCommissionButtonsForBusyState();
    }

    private void RenderCommissioningSessionSummary()
    {
        var id = _serviceCommissioningSessionId ?? _commissionSessionId ?? "unknown";
        var shortId = id[..Math.Min(8, id.Length)];
        ProbeResultText.Text =
            $"探测通过，会话 {shortId}… / 阶段 {FormatCommissioningStage(_serviceCommissioningStage)} / " +
            $"到期 {FormatTimestamp(_serviceCommissioningExpiresAt)}。";
    }

    private static string FormatCommissioningStage(string? stage) => stage switch
    {
        "Probed" => "已探测",
        "IdempotentVerified" => "等值写回已验证",
        "TestVerified" => "分级测试已验证",
        _ => stage ?? "未知",
    };

    private async Task AbortCommissionSessionBestEffortAsync(
        string reason,
        CancellationToken cancellationToken = default)
    {
        var sessionId = _commissionSessionId;
        if (sessionId is null)
        {
            return;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(PipeTimeout);
        try
        {
            await SendCheckedAsync(
                PipeOperations.CommissionAbort,
                new { sessionId },
                timeout.Token,
                PipeTimeout);
            ActivityText.Text = $"已因“{reason}”取消配对会话；服务已尽力写入 100% 并释放设备。";
        }
        catch (Exception exception) when (IsExpectedClientException(exception))
        {
            ActivityText.Text = $"无法确认配对会话已释放（{ExplainException(exception)}）。若诊断显示硬件处置失败，" +
                                "服务会保留独占所有权直到进程退出；请等待服务退出并重启，期间不要启动 Myth.Cool。";
        }
    }

    private void UpdateCommissionButtonsForBusyState()
    {
        var attended = AttendedCheckBox.IsChecked == true;
        var sessionOwnedByThisWindow =
            _serviceCommissioningActive &&
            _commissionSessionId is not null &&
            string.Equals(
                _commissionSessionId,
                _serviceCommissioningSessionId,
                StringComparison.OrdinalIgnoreCase) &&
            !(_serviceCommissioningExpiresAt is { } expiresAt && expiresAt <= DateTimeOffset.UtcNow);
        AttendedCheckBox.IsEnabled = !_commandInProgress && !_closing;
        ProbeButton.IsEnabled = attended && StateMutationAvailable && !_serviceCommissioningActive &&
            _lastStatus?.Commissioned == false;
        IdempotentButton.IsEnabled = attended && StateMutationAvailable && sessionOwnedByThisWindow &&
            string.Equals(_serviceCommissioningStage, "Probed", StringComparison.Ordinal);
        TestButton.IsEnabled = attended && StateMutationAvailable && sessionOwnedByThisWindow &&
            string.Equals(_serviceCommissioningStage, "IdempotentVerified", StringComparison.Ordinal);
        CommitButton.IsEnabled = attended && StateMutationAvailable && sessionOwnedByThisWindow &&
            string.Equals(_serviceCommissioningStage, "TestVerified", StringComparison.Ordinal);
        RecoverTaskTransitionButton.IsEnabled = attended && !_commandInProgress && !_closing &&
            _lastStatus is not null && _lastStatus.ForcedUninstallRecovery is not { Required: true } &&
            !_serviceCommissioningActive;
        var forcedRecovery = _lastStatus?.ForcedUninstallRecovery;
        var statusKnown = _lastStatus is not null;
        var forcedRecoveryRequired = forcedRecovery?.Required == true;
        var forcedRecoveryAttended = ForcedRecoveryAttendedCheckBox.IsChecked == true;
        ForcedRecoveryAttendedCheckBox.IsEnabled = forcedRecoveryRequired &&
            forcedRecovery?.MarkerValid == true && !_commandInProgress && !_closing;
        RollbackButton.IsEnabled = statusKnown && !_commandInProgress && !_closing &&
            (!forcedRecoveryRequired || forcedRecovery?.MarkerValid == true && forcedRecoveryAttended);
        StartMythCoolCheckBox.IsEnabled = statusKnown && !_commandInProgress && !_closing &&
            !forcedRecoveryRequired;
    }

    private static bool TryGetProperty(JsonElement element, string propertyName, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static string? ReadStringProperty(JsonElement element, string propertyName) =>
        TryGetProperty(element, propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static DateTimeOffset? ReadDateTimeOffsetProperty(JsonElement element, string propertyName)
    {
        var text = ReadStringProperty(element, propertyName);
        return DateTimeOffset.TryParse(
            text,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var value)
            ? value
            : null;
    }

    private static bool ReadRequiredBooleanProperty(JsonElement element, string propertyName)
    {
        if (TryGetProperty(element, propertyName, out var value) &&
            value.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return value.GetBoolean();
        }

        throw new InvalidDataException($"服务响应缺少布尔字段 {propertyName}。");
    }

    private static bool TryReadDouble(JsonElement element, string propertyName, out double value)
    {
        value = default;
        return TryGetProperty(element, propertyName, out var property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetDouble(out value);
    }

    private static bool TryReadInt32(JsonElement element, string propertyName, out int value)
    {
        value = default;
        return TryGetProperty(element, propertyName, out var property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetInt32(out value);
    }

    private static string FormatTemperature(double value) => $"{value:0.0}°C";

    private static string FormatTemperature(double? value) =>
        value is null ? "—" : FormatTemperature(value.Value);

    private static string FormatTimestamp(DateTimeOffset? value) =>
        value?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture) ?? "—";

    private static bool IsExpectedClientException(Exception exception) =>
        exception is IOException
            or TimeoutException
            or UnauthorizedAccessException
            or OperationCanceledException
            or InvalidOperationException
            or JsonException
            or LocalDiagnosticsWriteException
            or PipeApiException;

    private static string ExplainException(Exception exception) => exception switch
    {
        PipeApiException pipe => $"{pipe.Code}: {pipe.Message}",
        DiagnosticsArchiveDestinationExistsException =>
            "目标文件已存在；诊断导出不会覆盖任何现有文件，请选择一个新文件名。",
        LocalDiagnosticsWriteException localWrite => localWrite.Message,
        UnauthorizedAccessException => "当前用户未被授权访问服务。请用安装发起用户登录，或检查受保护的 64-bit HKLM 安装授权记录。",
        OperationCanceledException => "连接本地服务超时。",
        IOException => "本地服务未运行、管道已中断，或响应不完整。",
        _ => exception.Message,
    };

    private void ShowWarning(string message)
    {
        // Async IPC can finish after Closing/Closed. An owner in that state cannot
        // create a dialog; report inline instead of recursively faulting the dispatcher.
        if (_closing || _closed) return;
        ShowPageFeedback(message);
    }

    private async void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (tray is not null && !trayExitRequested && !_serviceCommissioningActive && !_closing)
        {
            e.Cancel = true;
            _ = Dispatcher.BeginInvoke(new Action(() => { if (!_closing && !_closed) Hide(); }));
            return;
        }
        if (_allowClose)
        {
            return;
        }

        e.Cancel = true;
        if (_closing)
        {
            return;
        }

        _closing = true;
        InvalidateStartupConnection();
        InvalidatePageServiceConnection();
        trayDebounce?.Stop();
        trayPendingPercent = null;
        _refreshTimer.Stop();
        _windowLifetime.Cancel();
        IsEnabled = false;
        UpdateCommissionButtonsForBusyState();

        // Even a no-session abort may complete synchronously. Yield out of WPF's
        // Closing callback before calling Close again, otherwise it is reentrant.
        await Dispatcher.Yield(DispatcherPriority.Background);

        using var abortTimeout = new CancellationTokenSource(PipeTimeout);
        await AbortCommissionSessionBestEffortAsync("关闭设置程序", abortTimeout.Token);
        ResetCommissionSession();
        _allowClose = true;
        Close();
    }

    private static JsonSerializerOptions CreateJsonOptions(bool writeIndented)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = writeIndented,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}

public sealed class CurvePointRow
{
    public CurvePointRow(double temperatureCelsius, int fanPercent)
    {
        TemperatureCelsius = temperatureCelsius;
        FanPercent = fanPercent;
    }

    public double TemperatureCelsius { get; set; }

    public int FanPercent { get; set; }
}

public sealed class PipeApiException : Exception
{
    public PipeApiException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}

public sealed class LocalDiagnosticsWriteException : Exception
{
    public LocalDiagnosticsWriteException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
