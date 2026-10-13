using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using V12UltraController.Core.Control;
using V12UltraController.Core.Configuration;
using V12UltraController.Core.Hardware;
using V12UltraController.Core.Ipc;
using V12UltraController.Core.Status;
using V12UltraController.Settings;

internal static class Program
{
    private static int checks;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 2 || args[0] != "--output")
        {
            Console.WriteLine("Offline synthetic WPF validation only. Usage: SettingsPreview --output <new-directory>");
            return args.Length == 0 ? 0 : 2;
        }

        var output = Path.GetFullPath(args[1]);
        if (Directory.Exists(output) || File.Exists(output))
        {
            Console.Error.WriteLine("Output must be a new directory; existing artifacts are never overwritten.");
            return 2;
        }

        Directory.CreateDirectory(output);
        var screenshots = 0;
        var app = new App();
        app.InitializeComponent();
        // Application queues startup even without Run; .NET 10 rejects a null
        // StartupUri. Suppress its default launch in this test host before pumping.
        var defaultStartup = typeof(StartupEventArgs).GetProperty("PerformDefaultAction",
            BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new InvalidOperationException("Unsupported WPF startup contract");
        app.Startup += (_, startup) => defaultStartup.SetValue(startup, false);
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var dispatcherErrors = new List<Exception>();
        app.DispatcherUnhandledException += (_, error) => { dispatcherErrors.Add(error.Exception); error.Handled = true; };
        ShortcutChecks.Run(Assert, output);
        ProfileChecks.Run(Assert);
        LocalizationChecks.Run(Assert);
        screenshots += SafetyProtectionChecks.Run(Assert, output);
        screenshots += ManualActionsChecks.Run(Assert, output);
        screenshots += StartupSettingsChecks.Run(Assert, output);
        CurveDraftChecks.Run(Assert);
        screenshots += PageInteractionChecks.Run(Assert, output);
        screenshots += UiStateChecks.Run(Assert, output);
        screenshots += UiManualPriorityChecks.Run(Assert, output);
        TraySchedulingChecks.Run(Assert);
        TrayHookChecks.Run(Assert);
        screenshots += QuickToggleChecks.Run(Assert, output);
        screenshots += LayoutRefinementChecks.Run(Assert, output);
        // Never Run/Show: only measure and render the detached content visual.
        // All UI requests, including an incidental Loaded event, use this fake.
        var fake = new FakeClient();
        var starter = new FakeStarter();
        var appearance = new FakeAppearance();
        var language = new FakeLanguage();
        var profileStore = new MemoryControlProfileStore([
            new("sample-auto", "日常自动", ControlMode.Auto, 100, FanCurve.RemoteHostSafe, new LightingSettings(true,0,1,153,2)),
            new("sample-manual", "固定转速", ControlMode.Manual, 60, FanCurve.RemoteHostSafe, null),
        ]);
        var window = new MainWindow(fake, starter, appearance, profileStore, languagePreference: language);
        TrayStartupChecks.Run(Assert, window);
        Assert(window.Title.Contains("FanOrder"), "window uses FanOrder branding");
        var deviceMenu = window.BuildDeviceMenu();
        var deviceItems = deviceMenu.Items.OfType<MenuItem>().Where(item => item.IsEnabled).ToArray();
        Assert(deviceItems.Length == 1 && deviceItems[0].IsChecked, "only current V12 profile is selectable");
        var operationsBeforeSelection = fake.Operations.Count;
        deviceItems[0].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Assert(fake.Operations.Count == operationsBeforeSelection, "reselecting current device sends no service commands");
        var startInfo = WindowsBackendStarter.CreateStartInfo();
        Assert(Path.IsPathFullyQualified(startInfo.FileName) && Path.GetFileName(startInfo.FileName) == "sc.exe",
            "starter uses absolute Windows executable");
        Assert(startInfo.ArgumentList.SequenceEqual(new[] { "start", PipeProtocol.ServiceName }),
            "starter has only fixed service-start arguments");
        Assert(startInfo.UseShellExecute && startInfo.Verb == "runas" &&
            startInfo.WindowStyle == System.Diagnostics.ProcessWindowStyle.Hidden, "starter requests elevation without console");
        var root = (FrameworkElement)window.Content;
        ((Panel)root).Background = window.Background;
        var tabs = FindVisual<TabControl>(root).Single();
        var stamp = new DateTimeOffset(2026, 9, 5, 8, 0, 0, TimeSpan.Zero);
        var normal = new ControllerStatus("Running", true, "service (synthetic)",
            new(true, "374A", "B101", "1602", true),
            new(ControlMode.Manual, 60, 60, true, 60, ManualPowerSupported: true),
            new(54, new double[] { 48, 51 }, 54, true, stamp),
            new(false, FailSafeReason.None, null), stamp, stamp,
            new(false, null, null), new(false, null, null, null),
            new(false, false, "keepAt100", null, null, false));
        Refresh(normal);
        var releasedAtStartup = normal with
        {
            Owner = "none",
            Device = normal.Device with { Connected = false },
            Fan = normal.Fan with { Mode = ControlMode.Released, ReportedControlPercent = null, PoweredOn = null },
        };
        Refresh(releasedAtStartup);
        Assert(fake.Operations.Contains(PipeOperations.ModeSet), "startup requests takeover for released commissioned control");
        Assert(fake.Status?.Fan.Mode == ControlMode.Auto, "startup takeover confirms auto mode");
        normal = normal with { Software = new SoftwareStatus(typeof(MainWindow).Assembly.GetName().Version!.ToString(), UiCompatibility.ContractVersion, true) };
        Refresh(normal);
        Assert(tabs.TabStripPlacement == Dock.Left, "Windows sidebar is default");
        var appearanceCalls = fake.Operations.Count;
        ((ComboBox)window.FindName("AppearanceCombo")).SelectedIndex = 1;
        Assert(tabs.TabStripPlacement == Dock.Left && window.CurrentPageId == "overview", "palette preserves navigation structure");
        Assert(fake.Operations.Count == appearanceCalls, "appearance never sends service commands");
        Capture("classic-home", 0);
        ((ComboBox)window.FindName("AppearanceCombo")).SelectedIndex = 0;
        Assert(!appearance.Classic && appearance.Saves == 2, "appearance preference saved through injected store only");
        var languageCombo = (ComboBox)window.FindName("LanguageCombo");
        languageCombo.SelectedIndex = 1;
        Assert(window.Title == "FanOrder Settings" && Text("PageTitleText") == "Control",
            "English language switch updates title and page heading");
        var generalTab = tabs.Items.Cast<TabItem>().Single(item => (string)item.Tag == "general");
        Assert((string)generalTab.Header == "General", "English language switch updates tab headers");
        var englishDeviceMenu = window.BuildDeviceMenu();
        Assert(englishDeviceMenu.Items.OfType<MenuItem>().Any(item =>
            (item.Header as string)?.Contains("More devices", StringComparison.Ordinal) == true),
            "English language switch updates runtime menus");
        Capture("english-home", 0);
        for (var page = 1; page < 8; page++) Capture("english-page-" + page, page);
        CapturePage("english-profiles", "profiles");
        CapturePage("english-general", "general");
        CapturePage("english-shortcuts", "shortcuts");
        Capture("english-home-return", 0);
        languageCombo.SelectedIndex = 0;
        Assert(window.Title == "风序 · FanOrder 设置" && Text("PageTitleText") == "控制",
            "switching back restores Simplified Chinese");
        Assert(language.Saves == 2 && language.Language == UiLanguage.SimplifiedChinese,
            "language preference is saved through the injected store");
        Assert(Enabled("HomeLightToggleButton"), "capable healthy backend permits home lighting");
        Assert(Text("CompatibilityText").Contains("版本与界面协议一致"), "matching version is displayed");
        Refresh(normal with { Software = null });
        Assert(!Enabled("HomeLightToggleButton"), "old backend disables home lighting");
        Assert(Text("CompatibilityText").Contains("未确认"), "missing metadata is not assumed compatible");
        Refresh(normal with { Software = normal.Software! with { UiContractVersion = 999 } });
        Assert(!Enabled("ApplyManualSpeedButton") && !Enabled("HomeLightToggleButton"), "known contract mismatch blocks mutations");
        Assert(Text("ControlAvailabilityText").Contains("协议不匹配"), "protocol mismatch has visible reason");
        Refresh(normal);
        Capture("version-status", 5);
        Capture("history-empty", 6);
        Assert(Text("ControlTemperatureText") == "54.0°C", "normal temperature");
        Assert(!Enabled("StartBackendButton"), "connected backend cannot be started again");
        Assert(starter.Calls == 0, "opening or refreshing UI never starts backend");
        Assert(Enabled("ApplyManualSpeedButton"), "manual enabled");
        Assert(Enabled("FanOnButton"), "power ON enabled for capable commissioned backend");
        Assert(Enabled("FanOffButton"), "power OFF enabled below entry threshold");
        Refresh(normal with { Fan = normal.Fan with { ManualPowerSupported = false } });
        Assert(!Enabled("FanOnButton") && !Enabled("FanOffButton"), "old backend power disabled");
        Refresh(normal with { Thermal = normal.Thermal with { ControlCelsius = 60 } });
        Assert(Enabled("FanOnButton") && !Enabled("FanOffButton"), "hot OFF disabled without disabling ON");
        Refresh(normal);
        Capture("normal", 0);
        Refresh(normal with { ServiceState = "NotCommissioned", Commissioned = false, Owner = "none",
            Device = normal.Device with { Connected = false },
            FailSafe = new(true, FailSafeReason.NotCommissioned, stamp) });
        Assert(Text("FanModeText").Contains("历史回读", StringComparison.Ordinal), "released device labels cached reading historical");
        Assert(Text("DeviceStateText") == "待接管", "uncommissioned is not falsely diagnosed as unplugged");
        Assert(Text("DeviceIdentityText").Contains("配对、接管与回滚", StringComparison.Ordinal), "uncommissioned device gives actionable next step");
        Assert(Text("FanModeText").Contains("当前转速/开关状态未知", StringComparison.Ordinal), "released device current state unknown");
        Assert(!Text("FanModeText").Contains("设备开启", StringComparison.Ordinal), "cached ON is not current ON");
        Capture("released-historical", 0);
        Refresh(normal);
        Capture("curve", 1);
        Capture("commissioning", 2);
        Capture("shutdown-default", 3);
        Assert(!Enabled("ApplyLightingButton"), "lighting cannot apply before capability read");
        var beforeInitialRead = fake.Operations.Count;
        var firstBrightness = (Slider)window.FindName("LightingBrightnessSlider");
        var originalBrightness = firstBrightness.Value;
        firstBrightness.Value = 42;
        typeof(MainWindow).GetMethod("ReadLightingButton_Click", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, new object[] { window, new RoutedEventArgs() });
        Assert(fake.Operations.Count == beforeInitialRead + 1 && firstBrightness.Value == 42,
            "first cache read preserves an existing local draft");
        typeof(MainWindow).GetMethod("UndoLighting_Click", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, new object[] { window, new RoutedEventArgs() });
        Assert(Enabled("ApplyLightingButton"), "supported healthy lighting can apply");
        Assert(((ListBox)window.FindName("LightingColorCombo")).SelectedIndex == 2, "lighting renders green preset");
        var lightingCalls = fake.Operations.Count;
        ((ListBox)window.FindName("LightingModeCombo")).SelectedIndex = 2;
        Assert(Text("LightingColorHint").Contains("不使用单色"), "multi-color explains preset behavior");
        Assert(Text("LightingDraftText").Contains("未应用"), "draft changes are visible");
        Assert(fake.Operations.Count == lightingCalls, "preview never sends hardware requests");
        Capture("rgb-color-draft", 4);
        typeof(MainWindow).GetMethod("ReadLightingButton_Click", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, new object[] { window, new RoutedEventArgs() });
        Assert(fake.Operations.Count == lightingCalls + 1 && ((ListBox)window.FindName("LightingModeCombo")).SelectedIndex == 2,
            "refresh reads cache while preserving the dirty draft");
        typeof(MainWindow).GetMethod("UndoLighting_Click", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, new object[] { window, new RoutedEventArgs() });
        Assert(((ListBox)window.FindName("LightingModeCombo")).SelectedIndex == 1, "undo restores read mode");
        Assert(fake.Operations.Count == lightingCalls + 1, "undo is local only");
        ((CheckBox)window.FindName("LightingEnabledCheckBox")).IsChecked = false;
        typeof(MainWindow).GetMethod("ApplyLightingButton_Click", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, new object[] { window, new RoutedEventArgs() });
        Assert(!fake.Lighting.Enabled && Text("LightingStatusText").Contains("设备确认", StringComparison.Ordinal),
            "lighting OFF means lights only and requires confirmed response");
        Assert(fake.Operations.Count(op => op == PipeOperations.LightingSet) == 1,
            "lighting apply dispatches once");
        Assert(!fake.Operations.Contains(PipeOperations.PowerSet), "lighting never calls fan power API");
        Capture("rgb", 4);
        Capture("rgb-bottom", 4, scrollToEnd: true);
        var draftBrightness = ((Slider)window.FindName("LightingBrightnessSlider"));
        draftBrightness.Value = 42;
        var homeSavedBrightness = fake.Lighting.Brightness;
        typeof(MainWindow).GetMethod("HomeLightOn_Click", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, new object[] { window, new RoutedEventArgs() });
        Assert(fake.Lighting.Enabled && fake.Lighting.Brightness == homeSavedBrightness,
            "home light ON preserves backend preset rather than submitting editor draft");
        Assert(draftBrightness.Value == 42, "home quick control retains unsaved editor draft");
        Assert(Enabled("ReadStartupButton"), "startup can be read independently");
        Assert(!Enabled("SaveStartupButton"), "startup cannot save before confirmed read");
        typeof(MainWindow).GetMethod("ReadStartupButton_Click", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, new object[] { window, new RoutedEventArgs() });
        Assert(((CheckBox)window.FindName("StartupEnabledCheckBox")).IsChecked == true,
            "startup renders confirmed system preference");
        Assert(!Enabled("SaveStartupButton"), "confirmed unchanged startup setting does not enable save");
        ((CheckBox)window.FindName("StartupEnabledCheckBox")).IsChecked = false;
        typeof(MainWindow).GetMethod("SaveStartupButton_Click", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, new object[] { window, new RoutedEventArgs() });
        Assert(!fake.StartupEnabled && Text("StartupSettingText").Contains("不自动运行", StringComparison.Ordinal),
            "startup save dispatches false and renders confirmed response");
        Assert(fake.Operations.Count(op => op == PipeOperations.StartupSet) == 1,
            "startup saves exactly once");
        Assert(!fake.Operations.Contains(PipeOperations.ShutdownEnable) && !fake.Operations.Contains(PipeOperations.SpeedSet),
            "startup save never sends shutdown or speed changes");

        var thermalFailure = normal with
        {
            Fan = normal.Fan with { RequestedPercent = 100 },
            Thermal = new(null, [44, 61], null, false, stamp),
            FailSafe = new(true, FailSafeReason.TelemetryStale, stamp),
        };
        Refresh(thermalFailure);
        Assert(!Enabled("ApplyLightingButton"), "thermal failure blocks lighting edits");
        Assert(((Border)window.FindName("ThermalTroubleshootingBorder")).Visibility == Visibility.Visible,
            "invalid thermal guidance visible");
        Assert(Text("ThermalTroubleshootingText").Contains("PawnIO", StringComparison.Ordinal),
            "driver prerequisite guidance");
        Assert(Text("ThermalTroubleshootingText").Contains("不代表已确定", StringComparison.Ordinal),
            "guidance does not invent a diagnosis");
        Assert(Text("TemperatureDetailText").Contains("最近采样尝试", StringComparison.Ordinal),
            "invalid attempt is not a valid sample claim");
        Capture("thermal-unavailable", 0);
        Refresh(normal);
        Assert(((Border)window.FindName("ThermalTroubleshootingBorder")).Visibility == Visibility.Collapsed,
            "healthy temperature clears guidance");
        Refresh(thermalFailure);

        fake.Status = null;
        RefreshCurrent();
        Assert(Enabled("StartBackendButton"), "offline backend has manual start entry");
        starter.OnStart = () => fake.Status = normal;
        typeof(MainWindow).GetMethod("StartBackendButton_Click", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, new object[] { window, new RoutedEventArgs() });
        Assert(starter.Calls == 1 && Text("ActivityText").Contains("后台已连接", StringComparison.Ordinal),
            "start confirms real pipe readiness through fake response");
        Assert(!Enabled("StartBackendButton"), "successful start prevents duplicate request");
        fake.Status = null;
        RefreshCurrent();
        starter.OnStart = () => { };
        typeof(MainWindow).GetMethod("StartBackendButton_Click", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, new object[] { window, new RoutedEventArgs() });
        Assert(Text("ActivityText").Contains("尚未连接", StringComparison.Ordinal), "start request alone is not readiness");
        starter.OnStart = () => throw new System.ComponentModel.Win32Exception(1223);
        typeof(MainWindow).GetMethod("StartBackendButton_Click", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, new object[] { window, new RoutedEventArgs() });
        Assert(Text("ActivityText").Contains("已取消管理员授权", StringComparison.Ordinal), "UAC cancellation is explicit");
        RefreshCurrent();
        Assert(Text("ControlTemperatureText") == "—", "disconnect clears temperature");
        Assert(!Enabled("SaveStartupButton") && !Enabled("StartupEnabledCheckBox"),
            "disconnect invalidates startup preference");
        Assert(!Enabled("FanOnButton") && !Enabled("FanOffButton"), "disconnect disables power");
        Assert(Text("FanPercentText") == "—", "disconnect clears fan target");
        Assert(Text("FailSafeStateText") == "无法确认", "disconnect not healthy");
        Assert(Text("DeviceStateText") == "状态未知", "disconnect not connected");
        Assert(Text("ShutdownStateText") == "服务状态不可用", "disconnect clears shutdown");
        Assert(((Border)window.FindName("ThermalTroubleshootingBorder")).Visibility == Visibility.Collapsed,
            "disconnect clears old thermal guidance");
        ((CheckBox)window.FindName("AttendedCheckBox")).IsChecked = true;
        foreach (var name in new[] { "AutoModeButton", "ManualModeButton", "ApplyManualSpeedButton",
                     "ApplyCurveButton", "ProbeButton", "IdempotentButton", "TestButton", "CommitButton",
                     "RollbackButton", "RecoverTaskTransitionButton", "QualifyShutdownButton", "EnableShutdownButton",
                     "ReadStartupButton", "SaveStartupButton" })
        {
            Assert(!Enabled(name), "disconnect disables " + name);
        }
        Capture("disconnected", 0);

        var audit = normal with
        {
            SafetyOperationAudit = new(true, true, "11111111-2222-4333-8444-555555555555",
                "commission.idempotent", "HidWithOn100Recovery", "EffectInProgress", "Indeterminate", true,
                "合成故障：安全日志无法收尾，当前写入结果必须保留为未知。"),
            HardwareSafety = new(true, "synthetic ownership recovery barrier", stamp),
        };
        Refresh(audit);
        Assert(((Border)window.FindName("SafetyOperationAuditBorder")).Visibility == Visibility.Visible,
            "safety audit visible");
        foreach (var name in new[] { "AutoModeButton", "ManualModeButton", "ApplyManualSpeedButton", "ApplyCurveButton" })
        {
            Assert(!Enabled(name), "audit disables " + name);
        }
        Capture("audit-recovery", 0);
        Assert(Text("FailSafeStateText") == "恢复阻断", "recovery is not green healthy");
        foreach (var blocked in new[]
                 {
                     normal with { SafetyOperationAudit = audit.SafetyOperationAudit },
                     normal with { AuditMutation = new(true, true, "synthetic", "mode.set", "Prepared", "synthetic") },
                     normal with { ForcedUninstallRecovery = new(true, false, null, "corrupt marker", stamp) },
                 })
        {
            Refresh(blocked);
            foreach (var name in new[] { "AutoModeButton", "ManualModeButton", "ApplyManualSpeedButton", "ApplyCurveButton",
                         "ProbeButton", "QualifyShutdownButton", "EnableShutdownButton" })
            {
                Assert(!Enabled(name), "independent recovery barrier disables " + name);
            }
        }
        Assert(!Enabled("RollbackButton"), "corrupt recovery marker disables rollback");
        Refresh(normal with { ForcedUninstallRecovery = new(true, true, "synthetic-marker", null, stamp) });
        Assert(!Enabled("RollbackButton"), "forced recovery requires attendance");
        Assert(!Enabled("StartMythCoolCheckBox"), "forced recovery disallows vendor start");
        ((CheckBox)window.FindName("ForcedRecoveryAttendedCheckBox")).IsChecked = true;
        Assert(Enabled("RollbackButton"), "forced recovery attended rollback available");
        Capture("forced-recovery", 2, scrollToEnd: true);
        var sessionId = "11111111-2222-4333-8444-555555555555";
        var commissioning = normal with
        {
            Commissioned = false,
            Commissioning = new(true, sessionId, "Probed", DateTimeOffset.UtcNow.AddMinutes(1)),
        };
        Refresh(commissioning);
        ((CheckBox)window.FindName("AttendedCheckBox")).IsChecked = false;
        ((CheckBox)window.FindName("AttendedCheckBox")).IsChecked = true;
        Assert(!Enabled("ProbeButton"), "attendance cannot override another active session");
        typeof(MainWindow).GetField("_commissionSessionId", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(window, sessionId);
        Refresh(commissioning);
        Assert(Enabled("IdempotentButton"), "owned healthy session can progress");
        fake.Status = null;
        RefreshCurrent();
        Assert(!Enabled("IdempotentButton"), "owned disconnected session cannot progress");
        typeof(MainWindow).GetField("_commissionSessionId", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(window, null);
        Refresh(normal);
        Assert(Enabled("ApplyManualSpeedButton"), "reconnect restores manual control");
        Assert(((Border)window.FindName("SafetyOperationAuditBorder")).Visibility == Visibility.Collapsed,
            "recovery hides stale audit banner");
        typeof(MainWindow).GetMethod("AnalyzeHistoryButton_Click", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, new object[] { window, new RoutedEventArgs() });
        Refresh(normal);
        Assert(Text("AdviceSummaryText").Contains("没有可用于建议的有效自动模式记录"), "empty history explains why there is no advice");
        Assert(!Enabled("ApplyAdviceButton"), "no evidence never enables speculative apply");
        Assert(Enabled("ExportAdviceButton"), "analysis can be exported offline");
        fake.AdviceSamples = Enumerable.Range(0, 130).Select(i => new ThermalObservation(
            DateTimeOffset.UtcNow.AddMinutes(i - 130), 50, 70, true, false, CurveAdvisor.Identity(FanCurve.RemoteHostSafe))).ToArray();
        typeof(MainWindow).GetMethod("AnalyzeHistoryButton_Click", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, new object[] { window, new RoutedEventArgs() });
        Refresh(normal);
        Assert(Enabled("ApplyAdviceButton"), "supported local advice enables explicit apply");
        Capture("curve-advice", 7);
        typeof(MainWindow).GetMethod("ApplyAdviceButton_Click", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, new object[] { window, new RoutedEventArgs() });
        Refresh(normal);
        Assert(Enabled("UndoAdviceButton") && !Enabled("ApplyAdviceButton"), "one-click apply exposes undo without repeat apply");
        typeof(MainWindow).GetMethod("UndoAdviceButton_Click", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, new object[] { window, new RoutedEventArgs() });
        Refresh(normal);
        Assert(!Enabled("UndoAdviceButton"), "undo finishes cleanly");
        Refresh(normal with { Fan = normal.Fan with { Mode = ControlMode.PureManual }, Thermal = normal.Thermal with { Healthy = false } });
        Assert(Enabled("ApplyManualSpeedButton") && Enabled("FanOffButton"), "pure manual permits explicit commands without temperature override");
        Assert(Text("FailSafeStateText").Contains("禁用"), "pure manual protection card cannot claim active thermal safety");
        Capture("pure-manual", 0);
        Refresh(normal with { Fan = normal.Fan with { Mode = ControlMode.Released }, Device = normal.Device with { Connected = false } });
        Assert(!Enabled("ApplyManualSpeedButton") && !Enabled("FanOffButton"), "released mode disables direct controls");
        Assert(Enabled("PureManualButton"), "released mode exposes explicit resume");
        Refresh(normal with { Fan = normal.Fan with { ReportedControlPercent = 100 }, LastSuccessfulRead = DateTimeOffset.UtcNow });
        Assert(Text("FanReadbackText").Contains("2800 RPM（估算）"), "dual readout labels 100 percent as estimated 2800 RPM");
        Capture("fan-dual-readout", 0);
        Refresh(normal with { Fan = normal.Fan with { PoweredOn = false }, LastSuccessfulRead = DateTimeOffset.UtcNow });
        Assert(Text("FanReadbackText").Contains("0% · 0 RPM（已关闭）"), "confirmed OFF shows zero despite retained percent");
        Refresh(normal with { Device = normal.Device with { Connected = false } });
        Assert(Text("FanReadbackText").Contains("未知"), "disconnected device does not claim live RPM");
        Refresh(normal);
        var speedCalls = fake.Operations.Count(o => o == PipeOperations.SpeedSet);
        window.QueueTrayWheel(120);
        window.QueueTrayWheel(240);
        Assert(fake.Operations.Count(o => o == PipeOperations.SpeedSet) == speedCalls, "wheel input is coalesced before IPC");
        window.FlushTraySpeedAsync().GetAwaiter().GetResult();
        Assert(fake.LastSpeed == 63, "three tray detents produce one 63 percent request");
        Assert(fake.Operations.Count(o => o == PipeOperations.SpeedSet) == speedCalls + 1, "tray sends one coalesced request");
        Refresh(normal with { FailSafe = new(true, FailSafeReason.HidFailure, stamp) });
        window.QueueTrayWheel(-120);
        window.FlushTraySpeedAsync().GetAwaiter().GetResult();
        Assert(fake.LastSpeed == 63, "protected status cannot enqueue lowering");
        Refresh(normal);
        window.QueueTrayWheel(-120);
        fake.Status = null;
        RefreshCurrent();
        window.FlushTraySpeedAsync().GetAwaiter().GetResult();
        Refresh(normal);
        window.FlushTraySpeedAsync().GetAwaiter().GetResult();
        Assert(fake.Operations.Count(o => o == PipeOperations.SpeedSet) == speedCalls + 1, "disconnection drops pending input without replay on reconnect");
        fake.SpeedStateChanged = true;
        window.QueueTrayWheel(120);
        window.FlushTraySpeedAsync().GetAwaiter().GetResult();
        Assert(Text("ActivityText").Contains("旋钮"), "valid mismatched speed is explained without a modal error");
        var afterMismatch = fake.Operations.Count(o => o == PipeOperations.SpeedSet);
        window.FlushTraySpeedAsync().GetAwaiter().GetResult();
        Assert(fake.Operations.Count(o => o == PipeOperations.SpeedSet) == afterMismatch, "mismatched speed is never automatically retried");
        fake.SpeedMismatchCode = PipeErrorCodes.CommandReadbackMismatch;
        window.QueueTrayWheel(120);
        window.FlushTraySpeedAsync().GetAwaiter().GetResult();
        Assert(Text("ActivityText").Contains("旋钮"), "power or lighting mismatch also stays nonmodal");
        fake.SpeedStateChanged = false;
        Assert(!window.IsVisible, "no native window shown");
        var closingWindow = new MainWindow(new FakeClient());
        var closingArgs = new System.ComponentModel.CancelEventArgs();
        typeof(MainWindow).GetMethod("MainWindow_Closing", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(closingWindow, new object[] { closingWindow, closingArgs });
        Assert(closingArgs.Cancel, "first close is cancelled for async cleanup");
        Assert(!(bool)typeof(MainWindow).GetField("_allowClose", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(closingWindow)!, "synchronous no-session cleanup cannot reenter Close");
        typeof(MainWindow).GetMethod("ShowFromTray", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(closingWindow, null);
        typeof(MainWindow).GetMethod("ShowWarning", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(closingWindow, new object[] { "Synthetic late IPC error" });
        Assert(!closingWindow.IsVisible, "closing window rejects tray reopen and late warning");
        closingWindow.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(() => { }));
        Assert((bool)typeof(MainWindow).GetField("_closed", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(closingWindow)!, "deferred close completes after the Closing callback returns");
        var releasedClient = new FakeClient { Status = normal };
        var releasedWindow = new MainWindow(releasedClient);
        typeof(MainWindow).GetMethod("ReleaseAndExit", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(releasedWindow, null);
        releasedWindow.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(() => { }));
        Assert(releasedClient.Operations.Contains(PipeOperations.ModeSet), "tray exit first requests durable backend release");
        Assert((bool)typeof(MainWindow).GetField("_closed", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(releasedWindow)!, "verified backend release allows tray exit to close UI");
        Assert(fake.Operations.All(operation => operation is PipeOperations.SpeedSet or PipeOperations.StatusGet or PipeOperations.ModeSet or PipeOperations.StartupGet or PipeOperations.StartupSet or PipeOperations.StartupFanGet or PipeOperations.StartupFanSet or PipeOperations.LightingGet or PipeOperations.LightingSet or PipeOperations.HistoryAnalyze or PipeOperations.CurveAdviceApply),
            "only allowlisted fake status, startup and lighting requests");
        Refresh(normal with { Software = normal.Software! with { ProfileApplySupported = true } });
        var beforeNavigation = fake.Operations.Count;
        window.NavigateToPage("lighting");
        window.NavigatePrimary("settings");
        window.NavigatePrimary("control");
        Assert(window.CurrentPageId == "lighting", "primary navigation restores last child page");
        Assert(fake.Operations.Skip(beforeNavigation).All(operation => operation is PipeOperations.StartupGet or PipeOperations.StartupFanGet or PipeOperations.LightingGet),
            "navigation only reads startup state or lighting cache and never applies device changes");
        Assert(((Panel)window.FindName("PrimaryNavigationPanel")).Children.Count == 3, "only three primary destinations");
        Assert(tabs.Items.Count == 10, "all ten specialized pages are present");
        foreach (var page in new[] { "overview", "fan", "lighting", "profiles", "trends", "analysis", "general", "shortcuts", "maintenance", "about" })
            CapturePage("navigation-" + page, page);
        CapturePage("profiles-editor-bottom", "profiles", true);
        CapturePage("shortcuts-device-bottom", "shortcuts", true);
        CapturePage("fan-editor-bottom", "fan", true);
        var shutdownSection = (Expander)window.FindName("ShutdownExpander");
        shutdownSection.IsExpanded = true;
        ((Expander)window.FindName("CommissionExpander")).IsExpanded = false;
        CapturePage("help-shutdown", "maintenance", scrollToElement: shutdownSection);
        var shutdownHelp = (Button)window.FindName("ShutdownHelpButton");
        var helpContent = shutdownHelp.ToolTip;
        shutdownHelp.ToolTip = null;
        var helpPreview = new ToolTip { Content = helpContent };
        helpPreview.Measure(new Size(460, double.PositiveInfinity));
        helpPreview.Arrange(new Rect(helpPreview.DesiredSize));
        helpPreview.UpdateLayout();
        var helpBitmap = new RenderTargetBitmap((int)Math.Ceiling(helpPreview.ActualWidth),
            (int)Math.Ceiling(helpPreview.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        helpBitmap.Render(helpPreview);
        var helpEncoder = new PngBitmapEncoder();
        helpEncoder.Frames.Add(BitmapFrame.Create(helpBitmap));
        using (var helpStream = new FileStream(Path.Combine(output, "help-shutdown-details.png"), FileMode.CreateNew))
            helpEncoder.Save(helpStream);
        screenshots++;
        helpPreview.Content = null;
        shutdownHelp.ToolTip = helpContent;
        window.CurvePoints[0].FanPercent = 71;
        Assert(window.CurveDraftChanged, "edited curve is marked dirty");
        window.NavigatePrimary("monitor");
        window.NavigateToPage("fan");
        Assert(window.CurvePoints[0].FanPercent == 71, "curve draft survives navigation");
        window.ConfirmDraftReplacement = _ => false;
        typeof(MainWindow).GetMethod("ResetCurveButton_Click", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, new object[] { window, new RoutedEventArgs() });
        Assert(window.CurvePoints[0].FanPercent == 71, "declined curve replacement preserves edits");
        window.ConfirmDraftReplacement = _ => true;
        typeof(MainWindow).GetMethod("ResetCurveButton_Click", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, new object[] { window, new RoutedEventArgs() });
        Assert(dispatcherErrors.Count == 0, "no dispatcher exceptions during offline validation");
        Assert(app.Windows.Cast<Window>().All(candidate => !candidate.IsVisible &&
            new System.Windows.Interop.WindowInteropHelper(candidate).Handle == IntPtr.Zero),
            "no production or native window created by queued application startup");
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            checks,
            failures = 0,
            hardwareAccessed = false,
            realPipeAccessed = false,
            nativeWindowShown = false,
            screenshots
        }));
        return 0;

        string Text(string name) => ((TextBlock)window.FindName(name)).Text;
        bool Enabled(string name) => ((Control)window.FindName(name)).IsEnabled;
        void Refresh(ControllerStatus status)
        {
            fake.Status = status;
            RefreshCurrent();
        }
        void RefreshCurrent()
        {
            var method = typeof(MainWindow).GetMethod("RefreshStatusAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var task = (Task)method.Invoke(window, new object[] { false, false, false })!;
            Assert(task.IsCompleted, "fake status completes synchronously");
            task.GetAwaiter().GetResult();
        }
        void Capture(string scenario, int tabIndex, bool scrollToEnd = false)
        {
            ((Expander)window.FindName("CommissionExpander")).IsExpanded = tabIndex == 2;
            ((Expander)window.FindName("ShutdownExpander")).IsExpanded = tabIndex == 3;
            CapturePage(scenario, new[] { "overview", "fan", "maintenance", "maintenance", "lighting", "about", "trends", "analysis" }[tabIndex], scrollToEnd);
        }
        void CapturePage(string scenario, string pageId, bool scrollToEnd = false, FrameworkElement? scrollToElement = null)
        {
            ((Panel)root).Background = window.Background;
            window.NavigateToPage(pageId);
            foreach (var size in new[] { new Size(1180, 750), new Size(980, 640) })
            {
                root.Measure(size);
                root.Arrange(new Rect(size));
                root.UpdateLayout();
                var scroll = FindVisual<ScrollViewer>(tabs)
                    .OrderByDescending(viewer => viewer.ScrollableHeight).FirstOrDefault();
                if (scrollToEnd) scroll?.ScrollToEnd(); else scroll?.ScrollToHome();
                root.UpdateLayout();
                if (scrollToElement is not null && scroll is not null)
                {
                    scroll.ScrollToVerticalOffset(scroll.VerticalOffset + scrollToElement.TranslatePoint(new Point(0, 0), scroll).Y);
                    root.UpdateLayout();
                }
                if (scrollToEnd && pageId == "shortcuts")
                {
                    Assert(scroll is { ScrollableHeight: > 0 }, "shortcut groups have a scrollable viewport");
                    Assert(Math.Abs(scroll!.VerticalOffset - scroll.ScrollableHeight) < 1,
                        "shortcut viewport reaches the final device action");
                    var lastAction = FindVisual<Button>(tabs).Single(button =>
                        System.Windows.Automation.AutomationProperties.GetName(button) == "关闭灯光快捷键操作");
                    var lastPosition = lastAction.TransformToAncestor(scroll).Transform(new Point(0, 0));
                    Assert(lastPosition.Y >= 0 && lastPosition.Y + lastAction.ActualHeight <= scroll.ActualHeight,
                        "last device shortcut is fully visible at minimum and normal sizes");
                }
                if (V12UltraController.Settings.Localization.CurrentLanguage == UiLanguage.English)
                {
                    bool IsData(DependencyObject node)
                    {
                        for (DependencyObject? current = node; current is not null; current = VisualTreeHelper.GetParent(current))
                            if (current is TextBox || current is FrameworkElement { Tag: "UserContent" }) return true;
                        return false;
                    }
                    var untranslated = FindVisual<TextBlock>(root).Where(text => text.ActualWidth > 0 && !IsData(text)
                        && text.Text != "简体中文" && System.Text.RegularExpressions.Regex.IsMatch(text.Text, @"[\u4e00-\u9fff]"))
                        .Select(text => text.Text).Distinct().ToArray();
                    File.WriteAllText(Path.Combine(output, scenario + "-untranslated.json"), JsonSerializer.Serialize(untranslated));
                    Assert(untranslated.Length == 0, "English page has no untranslated UI text: " + scenario + ": " + string.Join(" | ", untranslated));
                }
                var bitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(root);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream = new FileStream(Path.Combine(output, $"{scenario}-{size.Width}.png"), FileMode.CreateNew);
                encoder.Save(stream);
                screenshots++;
            }
        }
    }

    private static void Assert(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException("Offline UI assertion failed: " + name);
        checks++;
    }


    private static IEnumerable<T> FindVisual<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) yield return match;
            foreach (var descendant in FindVisual<T>(child)) yield return descendant;
        }
    }

    private sealed class FakeClient : IPipeApiClient
    {
        public byte? LastSpeed { get; private set; }
        public bool SpeedStateChanged { get; set; }
        public string SpeedMismatchCode { get; set; } = PipeErrorCodes.DeviceStateChanged;
        public ThermalObservation[] AdviceSamples { get; set; } = [];
        public LightingSettings Lighting { get; set; } = new(true,1,2,200,2);
        public bool StartupEnabled { get; set; } = true;
        public byte StartupFanPercent { get; set; } = 50;
        public ControllerStatus? Status { get; set; }
        public List<string> Operations { get; } = new();
        public Task<PipeResponse> SendAsync(PipeRequest request, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Operations.Add(request.Operation);
            if (request.Operation == PipeOperations.ModeSet && Status is not null)
            {
                var mode = request.Payload.GetProperty("mode").GetString();
                Status = mode switch
                {
                    "auto" => Status with { Owner = "configured-user", Fan = Status.Fan with { Mode = ControlMode.Auto } },
                    "manual" => Status with { Owner = "configured-user", Fan = Status.Fan with { Mode = ControlMode.Manual } },
                    "puremanual" => Status with { Owner = "configured-user", Fan = Status.Fan with { Mode = ControlMode.PureManual } },
                    "released" => Status with { Owner = "none", Fan = Status.Fan with { Mode = ControlMode.Released },
                        Device = Status.Device with { Connected = false } },
                    _ => Status,
                };
            }
            if (request.Operation == PipeOperations.SpeedSet)
            {
                LastSpeed = request.Payload.GetProperty("percent").GetByte();
                if (SpeedStateChanged) return Task.FromResult(PipeResponse.Failure(request.RequestId,
                    SpeedMismatchCode, "转速未确认，可能被实体旋钮调整；未重试。"));
                if (Status is null) throw new InvalidOperationException("Synthetic speed requires a known status.");
                Status = Status with { Device = Status.Device with { Connected = true },
                    Fan = Status.Fan with { Mode = Status.Fan.Mode == ControlMode.PureManual ? ControlMode.PureManual : ControlMode.Manual,
                        RequestedPercent = LastSpeed.Value, ConfiguredManualPercent = LastSpeed, ReportedControlPercent = LastSpeed, PoweredOn = true },
                    LastSuccessfulRead = DateTimeOffset.UtcNow };
                return Task.FromResult(PipeResponse.Success(request.RequestId, Status));
            }
            if (request.Operation == PipeOperations.HistoryAnalyze)
                return Task.FromResult(PipeResponse.Success(request.RequestId, CurveAdvisor.Analyze(AdviceSamples, FanCurve.RemoteHostSafe, DateTimeOffset.UtcNow)));
            if (request.Operation == PipeOperations.CurveAdviceApply)
                return Task.FromResult(PipeResponse.Success(request.RequestId, new { applied = true }));
            if (request.Operation is PipeOperations.LightingGet or PipeOperations.LightingSet)
            {
                if (request.Operation == PipeOperations.LightingSet)
                    Lighting = request.Payload.Deserialize<LightingSettings>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
                return Task.FromResult(PipeResponse.Success(request.RequestId,
                    new { settings = Lighting, supported = true, cached = true, readbackConfirmed = true }));
            }
            if (Status is not null && request.Operation is PipeOperations.StartupGet or PipeOperations.StartupSet)
            {
                if (request.Operation == PipeOperations.StartupSet)
                    StartupEnabled = request.Payload.GetProperty("enabled").GetBoolean();
                return Task.FromResult(PipeResponse.Success(request.RequestId,
                    new { enabled = StartupEnabled, readbackConfirmed = true, startupFanPercent = StartupFanPercent, defaultStartupFanPercent = 50 }));
            }
            if (Status is not null && request.Operation is PipeOperations.StartupFanGet or PipeOperations.StartupFanSet)
            {
                if (request.Operation == PipeOperations.StartupFanSet)
                    StartupFanPercent = request.Payload.GetProperty("percent").GetByte();
                return Task.FromResult(PipeResponse.Success(request.RequestId,
                    new { percent = StartupFanPercent, defaultPercent = 50, readbackConfirmed = true }));
            }
            if (request.Operation is not (PipeOperations.StatusGet or PipeOperations.ModeSet) || Status is null)
                return Task.FromException<PipeResponse>(new IOException("Synthetic offline transport"));
            var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            options.Converters.Add(new JsonStringEnumConverter());
            return Task.FromResult(new PipeResponse(PipeProtocol.ApiVersion, request.RequestId, true,
                JsonSerializer.SerializeToElement(Status, options), null, DateTimeOffset.UtcNow));
        }
    }

    private sealed class FakeAppearance : IAppearancePreference
    {
        public bool Classic { get; private set; }
        public int Saves { get; private set; }
        public bool LoadClassic() => Classic;
        public void SaveClassic(bool classic) { Classic = classic; Saves++; }
    }

    private sealed class FakeLanguage : ILanguagePreference
    {
        public UiLanguage Language { get; private set; }
        public int Saves { get; private set; }
        public UiLanguage Load() => Language;
        public void Save(UiLanguage language) { Language = language; Saves++; }
    }

    private sealed class FakeStarter : IBackendStarter
    {
        public int Calls { get; private set; }
        public Action? OnStart { get; set; }
        public Task StartAsync(CancellationToken cancellationToken)
        {
            Calls++;
            OnStart?.Invoke();
            return Task.CompletedTask;
        }
    }
}
