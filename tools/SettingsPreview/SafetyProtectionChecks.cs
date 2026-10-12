using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using V12UltraController.Core.Configuration;
using V12UltraController.Core.Control;
using V12UltraController.Core.Ipc;
using V12UltraController.Core.Status;
using V12UltraController.Settings;

internal static class SafetyProtectionChecks
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    { Converters = { new JsonStringEnumConverter() } };

    internal static int Run(Action<bool, string> assert, string output)
    {
        var oldContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        try { RunChecks(assert, output); }
        finally { SynchronizationContext.SetSynchronizationContext(oldContext); }
        return 3;
    }

    private static void RunChecks(Action<bool, string> assert, string output)
    {
        var client = new SafetyFakeClient();
        var window = Create(client);
        assert(Button(window, "DisableSafetyProtectionButton").IsEnabled,
            "active fail-safe in auto mode still permits priority disable");
        assert(Button(window, "DisableSafetyProtectionButton").ToolTip.ToString()!.Contains("59%"),
            "disable tooltip names the saved manual target");
        Field(window, "trayPendingPercent", (byte?)82);
        ((Slider)window.FindName("ManualSpeedSlider")).Value = 67;
        Wait(window.DisableSafetyProtectionAsync());
        assert(client.Operations.SequenceEqual([PipeOperations.SafetyDisable, PipeOperations.SpeedSet]),
            "disable is confirmed before exactly one explicit saved-speed request");
        assert(client.SpeedTargets.SequenceEqual([(byte)59]) && client.Status.Fan.Mode == ControlMode.Auto,
            "disable restores saved manual target and preserves mode");
        assert(((Slider)window.FindName("ManualSpeedSlider")).Value == 67,
            "disable does not overwrite an unsaved manual slider draft");
        assert(ReadField(window, "trayPendingPercent") is null, "priority disable clears delayed tray input");
        assert(Text(window, "ActivityText").Contains("59% 已由设备确认") && Text(window, "FailSafeStateText") == "已禁用",
            "confirmed restore separately reports protection and device readback");
        assert(Button(window, "ApplyManualSpeedButton").IsEnabled && Button(window, "AutoModeButton").IsEnabled &&
            Button(window, "ManualModeButton").IsEnabled && Button(window, "AutoModeButton").Content.ToString()!.Contains("暂停"),
            "latched auto permits manual speed and saved mode selection without resuming automatic output");
        assert(!Button(window, "HomeLightToggleButton").IsEnabled && !Button(window, "HomeLightToggleButton").IsEnabled &&
            !Button(window, "ApplyLightingButton").IsEnabled, "latched state disables lighting output controls");
        assert(Button(window, "ApplyLightingButton").ToolTip.ToString()!.Contains("更新后台"),
            "legacy latched lighting controls explain missing backend capability");
        assert(!Button(window, "DisableSafetyProtectionButton").IsEnabled, "persisted disable cannot repeat");
        assert(Button(window, "DisableSafetyProtectionButton").Visibility == Visibility.Collapsed &&
            Button(window, "ResumeSafetyProtectionButton").Visibility == Visibility.Visible,
            "persisted stop shows only the resume action below the state metric");
        Capture(window, output, "safety-disabled-980", assert);

        var beforeRepeat = client.Operations.Count;
        Wait(window.DisableSafetyProtectionAsync());
        assert(client.Operations.Count == beforeRepeat, "repeated completed disable sends no request");
        window.ConfirmResumeSafetyProtection = () => false;
        Wait(window.ResumeSafetyProtectionAsync());
        assert(client.Operations.Count == beforeRepeat, "declining explicit resume sends no request");
        window.ConfirmResumeSafetyProtection = () => true;
        Wait(window.ResumeSafetyProtectionAsync());
        assert(client.Status.SafetyProtection?.Disabled == false && client.Operations[^1] == PipeOperations.SafetyResume,
            "explicit resume alone clears the protection latch");

        var retryClient = new SafetyFakeClient { Persist = false, RejectSpeed = true };
        var retryWindow = Create(retryClient);
        Wait(retryWindow.DisableSafetyProtectionAsync());
        assert(retryClient.Status.SafetyProtection is { Disabled: true, Persisted: false } &&
            Text(retryWindow, "FailSafeStateText") == "临时禁用", "failed marker persistence reports a short temporary state");
        assert(Text(retryWindow, "ActivityText").Contains("未确认恢复") && !Text(retryWindow, "ActivityText").Contains("已由设备确认"),
            "speed failure does not erase the successful stop or claim a lower speed");
        assert(Text(retryWindow, "FailSafeReasonText").Contains("未持久保存") && Button(retryWindow, "DisableSafetyProtectionButton").Content.ToString() == "重试保存",
            "temporary disable retains persistence details behind the help entry");
        assert(Button(retryWindow, "DisableSafetyProtectionButton").Visibility == Visibility.Visible &&
            Button(retryWindow, "ResumeSafetyProtectionButton").Visibility == Visibility.Visible &&
            Button(retryWindow, "ResumeSafetyProtectionButton").Margin.Left == 0,
            "temporary stop keeps both recovery actions without a wrapped-line indent");
        Capture(retryWindow, output, "safety-temporary-980", assert);
        retryClient.Status = retryClient.Status with { Fan = retryClient.Status.Fan with { ConfiguredManualPercent = 42, ReportedControlPercent = 42 } };
        Render(retryWindow, retryClient.Status);
        retryClient.Persist = true;
        var speedCount = retryClient.SpeedTargets.Count;
        Wait(retryWindow.DisableSafetyProtectionAsync());
        assert(retryClient.SpeedTargets.Count == speedCount && retryClient.Status.Fan.ReportedControlPercent == 42 &&
            retryClient.Operations[^1] == PipeOperations.SafetyDisable, "retrying persistence never replays saved-speed restoration");

        var barriers = new SafetyFakeClient { RejectSpeed = true };
        barriers.Status = barriers.Status with { Device = barriers.Status.Device with { Connected = false },
            HardwareSafety = new(true, "合成硬件屏障", DateTimeOffset.UtcNow),
            AuditMutation = new(true, true, "fake-audit", "fake", "blocked", "合成审计屏障") };
        var blockedWindow = Create(barriers);
        Field(blockedWindow, "_commandInProgress", true);
        Invoke(blockedWindow, "UpdateControlButtonsForBusyState");
        assert(Button(blockedWindow, "DisableSafetyProtectionButton").IsEnabled,
            "priority disable remains enabled during commands and hardware/audit/disconnection barriers");
        Wait(blockedWindow.DisableSafetyProtectionAsync());
        assert(barriers.Operations[0] == PipeOperations.SafetyDisable && Text(blockedWindow, "ActivityText").Contains("安全保护已禁用"),
            "blocked speed restoration does not prevent priority disable acknowledgement");
        Field(blockedWindow, "_commandInProgress", false);
        blockedWindow.ConfirmResumeSafetyProtection = () => true;
        Wait(blockedWindow.ResumeSafetyProtectionAsync());
        assert(Text(blockedWindow, "ActivityText").Contains("仍有故障待恢复") && barriers.Status.HardwareSafety.Poisoned,
            "resuming protection preserves and describes original recovery barriers");

        var legacy = new SafetyFakeClient();
        legacy.Status = legacy.Status with { Software = legacy.Status.Software! with { SafetyDisableSupported = false } };
        var legacyWindow = Create(legacy);
        Wait(legacyWindow.DisableSafetyProtectionAsync());
        assert(legacy.Operations.Count == 0 && !Button(legacyWindow, "DisableSafetyProtectionButton").IsEnabled,
            "legacy backend cannot be assumed to support priority disable");
        foreach (var mode in new[] { ControlMode.PureManual, ControlMode.Released })
        {
            var passive = new SafetyFakeClient { RejectSpeed = mode == ControlMode.Released };
            passive.Status = passive.Status with { Fan = passive.Status.Fan with { Mode = mode },
                Device = passive.Status.Device with { Connected = false } };
            var passiveWindow = Create(passive);
            assert(Button(passiveWindow, "DisableSafetyProtectionButton").IsEnabled,
                "passive mode is not confused with the independent protection latch");
            Wait(passiveWindow.DisableSafetyProtectionAsync());
            assert(passive.Status.SafetyProtection?.Disabled == true && !passive.Operations.Contains(PipeOperations.ModeSet),
                "passive priority disable does not silently select a new control mode");
        }

        var ambiguous = new SafetyFakeClient { RejectDisable = true };
        var ambiguousWindow = Create(ambiguous);
        Wait(ambiguousWindow.DisableSafetyProtectionAsync());
        assert(ambiguous.SpeedTargets.Count == 0 && Text(ambiguousWindow, "FailSafeStateText") == "禁用未确认",
            "unacknowledged stop never sends a follow-up speed request or claims disabled");
        Capture(ambiguousWindow, output, "safety-unconfirmed-980", assert);
        var unknown = new SafetyFakeClient();
        unknown.Status = unknown.Status with { Fan = unknown.Status.Fan with { ConfiguredManualPercent = null } };
        var unknownWindow = Create(unknown);
        Wait(unknownWindow.DisableSafetyProtectionAsync());
        assert(unknown.SpeedTargets.Count == 0 && Text(unknownWindow, "ActivityText").Contains("未发送调速"),
            "missing saved manual target is never guessed from the fail-safe 100 percent");

        var delayed = new SafetyFakeClient { HoldDisable = true };
        var delayedWindow = Create(delayed);
        var stopTask = delayedWindow.DisableSafetyProtectionAsync();
        assert(!stopTask.IsCompleted && delayed.SpeedTargets.Count == 0, "speed request waits for disable acknowledgement");
        delayed.Status = delayed.Status with { Fan = delayed.Status.Fan with { ConfiguredManualPercent = 73 } };
        delayed.CompleteDisable();
        Wait(stopTask);
        assert(delayed.SpeedTargets.SequenceEqual([(byte)59]), "queued disable restores the target captured at click time");

        var inFlight = new SafetyFakeClient { InFlight = true, RejectSpeed = true };
        var inFlightWindow = Create(inFlight);
        Wait(inFlightWindow.DisableSafetyProtectionAsync());
        assert(Text(inFlightWindow, "FailSafeReasonText").Contains("已发出的操作仍可能完成"),
            "acknowledged disable preserves the already-issued operation caveat");
        var unconfirmed = new SafetyFakeClient { ConfirmStop = false };
        var unconfirmedWindow = Create(unconfirmed);
        Wait(unconfirmedWindow.DisableSafetyProtectionAsync());
        assert(unconfirmed.SpeedTargets.Count == 0 && Text(unconfirmedWindow, "ActivityText").Contains("未确认"),
            "a disabled flag without confirmed automatic stop cannot authorize speed restoration");

        var oldRead = new SafetyFakeClient { HoldStatus = true };
        var oldReadWindow = Create(oldRead);
        var refreshTask = (Task)typeof(MainWindow).GetMethod("RefreshStatusAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(oldReadWindow, [false, false, false])!;
        Wait(oldReadWindow.DisableSafetyProtectionAsync());
        oldRead.CompleteStatus();
        Wait(refreshTask);
        assert(Status(oldReadWindow).SafetyProtection?.Disabled == true && Status(oldReadWindow).Fan.ReportedControlPercent == 59,
            "late ordinary status response cannot replace the confirmed protection stop and restored speed");

        var pending = new SafetyFakeClient { HoldProfile = true };
        var saved = new ControlProfileSnapshot("old-profile", "旧方案", ControlMode.Auto, 88,
            new FanCurve([new(0, 30), new(50, 60), new(90, 100)]), null);
        var pendingWindow = Create(pending, new MemoryControlProfileStore([saved]));
        var profileTask = pendingWindow.ApplySavedProfileAsync(saved.Id);
        assert(!profileTask.IsCompleted, "fake profile remains pending without a real service");
        Wait(pendingWindow.DisableSafetyProtectionAsync());
        pending.CompleteProfile(saved);
        Wait(profileTask);
        assert(Status(pendingWindow).SafetyProtection?.Disabled == true && Text(pendingWindow, "ActivityText").Contains("59% 已由设备确认"),
            "late profile response cannot overwrite priority disable or its final speed result");
        assert(!((TextBlock)ReadField(pendingWindow, "_profileResult")!).Text.Contains("已应用并完成验证"),
            "superseded profile wait never reports current successful application");
    }

    private static MainWindow Create(SafetyFakeClient client, IControlProfileStore? profiles = null)
    {
        var window = new MainWindow(client, profileStore: profiles);
        Render(window, client.Status);
        return window;
    }
    private static Button Button(MainWindow window, string name) => (Button)window.FindName(name);
    private static string Text(MainWindow window, string name) => ((TextBlock)window.FindName(name)).Text;
    private static void Render(MainWindow window, ControllerStatus status) =>
        typeof(MainWindow).GetMethod("RenderStatus", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [status]);
    private static object? Invoke(MainWindow window, string name) =>
        typeof(MainWindow).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null);
    private static void Field(MainWindow window, string name, object? value) =>
        typeof(MainWindow).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, value);
    private static object? ReadField(MainWindow window, string name) =>
        typeof(MainWindow).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window);
    private static ControllerStatus Status(MainWindow window) => (ControllerStatus)ReadField(window, "_lastStatus")!;
    private static void Wait(Task task)
    {
        if (!task.IsCompleted)
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            timer.Tick += (_, _) => frame.Continue = false;
            timer.Start();
            var dispatcher = Dispatcher.CurrentDispatcher;
            _ = task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default);
            Dispatcher.PushFrame(frame);
            timer.Stop();
        }
        if (!task.IsCompleted) throw new InvalidOperationException("Synthetic priority safety task did not complete.");
        task.GetAwaiter().GetResult();
    }
    private static void Capture(MainWindow window, string output, string name, Action<bool, string> assert)
    {
        window.NavigateToPage("overview");
        var root = (FrameworkElement)window.Content;
        ((Panel)root).Background = window.Background;
        var size = new Size(980, 640);
        root.Measure(size); root.Arrange(new Rect(size)); root.UpdateLayout();
        var state = (TextBlock)window.FindName("FailSafeStateText");
        var formatted = new FormattedText(state.Text, System.Globalization.CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight, new Typeface(state.FontFamily, state.FontStyle, state.FontWeight, state.FontStretch),
            state.FontSize, state.Foreground, 1);
        assert(formatted.Width <= state.ActualWidth + 1, "minimum-width safety metric fits without clipping: " + name);
        var bitmap = new RenderTargetBitmap(980, 640, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new FileStream(Path.Combine(output, name + ".png"), FileMode.CreateNew);
        encoder.Save(stream);
    }

    private sealed class SafetyFakeClient : IPipeApiClient
    {
        internal ControllerStatus Status = new("Running", true, "synthetic",
            new(true, "374A", "B101", "1602", true), new(ControlMode.Auto, 100, 100, true, 59, true),
            new(92, [90], 92, true, DateTimeOffset.UtcNow), new(true, FailSafeReason.EmergencyTemperature, DateTimeOffset.UtcNow),
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, new(false, null, null),
            Software: new("synthetic", UiCompatibility.ContractVersion, true, true, true),
            SafetyProtection: new(false, true, false, false, null, null));
        internal readonly List<string> Operations = [];
        internal readonly List<byte> SpeedTargets = [];
        internal bool Persist = true, ConfirmStop = true, InFlight, RejectSpeed, RejectDisable, HoldDisable, HoldProfile, HoldStatus;
        private PipeRequest? pendingDisable, pendingProfile, pendingStatus;
        private ControllerStatus? oldProfileStatus, oldStatus;
        private readonly TaskCompletionSource<PipeResponse> disableCompletion = new();
        private readonly TaskCompletionSource<PipeResponse> profileCompletion = new();
        private readonly TaskCompletionSource<PipeResponse> statusCompletion = new();

        public Task<PipeResponse> SendAsync(PipeRequest request, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Operations.Add(request.Operation);
            if (request.Operation == PipeOperations.StatusGet && HoldStatus)
            {
                HoldStatus = false; pendingStatus = request; oldStatus = Status;
                return statusCompletion.Task;
            }
            if (request.Operation == PipeOperations.ProfileApply && HoldProfile)
            {
                pendingProfile = request; oldProfileStatus = Status;
                return profileCompletion.Task; // Deliberately emulate a late, uncancellable response.
            }
            if (request.Operation == PipeOperations.SafetyDisable)
            {
                if (RejectDisable) return Task.FromException<PipeResponse>(new IOException("合成断连"));
                if (HoldDisable) { pendingDisable = request; return disableCompletion.Task; }
                Disable();
            }
            else if (request.Operation == PipeOperations.SpeedSet)
            {
                var target = request.Payload.GetProperty("percent").GetByte();
                SpeedTargets.Add(target);
                if (RejectSpeed) return Task.FromException<PipeResponse>(new IOException("合成恢复屏障，调速未送达"));
                Status = Status with { Device = Status.Device with { Connected = true },
                    Fan = Status.Fan with { ConfiguredManualPercent = target, ReportedControlPercent = target, RequestedPercent = target, PoweredOn = true },
                    LastSuccessfulRead = DateTimeOffset.UtcNow };
            }
            else if (request.Operation == PipeOperations.SafetyResume)
            {
                if (!request.Payload.GetProperty("confirmed").GetBoolean()) throw new InvalidOperationException("Resume was not confirmed.");
                Status = Status with { SafetyProtection = new(false, true, false, false, null, null) };
            }
            else if (request.Operation != PipeOperations.StatusGet) throw new InvalidOperationException("Unexpected synthetic operation: " + request.Operation);
            return Task.FromResult(Response(request, Status));
        }
        private void Disable() => Status = Status with { SafetyProtection = new(true, Persist, ConfirmStop, InFlight,
            Persist ? null : "合成保存失败", DateTimeOffset.UtcNow) };
        internal void CompleteDisable() { Disable(); disableCompletion.SetResult(Response(pendingDisable!, Status)); }
        internal void CompleteStatus() => statusCompletion.SetResult(Response(pendingStatus!, oldStatus!));
        internal void CompleteProfile(ControlProfileSnapshot profile)
        {
            var application = new ProfileApplicationStatus(pendingProfile!.RequestId, profile.Id, profile.Name,
                ProfileApplyStage.Finished, ProfileApplyOutcome.Completed, ProfileConfigurationState.Saved,
                ProfileFanState.Confirmed, ProfileLightingState.NotRequested, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
            profileCompletion.SetResult(Response(pendingProfile, new ProfileApplyResult(application, oldProfileStatus!)));
        }
        private static PipeResponse Response(PipeRequest request, object data) => new(PipeProtocol.ApiVersion,
            request.RequestId, true, JsonSerializer.SerializeToElement(data, JsonOptions), null, DateTimeOffset.UtcNow);
    }
}
