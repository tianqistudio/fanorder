using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using V12UltraController.Core.Control;
using V12UltraController.Core.Ipc;
using V12UltraController.Core.Status;
using V12UltraController.Settings;

internal static class UiManualPriorityChecks
{
    internal static int Run(Action<bool, string> assert, string output)
    {
        var context = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        try { Check(assert, output); }
        finally { SynchronizationContext.SetSynchronizationContext(context); }
        return 2;
    }

    private static void Check(Action<bool, string> assert, string output)
    {
        var client = new ManualClient();
        var window = Create(client);
        assert(Button(window).IsEnabled && !Button(window).ToolTip.ToString()!.Contains("先选择"),
            "automatic mode allows an explicit manual speed without a preliminary mode switch");
        Apply(window, 58); Idle(window);
        assert(client.SpeedTargets.SequenceEqual([58]) && client.Requests.Count(operation => operation == PipeOperations.ModeSet) == 0,
            "automatic-to-manual sends one SpeedSet and no separate ModeSet");
        assert(Status(window).Fan.Mode == ControlMode.Manual && Text(window, "CurrentControlModeText") == "手动控制" &&
            client.Status.SafetyProtection?.Disabled == false && !Dirty(window),
            "confirmed speed response updates manual mode while retaining enabled protection");

        foreach (var mode in new[] { ControlMode.Manual, ControlMode.Auto })
        {
            var active = Automatic() with { Commissioned = false,
                Commissioning = new(true, "synthetic-pairing", "Probed", DateTimeOffset.UtcNow.AddMinutes(1)) };
            client.Status = active; Render(window, active);
            assert(!Button(window).IsEnabled, "active pairing does not enable an explicit manual speed");
            client.Status = Automatic() with { Fan = Automatic().Fan with { Mode = mode },
                Commissioning = new(false, null, null, null) };
            Render(window, client.Status);
            assert(Button(window).IsEnabled,
                $"one confirmed reconnect from pairing restores {mode} speed availability without a second status refresh");
        }

        client.Status = Automatic() with { Device = client.Status.Device with { Connected = false },
            SafetyProtection = new(true, true, true, false, null, DateTimeOffset.UtcNow) };
        Render(window, client.Status);
        assert(Button(window).IsEnabled, "paused automatic mode can explicitly reconnect for manual speed");
        Apply(window, 59); Idle(window);
        assert(Status(window).Fan.Mode == ControlMode.Manual && Status(window).SafetyProtection?.Disabled == true,
            "automatic-to-manual under the stop latch does not restore protection");
        Capture(window, output, "manual-priority-confirmed-980");

        client.Status = client.Status with { Fan = client.Status.Fan with { Mode = ControlMode.PureManual } };
        Render(window, client.Status); Apply(window, 60); Idle(window);
        assert(Status(window).Fan.Mode == ControlMode.PureManual && Status(window).SafetyProtection?.Disabled == true,
            "explicit speed preserves PureManual and the independent disabled protection setting");

        client.Status = Automatic() with { SafetyProtection = new(true, true, true, false, null, DateTimeOffset.UtcNow) };
        client.Actual = 64; Render(window, client.Status); Apply(window, 52); Idle(window);
        assert(Status(window).Fan.ConfiguredManualPercent == 64 && Status(window).Fan.Mode == ControlMode.Manual &&
            Input(window).Text == "52" && Dirty(window),
            "a different valid device result becomes the current target while the original input remains a draft");
        assert(Text(window, "ActivityText").Contains("设备最终为 64%") && Text(window, "ActivityText").Contains("原输入 52% 未确认") &&
            Text(window, "PageFeedbackText").Contains("已转手动") && Status(window).SafetyProtection?.Disabled == true,
            "different confirmed speed reports the device result and never claims the requested value succeeded");
        Capture(window, output, "manual-priority-device-wins-980");

        Render(window, client.Status with { ServiceState = "ManualObservationSaveUnconfirmed",
            Fan = client.Status.Fan with { ConfiguredManualPercent = 59 } });
        assert(Text(window, "GlobalStatusText") == "已保留设备手动设置，但保存未确认；重启后可能恢复原配置" &&
            ((Border)window.FindName("GlobalStatusBanner")).Visibility == Visibility.Visible &&
            Status(window).SafetyProtection?.Disabled == true,
            "manual observation persistence warning is visible above an independently disabled protection status");

        foreach (var invalidResponse in new[] { "missing", "automatic", "unverified" })
        {
            client.Status = Automatic(); client.InvalidResponse = invalidResponse; Render(window, client.Status);
            var before = client.SpeedTargets.Count;
            Apply(window, 53); Idle(window);
            assert(client.SpeedTargets.Count == before + 1 && Dirty(window) &&
                Text(window, "PageFeedbackText").Length > 0 && !Text(window, "ActivityText").Contains("已确认。"),
                $"{invalidResponse} speed response preserves the draft and is not reported as confirmed success");
        }
        client.InvalidResponse = null; client.Actual = null;
        foreach (var blocked in new[]
        {
            Automatic() with { Fan = Automatic().Fan with { Mode = ControlMode.Released } },
            Automatic() with { HardwareSafety = new(true, "synthetic recovery barrier", DateTimeOffset.UtcNow) },
            Automatic() with { Software = Automatic().Software! with { UiContractVersion = 999 } },
            Automatic() with { AuditMutation = new(true, true, "synthetic", "test", "pending", "synthetic barrier") },
        })
        {
            client.Status = blocked; Render(window, blocked); Input(window).Text = "57";
            var before = client.SpeedTargets.Count;
            assert(!Button(window).IsEnabled, "existing release, recovery and protocol conditions still disable manual submission");
            Click(window); Idle(window);
            assert(client.SpeedTargets.Count == before, "blocked direct manual handler sends no speed request");
        }
        client.Status = Automatic(); Render(window, client.Status); Input(window).Text = "101";
        var beforeInvalid = client.SpeedTargets.Count; Click(window); Idle(window);
        assert(client.SpeedTargets.Count == beforeInvalid && !Button(window).IsEnabled,
            "automatic mode does not bypass the manual percentage input bounds");

        foreach (var failOldRead in new[] { false, true })
        {
            var delayed = new ManualClient { DelayStatusCount = 2 };
            var delayedWindow = Create(delayed);
            var oldRefresh = (Task)Invoke(delayedWindow, "RefreshStatusAsync", false, false, false)!;
            assert(delayed.Pending.Count == 1, "synthetic old automatic status read is held before manual submission");
            Apply(delayedWindow, 55);
            assert(Status(delayedWindow).Fan.Mode == ControlMode.Manual && Status(delayedWindow).Fan.ConfiguredManualPercent == 55,
                "speed acknowledgement updates the display without waiting for an older status callback");
            delayed.Complete(0, failOldRead);
            PumpUntil(() => delayed.Pending.Count == 2);
            assert(Status(delayedWindow).Fan.Mode == ControlMode.Manual && Status(delayedWindow).Fan.ConfiguredManualPercent == 55 &&
                Text(delayedWindow, "ActivityText").Contains("55% 已确认"),
                failOldRead ? "obsolete status failure cannot disconnect or replace the confirmed manual result" :
                    "obsolete automatic status callback cannot repaint the confirmed manual mode");
            delayed.Complete(1, false); Idle(delayedWindow); oldRefresh.GetAwaiter().GetResult();
            assert(delayed.SpeedTargets.SequenceEqual([55]) && delayed.Requests.All(operation => operation is PipeOperations.SpeedSet or PipeOperations.StatusGet),
                "refresh ordering protection introduces no extra control commands or retries");
        }

        var trayClient = new ManualClient { DelaySpeed = true, Actual = 64 };
        var trayWindow = Create(trayClient);
        trayWindow.QueueTrayWheel(120);
        var trayFlush = trayWindow.FlushTraySpeedAsync();
        Input(trayWindow).Text = "72";
        trayWindow.QueueTrayWheel(120);
        trayClient.CompleteSpeed(); PumpUntil(() => trayFlush.IsCompleted); trayFlush.GetAwaiter().GetResult();
        assert(Status(trayWindow).Fan.ConfiguredManualPercent == 64 && Status(trayWindow).Fan.Mode == ControlMode.Manual &&
            Input(trayWindow).Text == "72" && Dirty(trayWindow) && Text(trayWindow, "ActivityText").Contains("托盘请求 60% 未确认"),
            "late tray response adopts the actual physical speed without replacing a newer window draft");
        trayWindow.FlushTraySpeedAsync().GetAwaiter().GetResult();
        assert(trayClient.SpeedTargets.SequenceEqual([60]) && Field(trayWindow, "trayPendingPercent") is null,
            "a different confirmed physical speed discards wheel deltas based on the superseded target");

        var trayDelayed = new ManualClient { DelayStatusCount = 2 };
        var trayDelayedWindow = Create(trayDelayed);
        var trayOldRefresh = (Task)Invoke(trayDelayedWindow, "RefreshStatusAsync", false, false, false)!;
        trayDelayedWindow.QueueTrayWheel(120); var pendingTray = trayDelayedWindow.FlushTraySpeedAsync();
        trayDelayed.Complete(0, false); PumpUntil(() => trayDelayed.Pending.Count == 2);
        assert(Status(trayDelayedWindow).Fan.Mode == ControlMode.Manual && Status(trayDelayedWindow).Fan.ConfiguredManualPercent == 60,
            "a status callback issued before a tray request cannot restore the previous automatic mode");
        trayDelayed.Complete(1, false); PumpUntil(() => pendingTray.IsCompleted); pendingTray.GetAwaiter().GetResult();
        trayOldRefresh.GetAwaiter().GetResult();

        var trayInvalid = new ManualClient { DelaySpeed = true, Actual = 64, InvalidResponse = "unverified" };
        var trayInvalidWindow = Create(trayInvalid);
        trayInvalidWindow.QueueTrayWheel(120); var invalidTray = trayInvalidWindow.FlushTraySpeedAsync();
        trayInvalidWindow.QueueTrayWheel(120);
        trayInvalid.CompleteSpeed(); PumpUntil(() => invalidTray.IsCompleted); invalidTray.GetAwaiter().GetResult();
        trayInvalidWindow.FlushTraySpeedAsync().GetAwaiter().GetResult();
        assert(trayInvalid.SpeedTargets.SequenceEqual([60]) && Field(trayInvalidWindow, "trayPendingPercent") is null &&
            Text(trayInvalidWindow, "ActivityText").Contains("没有有效设备确认"),
            "unverified different tray result is not adopted and cannot replay queued wheel input");
    }

    private static ControllerStatus Automatic()
    {
        var now = DateTimeOffset.UtcNow;
        return new("Synthetic", true, "service (synthetic)", new(true, "374A", "B101", "1602", true),
            new(ControlMode.Auto, 59, 59, true, 59, true), new(54, [52.0], 54, true, now),
            new(false, FailSafeReason.None, null), now, now, new(false, null, null),
            Software: new("manual-priority-test", UiCompatibility.ContractVersion, true, true, true, true),
            SafetyProtection: new(false, true, false, false, null, null));
    }
    private static MainWindow Create(ManualClient client)
    {
        var window = new MainWindow(client); Render(window, client.Status); window.NavigateToPage("fan"); return window;
    }
    private static void Render(MainWindow window, ControllerStatus status)
    {
        Invoke(window, "RenderStatus", status); Invoke(window, "SetConnectionState", true, "本地服务已连接");
    }
    private static void Apply(MainWindow window, int value) { Input(window).Text = value.ToString(); Click(window); }
    private static void Click(MainWindow window) => Invoke(window, "ApplyManualSpeedButton_Click", window, new RoutedEventArgs());
    private static TextBox Input(MainWindow window) => (TextBox)window.FindName("ManualSpeedInput");
    private static Button Button(MainWindow window) => (Button)window.FindName("ApplyManualSpeedButton");
    private static string Text(MainWindow window, string name) => ((TextBlock)window.FindName(name)).Text;
    private static ControllerStatus Status(MainWindow window) => (ControllerStatus)Field(window, "_lastStatus")!;
    private static bool Dirty(MainWindow window) => (bool)Field(window, "_manualSpeedDirty")!;
    private static object? Field(MainWindow window, string name) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window);
    private static object? Invoke(MainWindow window, string name, params object[] arguments) =>
        typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, arguments);
    private static void Idle(MainWindow window) => PumpUntil(() => !(bool)Field(window, "_commandInProgress")!);
    private static void PumpUntil(Func<bool> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (!done())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Synthetic manual-priority request did not complete.");
            Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
        }
    }
    private static void Capture(MainWindow window, string output, string name)
    {
        var root = (FrameworkElement)window.Content; ((Panel)root).Background = window.Background;
        root.Measure(new Size(980, 640)); root.Arrange(new Rect(0, 0, 980, 640)); root.UpdateLayout();
        var bitmap = new RenderTargetBitmap(980, 640, 96, 96, PixelFormats.Pbgra32); bitmap.Render(root);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new FileStream(Path.Combine(output, name + ".png"), FileMode.CreateNew); encoder.Save(stream);
    }
    private sealed class ManualClient : IPipeApiClient
    {
        internal ControllerStatus Status = Automatic();
        internal readonly List<string> Requests = [];
        internal readonly List<int> SpeedTargets = [];
        internal readonly List<(PipeRequest Request, ControllerStatus Snapshot, TaskCompletionSource<PipeResponse> Completion)> Pending = [];
        internal byte? Actual;
        internal string? InvalidResponse;
        internal int DelayStatusCount;
        internal bool DelaySpeed;
        private (PipeRequest Request, TaskCompletionSource<PipeResponse> Completion)? pendingSpeed;
        public Task<PipeResponse> SendAsync(PipeRequest request, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Requests.Add(request.Operation);
            if (request.Operation == PipeOperations.StatusGet)
            {
                if (DelayStatusCount-- > 0)
                {
                    var completion = new TaskCompletionSource<PipeResponse>(); Pending.Add((request, Status, completion)); return completion.Task;
                }
                return Task.FromResult(PipeResponse.Success(request.RequestId, Status));
            }
            if (request.Operation != PipeOperations.SpeedSet) throw new InvalidOperationException("Unexpected manual-only IPC: " + request.Operation);
            var requested = request.Payload.GetProperty("percent").GetByte(); SpeedTargets.Add(requested);
            if (DelaySpeed)
            {
                DelaySpeed = false;
                var completion = new TaskCompletionSource<PipeResponse>(); pendingSpeed = (request, completion); return completion.Task;
            }
            return Task.FromResult(SpeedResponse(request));
        }
        private PipeResponse SpeedResponse(PipeRequest request)
        {
            var requested = request.Payload.GetProperty("percent").GetByte();
            if (InvalidResponse == "missing") return PipeResponse.Success(request.RequestId, new { saved = true });
            var actual = Actual ?? requested;
            Status = Status with { Device = Status.Device with { Connected = true },
                Fan = Status.Fan with { Mode = InvalidResponse == "automatic" ? ControlMode.Auto :
                    Status.Fan.Mode == ControlMode.PureManual ? ControlMode.PureManual : ControlMode.Manual,
                    ConfiguredManualPercent = actual, RequestedPercent = actual,
                    ReportedControlPercent = InvalidResponse == "unverified" ? null : actual, PoweredOn = true },
                LastSuccessfulRead = DateTimeOffset.UtcNow };
            return PipeResponse.Success(request.RequestId, Status);
        }
        internal void CompleteSpeed()
        {
            var pending = pendingSpeed ?? throw new InvalidOperationException("No synthetic speed is pending.");
            pendingSpeed = null;
            pending.Completion.SetResult(SpeedResponse(pending.Request));
        }
        internal void Complete(int index, bool fail)
        {
            var pending = Pending[index];
            if (fail) pending.Completion.SetException(new IOException("synthetic obsolete status failure"));
            else pending.Completion.SetResult(PipeResponse.Success(pending.Request.RequestId, pending.Snapshot));
        }
    }
}
