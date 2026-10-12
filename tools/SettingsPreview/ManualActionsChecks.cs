using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using V12UltraController.Core.Configuration;
using V12UltraController.Core.Control;
using V12UltraController.Core.Hardware;
using V12UltraController.Core.Ipc;
using V12UltraController.Core.Status;
using V12UltraController.Settings;

internal static class ManualActionsChecks
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    { Converters = { new JsonStringEnumConverter() } };
    private static readonly FanCurve Curve = new([new(0, 30), new(50, 60), new(90, 100)]);

    internal static int Run(Action<bool, string> assert, string output)
    {
        var client = new ManualFakeClient();
        var manual = new ControlProfileSnapshot("manual-cold", "手动组合", ControlMode.Manual, 42, Curve,
            new LightingSettings(true, 1, 2, 155, 2));
        var automatic = manual with { Id = "auto-paused", Name = "自动草稿", Mode = ControlMode.Auto };
        var window = new MainWindow(client, profileStore: new MemoryControlProfileStore([manual, automatic]));
        window.BringProfileConfirmationToFront = () => { };
        window.ConfirmProfileAction = _ => true;
        Render(window, client.Status);
        assert(Button(window, "FanOnButton").IsEnabled && Button(window, "FanOffButton").IsEnabled &&
            Button(window, "ApplyManualSpeedButton").IsEnabled,
            "latched cold backend permits explicit speed and power without a cached handle");
        assert(Button(window, "FanOffButton").ToolTip.ToString()!.Contains("关闭风扇并熄灯") &&
            Button(window, "FanOnButton").ToolTip.ToString()!.Contains("设备未断开") &&
            Text(window, "FanPowerHelpText").Contains("设备按键和手动灯光的新设置优先"),
            "linked fan power help explains dark OFF, temporary restoration, and newer manual lighting priority");
        assert(Text(window, "DeviceStateText") == "待操作确认" && Text(window, "FanTargetLabelText") == "手动目标" &&
            Text(window, "FanPercentText") == "59%" && Text(window, "FanReadbackText").Contains("未知"),
            "passive unobserved device distinguishes saved target from unknown readback and legacy 100 projection");
        assert(Button(window, "ManualModeButton").Content.ToString()!.Contains("当前") &&
            Button(window, "AutoModeButton").Content.ToString()!.Contains("暂停"),
            "mode buttons show current manual mode and paused automatic configuration");
        assert(Button(window, "ApplyLightingButton").IsEnabled && Button(window, "HomeLightToggleButton").IsEnabled,
            "new manual capability permits complete RGB draft and cached shortcut actions before opening a handle");
        assert(!Button(window, "SaveStartupButton").IsEnabled && Button(window, "SaveStartupButton").ToolTip.ToString()!.Contains("自动确认"),
            "unread startup setting has a precise disabled reason");
        assert(Button(window, "ApplyAdviceButton").ToolTip.ToString()!.Contains("先分析") &&
            Button(window, "UndoAdviceButton").ToolTip.ToString()!.Contains("尚未应用"),
            "unavailable history actions explain missing analysis and undo state");
        Capture(window, "fan", "manual-cold-fan-980", output);
        Capture(window, "overview", "manual-cold-overview-980", output);

        var offConfirm = "";
        window.ConfirmFanOff = text => { offConfirm = text; return false; };
        Event(window, "FanOffButton_Click");
        assert(client.Operations.Count == 0 && offConfirm.Contains("都不会自动重新开扇") &&
            offConfirm.Contains("关闭风扇并熄灯") && offConfirm.Contains("临时保留"),
            "OFF remains explicitly confirmed with accurate paused-control wording");
        window.ConfirmFanOff = _ => true;
        Event(window, "FanOffButton_Click");
        assert(client.PowerTargets.SequenceEqual([false]) && client.Status.SafetyProtection?.Disabled == true,
            "confirmed high-temperature explicit OFF preserves protection latch");
        assert(Text(window, "ActivityText").Contains("关闭并熄灯"),
            "confirmed linked OFF reports that both fan and lighting are off");
        TaskCall(window, "SetFanPowerAsync", true);
        assert(client.PowerTargets.SequenceEqual([false, true]) && Text(window, "ActivityText").Contains("保存的手动目标") &&
            !Text(window, "ActivityText").Contains("100%"), "explicit ON describes saved manual target instead of claiming 100 percent");
        assert(Text(window, "ActivityText").Contains("已恢复关闭前的灯光选择"),
            "ON reports lighting restoration only after the backend explicitly confirms it");

        var legacyClient = new ManualFakeClient { ReturnLightingLinkResult = false };
        legacyClient.Status = legacyClient.Status with { Software = legacyClient.Status.Software! with { FanLightingLinkSupported = false } };
        var legacyWindow = new MainWindow(legacyClient);
        Render(legacyWindow, legacyClient.Status);
        var legacyConfirmation = "";
        legacyWindow.ConfirmFanOff = text => { legacyConfirmation = text; return false; };
        Event(legacyWindow, "FanOffButton_Click");
        assert(legacyClient.Operations.Count == 0 && !legacyConfirmation.Contains("熄灯") &&
            !Text(legacyWindow, "FanPowerHelpText").Contains("灯光") &&
            !Button(legacyWindow, "FanOnButton").ToolTip.ToString()!.Contains("灯光"),
            "a backend without fan-light linking retains its original confirmation and help");
        TaskCall(legacyWindow, "SetFanPowerAsync", false);
        assert(Text(legacyWindow, "ActivityText").Contains("设备已确认关闭") &&
            !Text(legacyWindow, "ActivityText").Contains("熄灯"),
            "legacy power replies never claim that lighting was turned off");
        legacyClient.Status = legacyClient.Status with { Software = legacyClient.Status.Software! with { FanLightingLinkSupported = true } };
        Render(legacyWindow, legacyClient.Status);
        TaskCall(legacyWindow, "SetFanPowerAsync", true);
        assert(Text(legacyWindow, "ActivityText").Contains("已确认") &&
            !Text(legacyWindow, "ActivityText").Contains("已恢复关闭前"),
            "link capability alone never claims restoration when the power reply does not confirm it");
        client.Status = ColdStatus(); Render(window, client.Status);
        window.QueueTrayWheel(120); window.FlushTraySpeedAsync().GetAwaiter().GetResult();
        assert(client.SpeedTargets.Last() == 60, "cold latched tray speed uses persisted 59 baseline and permits explicit reconnect");

        var modesBefore = client.SpeedTargets.Count;
        Event(window, "AutoModeButton_Click");
        assert(client.Status.Fan.Mode == ControlMode.Auto && client.Status.SafetyProtection?.Disabled == true &&
            client.SpeedTargets.Count == modesBefore && Text(window, "ActivityText").Contains("自动调节暂停"),
            "saving automatic mode preserves the latch and never claims automatic output resumed");
        Event(window, "ManualModeButton_Click");
        assert(client.Status.Fan.Mode == ControlMode.Manual && Text(window, "ActivityText").Contains("仍禁用"),
            "saving manual mode does not silently restore protection");

        client.CachedLighting = null;
        TaskCall(window, "SetHomeLightingAsync", false);
        assert(client.LightingWrites.Count == 0 && Text(window, "ActivityText").Contains("完整设置") &&
            !Button(window, "HomeLightToggleButton").IsEnabled,
            "missing cached RGB state never invents colors or sends shortcut output");
        var brightness = (Slider)window.FindName("LightingBrightnessSlider"); brightness.Value = 181;
        Event(window, "ApplyLightingButton_Click");
        assert(client.LightingWrites.Single().Brightness == 181 && client.Status.SafetyProtection?.Disabled == true,
            "complete local RGB draft can explicitly apply without a prior cached hardware state");
        client.ReturnLightingSupported = false;
        TaskCall(window, "SetHomeLightingAsync", false);
        assert(client.LightingWrites.Count == 2 && !client.LightingWrites[^1].Enabled &&
            client.LightingWrites[^1].Brightness == 181,
            "persisted lighting cache remains usable with new capability even when no handle reports supported");
        Capture(window, "lighting", "manual-lighting-980", output);

        client.Status = ColdStatus(); Render(window, client.Status);
        var priorOps = client.Operations.Count;
        window.ApplySavedProfileAsync(automatic.Id).GetAwaiter().GetResult();
        assert(client.Operations.Count == priorOps && ProfileText(window).Contains("自动方案暂停"),
            "automatic profile stays editable but cannot resume output through apply");
        window.ApplySavedProfileAsync(manual.Id).GetAwaiter().GetResult();
        assert(client.AppliedProfile is { ConfirmResumeControl: false } && client.Status.SafetyProtection?.Disabled == true &&
            client.Status.Fan.ReportedControlPercent == 42, "manual profile applies saved fan and RGB while preserving the latch");
        assert(((Button)Field(window, "_profileSave")!).ToolTip.ToString()!.Contains("没有未保存"),
            "clean saved profile explains why local save is disabled");
        var overviewSelect = (ComboBox)window.FindName("OverviewProfileCombo");
        overviewSelect.SelectedValue = automatic.Id;
        assert(!Button(window, "OverviewApplyProfileButton").IsEnabled && Button(window, "OverviewApplyProfileButton").ToolTip.ToString()!.Contains("自动方案暂停"),
            "overview selection immediately updates paused automatic-profile reason");
        overviewSelect.SelectedValue = manual.Id;
        assert(Button(window, "OverviewApplyProfileButton").IsEnabled,
            "overview permits saved manual profile without restoring protection");
        Capture(window, "profiles", "manual-profile-980", output);

        Event(window, "AnalyzeHistoryButton_Click");
        Event(window, "ApplyAdviceButton_Click");
        assert(Text(window, "AdviceSummaryText").Contains("仍暂停") && client.Status.SafetyProtection?.Disabled == true,
            "history advice saves a paused automatic configuration without claiming safety is active");
        Event(window, "UndoAdviceButton_Click");
        assert(Text(window, "AdviceSummaryText").Contains("暂停"), "history undo preserves paused-control wording");

        var barrier = ColdStatus() with { HardwareSafety = new(true, "synthetic barrier", DateTimeOffset.UtcNow) };
        client.Status = barrier; Render(window, barrier);
        assert(!Button(window, "ApplyManualSpeedButton").IsEnabled && !Button(window, "FanOnButton").IsEnabled &&
            Text(window, "FanPercentText") == "59%", "real recovery barriers remain blocked without displaying an unsent 100 target");
        assert(((Button)Field(window, "_profileCreate")!).IsEnabled,
            "read-only local profile creation is not blocked by unrelated hardware recovery state");
        TaskCall(window, "CreateProfileFromCurrentAsync");
        assert(((TextBlock)Field(window, "_profileDraftLabel")!).Text.Contains("尚未保存"),
            "creating local profile under a hardware barrier only reads saved settings");

        client.Status = ColdStatus() with { Fan = ColdStatus().Fan with { Mode = ControlMode.PureManual }, SafetyProtection = new(false, true, false, false, null, null) };
        Render(window, client.Status);
        assert(Button(window, "FanOffButton").IsEnabled && Button(window, "ApplyLightingButton").IsEnabled,
            "puremanual uses the same explicit power and RGB capabilities without a latch");
        client.Status = client.Status with { Fan = client.Status.Fan with { Mode = ControlMode.Released } };
        Render(window, client.Status);
        assert(!Button(window, "FanOnButton").IsEnabled && !Button(window, "ApplyManualSpeedButton").IsEnabled &&
            Button(window, "FanOnButton").ToolTip.ToString()!.Contains("先选择纯手动"),
            "released ownership still requires explicit puremanual selection before device actions");
        var emptyWindow = new MainWindow(new ManualFakeClient()); Render(emptyWindow, ColdStatus());
        assert(((Button)Field(emptyWindow, "_profileSave")!).ToolTip.ToString()!.Contains("选择或创建"),
            "empty local profile editor explains its disabled actions");
        return 8;
    }

    private static ControllerStatus ColdStatus() => new("SafetyProtectionDisabled", true, "synthetic",
        new(false, "374A", "B101", "1602", true), new(ControlMode.Manual, 100, null, null, 59, false),
        new(83.5, [80], 83.5, true, DateTimeOffset.UtcNow), new(false, FailSafeReason.None, null), null, null,
        new(false, null, null), Software: new("synthetic", UiCompatibility.ContractVersion, true, true, true, true, FanLightingLinkSupported: true),
        SafetyProtection: new(true, true, true, false, null, DateTimeOffset.UtcNow));
    private static void Render(MainWindow window, ControllerStatus status)
    {
        typeof(MainWindow).GetMethod("RenderStatus", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [status]);
        typeof(MainWindow).GetMethod("SetConnectionState", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [true, "本地服务已连接"]);
    }
    private static object? Field(MainWindow window, string name) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window);
    private static Button Button(MainWindow window, string name) => (Button)window.FindName(name);
    private static string Text(MainWindow window, string name) => ((TextBlock)window.FindName(name)).Text;
    private static string ProfileText(MainWindow window) => ((TextBlock)Field(window, "_profileResult")!).Text;
    private static void Event(MainWindow window, string name) => typeof(MainWindow).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [window, new RoutedEventArgs()]);
    private static void TaskCall(MainWindow window, string name, params object[] args) =>
        ((Task)typeof(MainWindow).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, args)!).GetAwaiter().GetResult();
    private static void Capture(MainWindow window, string page, string name, string output)
    {
        window.NavigateToPage(page);
        var root = (FrameworkElement)window.Content; ((Panel)root).Background = window.Background;
        foreach (var (width, height) in new[] { (980, 640), (1180, 750) })
        {
            root.Measure(new Size(width, height)); root.Arrange(new Rect(0, 0, width, height)); root.UpdateLayout();
            var image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); image.Render(root);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
            using var stream = new FileStream(Path.Combine(output, name.Replace("980", width.ToString()) + ".png"), FileMode.CreateNew); encoder.Save(stream);
        }
    }

    private sealed class ManualFakeClient : IPipeApiClient
    {
        internal ControllerStatus Status = ColdStatus();
        internal readonly List<string> Operations = [];
        internal readonly List<bool> PowerTargets = [];
        internal readonly List<byte> SpeedTargets = [];
        internal readonly List<LightingSettings> LightingWrites = [];
        internal LightingSettings? CachedLighting = new(true, 1, 2, 150, 2);
        internal bool ReturnLightingSupported;
        internal bool ReturnLightingLinkResult = true;
        internal ProfileApplyRequest? AppliedProfile;
        public Task<PipeResponse> SendAsync(PipeRequest request, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Operations.Add(request.Operation);
            object result;
            switch (request.Operation)
            {
                case PipeOperations.StatusGet: result = Status; break;
                case PipeOperations.CurveGet: result = Curve; break;
                case PipeOperations.PowerSet:
                    var on = request.Payload.GetProperty("poweredOn").GetBoolean(); PowerTargets.Add(on);
                    Status = Status with { Device = Status.Device with { Connected = true },
                        Fan = Status.Fan with { PoweredOn = on, ReportedControlPercent = on ? Status.Fan.ConfiguredManualPercent : null },
                        LastSuccessfulRead = DateTimeOffset.UtcNow };
                    result = ReturnLightingLinkResult
                        ? new { readbackConfirmed = true, poweredOn = on, lightingLinked = true, lightingRestored = on }
                        : new { readbackConfirmed = true, poweredOn = on }; break;
                case PipeOperations.SpeedSet:
                    var speed = request.Payload.GetProperty("percent").GetByte(); SpeedTargets.Add(speed);
                    Status = Status with { Device = Status.Device with { Connected = true },
                        Fan = Status.Fan with { Mode = Status.Fan.Mode == ControlMode.PureManual ? ControlMode.PureManual : ControlMode.Manual,
                            RequestedPercent = speed, ConfiguredManualPercent = speed, ReportedControlPercent = speed, PoweredOn = true }, LastSuccessfulRead = DateTimeOffset.UtcNow };
                    result = Status; break;
                case PipeOperations.ModeSet:
                    Status = Status with { Fan = Status.Fan with { Mode = Enum.Parse<ControlMode>(request.Payload.GetProperty("mode").GetString()!, true) } };
                    result = Status; break;
                case PipeOperations.LightingGet:
                    result = new { supported = ReturnLightingSupported, settings = CachedLighting, settingsFromPersistedCache = true }; break;
                case PipeOperations.LightingSet:
                    CachedLighting = request.Payload.Deserialize<LightingSettings>(JsonOptions)!; LightingWrites.Add(CachedLighting);
                    Status = Status with { Device = Status.Device with { Connected = true }, Fan = Status.Fan with { PoweredOn = true } };
                    result = new { readbackConfirmed = true }; break;
                case PipeOperations.ProfileApply:
                    AppliedProfile = request.Payload.Deserialize<ProfileApplyRequest>(JsonOptions)!;
                    var profile = AppliedProfile.Profile;
                    var application = new ProfileApplicationStatus(request.RequestId, profile.Id, profile.Name, ProfileApplyStage.Finished,
                        ProfileApplyOutcome.Completed, ProfileConfigurationState.Saved, ProfileFanState.Confirmed,
                        profile.Lighting is null ? ProfileLightingState.NotRequested : ProfileLightingState.Confirmed, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
                    Status = Status with { Device = Status.Device with { Connected = true }, Fan = Status.Fan with
                        { Mode = ControlMode.Manual, ConfiguredManualPercent = profile.ManualSpeedPercent, ReportedControlPercent = profile.ManualSpeedPercent, PoweredOn = true }, ProfileApplication = application };
                    result = new ProfileApplyResult(application, Status); break;
                case PipeOperations.HistoryAnalyze:
                    result = new CurveAdvice(DateTimeOffset.UtcNow, 100, 80, Curve, Curve, [new(50, 60, 62, 10, "synthetic")], "synthetic"); break;
                case PipeOperations.CurveAdviceApply:
                    Status = Status with { Fan = Status.Fan with { Mode = ControlMode.Auto } }; result = Status; break;
                default: throw new InvalidOperationException("Unexpected offline request: " + request.Operation);
            }
            return Task.FromResult(new PipeResponse(PipeProtocol.ApiVersion, request.RequestId, true,
                JsonSerializer.SerializeToElement(result, JsonOptions), null, DateTimeOffset.UtcNow));
        }
    }
}
