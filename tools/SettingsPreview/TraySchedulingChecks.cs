using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using V12UltraController.Core.Control;
using V12UltraController.Core.Ipc;
using V12UltraController.Core.Status;
using V12UltraController.Settings;

internal static class TraySchedulingChecks
{
    internal static void Run(Action<bool, string> assert)
    {
        var prior = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        try { Check(assert); }
        finally { SynchronizationContext.SetSynchronizationContext(prior); }
    }

    private static void Check(Action<bool, string> assert)
    {
        var stale = new TrayClient();
        stale.Status = stale.Status with { SafetyProtection = null };
        var staleWindow = Create(stale);
        var staleNotices = new List<string>();
        staleWindow.TrayNoticeObserved = (_, message) => staleNotices.Add(message);
        Set(staleWindow, "trayStatusReceived", DateTimeOffset.UtcNow.AddSeconds(-6));
        staleWindow.QueueTrayWheel(-120);
        PumpUntil(() => !(bool)Field(staleWindow, "trayRefreshingStatus")!);
        assert(stale.Targets.Count == 0 && stale.Operations.Contains(PipeOperations.StatusGet) &&
            staleNotices.Single().Contains("状态已过期") && Field(staleWindow, "trayPendingPercent") is null,
            "stale tray input refreshes status and discards the gesture without replaying a speed write");
        staleWindow.QueueTrayWheel(-120);
        PumpUntil(() => stale.Targets.Count == 1 && !Busy(staleWindow));
        assert(stale.Targets.SequenceEqual([58]), "a new gesture works after the stale status refresh");

        foreach (var reason in new[] { "protocol", "power", "protection" })
        {
            var blocked = new TrayClient();
            blocked.Status = blocked.Status with { SafetyProtection = null };
            blocked.Status = reason switch
            {
                "protocol" => blocked.Status with { Device = blocked.Status.Device with { ProtocolCompatible = false } },
                "power" => blocked.Status with { Fan = blocked.Status.Fan with { PoweredOn = false } },
                _ => blocked.Status with { FailSafe = new(true, FailSafeReason.HidFailure, DateTimeOffset.UtcNow) },
            };
            var blockedWindow = Create(blocked);
            var messages = new List<string>();
            blockedWindow.TrayNoticeObserved = (_, message) => messages.Add(message);
            blockedWindow.QueueTrayWheel(-120);
            assert(blocked.Targets.Count == 0 && messages.Count == 1 &&
                !messages[0].Contains("当前不可执行") && messages[0].Contains(reason switch
                { "protocol" => "协议", "power" => "开关状态", _ => "保护正在生效" }),
                $"tray {reason} gate explains the actual block and never sends a speed request");
        }

        var continuous = new TrayClient();
        var window = Create(continuous);
        var inputs = 0; var sentBeforeInputStopped = false;
        continuous.SpeedReceived = () => sentBeforeInputStopped |= inputs < 12;
        var producer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(40) };
        producer.Tick += (_, _) =>
        {
            inputs++; window.QueueTrayWheel(-120);
            if (inputs == 12) producer.Stop();
        };
        producer.Start();
        PumpUntil(() => inputs == 12 && Field(window, "trayPendingPercent") is null && !Busy(window));
        assert(sentBeforeInputStopped && continuous.Targets.Count >= 2 && continuous.Targets[^1] == 47,
            "hidden-window dispatcher throttle sends while wheel input continues and preserves the cumulative target");
        assert(!window.IsVisible && new WindowInteropHelper(window).Handle == IntPtr.Zero && Field(window, "tray") is null,
            "automatic tray timer checks use a detached fake window without a native tray, mouse hook or HWND");

        var busyClient = new TrayClient(); var busyWindow = Create(busyClient);
        var text = new List<string>(); busyWindow.TrayTextObserved = text.Add;
        Set(busyWindow, "_commandInProgress", true);
        busyWindow.QueueTrayWheel(-480); PumpFor(TimeSpan.FromMilliseconds(250));
        assert(busyClient.Targets.Count == 0 && Field(busyWindow, "trayPendingPercent") is byte pending && pending == 55 &&
            text.Any(value => value.Contains("等待当前操作完成")),
            "ordinary command busy retains the queued wheel target and explains why it is waiting");
        Set(busyWindow, "_commandInProgress", false);
        PumpUntil(() => busyClient.Targets.Count == 1 && Field(busyWindow, "trayPendingPercent") is null && !Busy(busyWindow));
        assert(busyClient.Targets.SequenceEqual([55]) && !busyClient.Operations.Contains(PipeOperations.ModeSet),
            "queued wheel input sends automatically after ordinary busy ends without a manual mode request");

        var delayed = new TrayClient { DelayNextSpeed = true }; var delayedWindow = Create(delayed);
        delayedWindow.QueueTrayWheel(-120);
        PumpUntil(() => delayed.PendingSpeed is not null);
        delayedWindow.QueueTrayWheel(-240);
        delayed.CompleteSpeed();
        PumpUntil(() => delayed.Targets.Count == 2 && Field(delayedWindow, "trayPendingPercent") is null && !Busy(delayedWindow));
        assert(delayed.Targets.SequenceEqual([58, 56]),
            "wheel deltas received in flight remain cumulative and send on the next timer tick");

        var rejected = new TrayClient { RejectUnchanged = true }; var rejectedWindow = Create(rejected);
        var notices = new List<string>(); var rejectedText = new List<string>();
        rejectedWindow.TrayNoticeObserved = (_, message) => notices.Add(message);
        rejectedWindow.TrayTextObserved = rejectedText.Add;
        rejectedWindow.QueueTrayWheel(-480);
        PumpUntil(() => rejected.Targets.Count == 1 && Field(rejectedWindow, "trayPendingPercent") is null && !Busy(rejectedWindow));
        assert(rejected.Status.Fan.ConfiguredManualPercent == 59 && notices.Count == 1 &&
            notices[0].Contains("55% 未确认") && notices[0].Contains("仍为 59%") &&
            rejectedText.Any(value => value.Contains("正在发送 55%")) && rejectedText[^1].Contains("55% 未确认"),
            "unchanged baseline 59 rejecting target 55 reports an observable tray notice and persistent unconfirmed tooltip");
        PumpFor(TimeSpan.FromMilliseconds(250));
        assert(rejected.Targets.SequenceEqual([55]) && !rejectedText[^1].Contains("已保存"),
            "rejected unchanged readback is never presented as success or retried automatically");
        rejected.RejectUnchanged = false; rejectedWindow.QueueTrayWheel(-120);
        PumpUntil(() => rejected.Targets.Count == 2 && Field(rejectedWindow, "trayPendingPercent") is null && !Busy(rejectedWindow));
        assert(rejected.Targets.SequenceEqual([55, 58]) && !rejected.Operations.Contains(PipeOperations.ModeSet) &&
            rejected.Status.SafetyProtection?.Disabled == true,
            "a fresh wheel gesture can retry from the actual baseline without UI ModeSet or protection changes");

        var background = new TrayClient(); var backgroundWindow = Create(background);
        var backgroundNotices = new List<string>();
        backgroundWindow.TrayNoticeObserved = (_, message) => backgroundNotices.Add(message);
        var unconfirmed = background.Status with { ServiceState = "ManualCommandUnconfirmed" };
        Render(backgroundWindow, unconfirmed); Render(backgroundWindow, unconfirmed);
        assert(backgroundNotices.Count == 1 && backgroundNotices[0].Contains("设备仍保持原档位") &&
            ((TextBlock)backgroundWindow.FindName("GlobalStatusText")).Text.Contains("调速目标未获设备确认"),
            "asynchronous command-unconfirmed status is visible above disabled protection and emits one tray notice per transition");
    }

    private static MainWindow Create(TrayClient client)
    {
        var window = new MainWindow(client); Render(window, client.Status); window.InitializeTrayTimer(); return window;
    }
    private static void Render(MainWindow window, ControllerStatus status) =>
        typeof(MainWindow).GetMethod("RenderStatus", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [status]);
    private static object? Field(MainWindow window, string name) =>
        typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window);
    private static void Set(MainWindow window, string name, object value) =>
        typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, value);
    private static bool Busy(MainWindow window) => (bool)Field(window, "_commandInProgress")!;
    private static void PumpUntil(Func<bool> completed)
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        while (!completed())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("Synthetic tray scheduling did not complete.");
            Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
        }
    }
    private static void PumpFor(TimeSpan duration)
    {
        var finished = false;
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = duration };
        timer.Tick += (_, _) => { timer.Stop(); finished = true; };
        timer.Start(); PumpUntil(() => finished);
    }
    private sealed class TrayClient : IPipeApiClient
    {
        internal ControllerStatus Status;
        internal readonly List<int> Targets = [];
        internal readonly List<string> Operations = [];
        internal bool RejectUnchanged, DelayNextSpeed;
        internal Action? SpeedReceived;
        internal TaskCompletionSource<PipeResponse>? PendingSpeed;
        private PipeRequest? pendingRequest;
        internal TrayClient()
        {
            var now = DateTimeOffset.UtcNow;
            Status = new("Synthetic", true, "service (synthetic)", new(true, "374A", "B101", "1602", true),
                new(ControlMode.Manual, 59, 59, true, 59, true), new(54, [52.0], 54, true, now),
                new(false, FailSafeReason.None, null), now, now, new(false, null, null),
                Software: new("tray-scheduling-test", UiCompatibility.ContractVersion, true, true, true, true),
                SafetyProtection: new(true, true, true, false, null, now));
        }
        public Task<PipeResponse> SendAsync(PipeRequest request, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Operations.Add(request.Operation);
            if (request.Operation == PipeOperations.StatusGet) return Task.FromResult(PipeResponse.Success(request.RequestId, Status));
            if (request.Operation != PipeOperations.SpeedSet) throw new InvalidOperationException("Unexpected fake tray IPC: " + request.Operation);
            Targets.Add(request.Payload.GetProperty("percent").GetInt32()); SpeedReceived?.Invoke();
            if (DelayNextSpeed)
            {
                DelayNextSpeed = false; pendingRequest = request; PendingSpeed = new(); return PendingSpeed.Task;
            }
            return Task.FromResult(Complete(request));
        }
        private PipeResponse Complete(PipeRequest request)
        {
            var target = request.Payload.GetProperty("percent").GetByte();
            if (RejectUnchanged) return PipeResponse.Failure(request.RequestId, PipeErrorCodes.DeviceStateChanged,
                $"请求 {target}% 未生效；设备仍为 59%，没有新实体输入证据。");
            Status = Status with { Fan = Status.Fan with { Mode = ControlMode.Manual, RequestedPercent = target,
                ConfiguredManualPercent = target, ReportedControlPercent = target }, LastSuccessfulRead = DateTimeOffset.UtcNow };
            return PipeResponse.Success(request.RequestId, Status);
        }
        internal void CompleteSpeed()
        {
            var completion = PendingSpeed!; PendingSpeed = null;
            completion.SetResult(Complete(pendingRequest!)); pendingRequest = null;
        }
    }
}
