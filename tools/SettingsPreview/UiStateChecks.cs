using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using V12UltraController.Core.Configuration;
using V12UltraController.Core.Control;
using V12UltraController.Core.Ipc;
using V12UltraController.Core.Status;
using V12UltraController.Settings;

internal static class UiStateChecks
{
    internal static int Run(Action<bool, string> assert, string output)
    {
        var client = new StateFakeClient();
        var window = new MainWindow(client);
        var input = (TextBox)window.FindName("ManualSpeedInput");
        var slider = (Slider)window.FindName("ManualSpeedSlider");
        var baseline = client.Status;
        Render(window, baseline);

        var stopped = baseline with { SafetyProtection = new(true, true, true, false, null, DateTimeOffset.UtcNow) };
        var poisoned = stopped with { HardwareSafety = new(true, "synthetic hardware failure", DateTimeOffset.UtcNow) };
        Render(window, poisoned);
        assert(Text(window, "GlobalStatusText").StartsWith("设备通信异常", StringComparison.Ordinal) &&
            Text(window, "GlobalStatusText").Contains("自动保护仍禁用"),
            "hardware fault remains the leading global message while protection is disabled");
        assert(Element(window, "GlobalStatusBanner").Visibility == Visibility.Visible &&
            Field(window, "_globalStatusDestination")?.ToString() == "maintenance",
            "hardware fault banner remains visible and routes to recovery details");
        Capture(window, "ui-state-hardware-latch", output);
        Render(window, stopped with { AuditMutation = new(true, true, "synthetic", "curve.set", "MutationInProgress", "pending audit") });
        assert(Text(window, "GlobalStatusText").StartsWith("控制暂不可用", StringComparison.Ordinal) &&
            Text(window, "GlobalStatusText").Contains("恢复事务") && Text(window, "GlobalStatusText").Contains("仍禁用"),
            "configuration audit barrier is not hidden by the disabled-protection status");
        Render(window, stopped with { SafetyOperationAudit = new(true, true, "synthetic", "lighting.set",
            "HidWithOn100Recovery", "InProgress", null, true, "pending safety audit") });
        assert(Text(window, "GlobalStatusText").StartsWith("控制暂不可用", StringComparison.Ordinal) &&
            Text(window, "GlobalStatusText").Contains("仍禁用"),
            "safety-operation audit barrier is not hidden by the disabled-protection status");
        Render(window, poisoned with { SafetyProtection = stopped.SafetyProtection! with { Persisted = false } });
        assert(Text(window, "GlobalStatusText").StartsWith("设备通信异常", StringComparison.Ordinal),
            "hardware fault outranks a simultaneous stop-intent persistence warning");

        Render(window, baseline);
        assert(Text(window, "CurveExplanationText").Contains("90°C") && Text(window, "CurveExplanationText").Contains("100%"),
            "normal control curve help describes its active thermal protection");
        assert(Button(window, "FanOffButton").ToolTip.ToString()!.Contains("关闭风扇并熄灯") &&
            Button(window, "FanOffButton").ToolTip.ToString()!.Contains("60°C") &&
            Text(window, "FanPowerHelpText").Contains("升温或监控故障会自动开启"),
            "linked lighting preserves the existing protected-mode OFF limits and recovery explanation");
        var released = baseline with { Owner = "none", Device = baseline.Device with { Connected = false },
            Fan = baseline.Fan with { Mode = ControlMode.Released } };
        Render(window, released);
        assert(Text(window, "GlobalStatusText").Contains("控制已释放") && Text(window, "CurrentControlModeText").Contains("已释放"),
            "released ownership is explicitly distinguished from ordinary manual control");
        assert(Text(window, "CurveExplanationText").Contains("自动调节已停止") &&
            Text(window, "CurveExplanationText").Contains("不会启用保护"),
            "released curve help never promises active automatic protection");
        assert(Text(window, "FanPowerHelpText").Contains("已释放") &&
            !Text(window, "FanPowerHelpText").Contains("升温或监控故障会自动开启") &&
            Text(window, "ControlProtectionText").Contains("选择纯手动"),
            "released fan help explains stopped output and the explicit path back to manual control");
        assert(!Button(window, "ReleaseControlButton").IsEnabled && !Button(window, "ApplyManualSpeedButton").IsEnabled,
            "released state does not offer an implicit speed command or duplicate release");
        Capture(window, "ui-state-released", output);

        client.Status = baseline; Render(window, baseline);
        SendKey(window, input, Key.Escape);
        foreach (var invalid in new[] { "", "9", "101", "-1", "2.5", "abc", "1e2", "59%" })
        {
            input.Text = invalid;
            var requests = client.Requests.Count;
            var handled = SendKey(window, input, Key.Enter);
            assert(handled && !Button(window, "ApplyManualSpeedButton").IsEnabled &&
                Element(window, "ManualSpeedValidationText").Visibility == Visibility.Visible,
                $"invalid manual input '{invalid}' is visibly rejected and Enter is consumed");
            assert(client.Requests.Count == requests, $"invalid manual input '{invalid}' sends no IPC request");
        }
        input.Text = "9";
        var beforeInvalidClick = client.Requests.Count;
        Event(window, "ApplyManualSpeedButton_Click");
        WaitForIdle(window);
        assert(client.Requests.Count == beforeInvalidClick, "manual apply handler independently refuses invalid input before IPC");
        Capture(window, "ui-state-invalid-speed", output);
        assert(SendKey(window, input, Key.Escape) && input.Text == "59" && slider.Value == 59 &&
            !(bool)Field(window, "_manualSpeedDirty")!, "Escape restores the saved target and clears only the local draft");

        foreach (var value in new[] { 10, 100 })
        {
            input.Text = value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var before = client.SpeedTargets.Count;
            SendKey(window, input, Key.Enter); WaitForIdle(window);
            assert(client.SpeedTargets.Count == before + 1 && client.SpeedTargets[^1] == value,
                $"valid manual boundary {value} submits one exact numeric speed through the fake API");
        }

        client.DelaySpeed = true;
        input.Text = "61";
        SendKey(window, input, Key.Enter);
        assert(client.PendingSpeed is not null && client.SpeedTargets[^1] == 61 &&
            (bool)Field(window, "_commandInProgress")!, "numeric Enter captures its submitted value while the response is delayed");
        input.Text = "72";
        client.CompleteSpeed(); WaitForIdle(window);
        assert(input.Text == "72" && slider.Value == 72 && (bool)Field(window, "_manualSpeedDirty")!,
            "late speed response and status refresh preserve edits made after submission");
        assert(Text(window, "ActivityText").Contains("61%") && Text(window, "ActivityText").Contains("未应用"),
            "completion distinguishes the submitted target from the newer unapplied draft");
        Capture(window, "ui-state-retained-speed-draft", output);
        SendKey(window, input, Key.Escape);
        assert(input.Text == "61" && !((bool)Field(window, "_manualSpeedDirty")!),
            "Escape after the reply restores the newly confirmed saved target");

        client.DelaySpeed = false;
        client.Status = baseline; Render(window, baseline);
        assert(Button(window, "ReleaseControlButton").IsEnabled, "owned healthy controller exposes the explicit release entry");
        Event(window, "ReleaseControlButton_Click"); WaitForIdle(window);
        assert(client.ReleasedRequests == 1 && client.Status.Owner == "none" &&
            client.Status.Fan.Mode == ControlMode.Released && !client.Status.Device.Connected,
            "release entry waits for a returned released mode, absent owner, and disconnected handle");
        assert(!(bool)Field(window, "_closing")! && !(bool)Field(window, "_closed")! &&
            Text(window, "ActivityText").Contains("界面继续运行"), "releasing device control keeps the settings window alive");
        assert(!window.IsVisible, "state checks never show a native window or connect to a real pipe");
        return 4;
    }

    private static ControllerStatus HealthyStatus() => new("Running", true, "synthetic-owner",
        new(true, "374A", "B101", "1602", true), new(ControlMode.Manual, 59, 59, true, 59, true),
        new(55, [50], 55, true, DateTimeOffset.UtcNow), new(false, FailSafeReason.None, null),
        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, new(false, null, null),
        Software: new("synthetic", UiCompatibility.ContractVersion, true, true, true, true, FanLightingLinkSupported: true),
        SafetyProtection: new(false, true, false, false, null, null));
    private static object? Field(MainWindow window, string name) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window);
    private static FrameworkElement Element(MainWindow window, string name) => (FrameworkElement)window.FindName(name);
    private static Button Button(MainWindow window, string name) => (Button)window.FindName(name);
    private static string Text(MainWindow window, string name) => ((TextBlock)window.FindName(name)).Text;
    private static void Render(MainWindow window, ControllerStatus status)
    {
        typeof(MainWindow).GetMethod("RenderStatus", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [status]);
        typeof(MainWindow).GetMethod("SetConnectionState", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [true, "模拟后台已连接"]);
    }
    private static void Event(MainWindow window, string method) => typeof(MainWindow).GetMethod(method,
        BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [window, new RoutedEventArgs()]);
    private static bool SendKey(MainWindow window, TextBox input, Key key)
    {
        var e = new KeyEventArgs(Keyboard.PrimaryDevice, new DetachedSource(), Environment.TickCount, key)
            { RoutedEvent = Keyboard.KeyDownEvent };
        typeof(MainWindow).GetMethod("ManualSpeedInput_KeyDown", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [input, e]);
        return e.Handled;
    }
    private static void WaitForIdle(MainWindow window)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while ((bool)Field(window, "_commandInProgress")!)
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("Synthetic UI command did not complete.");
            var frame = new DispatcherFrame();
            window.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }
    }
    private static void Capture(MainWindow window, string name, string output)
    {
        window.NavigateToPage("fan");
        var root = (FrameworkElement)window.Content;
        ((Panel)root).Background = window.Background;
        root.Measure(new Size(980, 640)); root.Arrange(new Rect(0, 0, 980, 640)); root.UpdateLayout();
        var image = new RenderTargetBitmap(980, 640, 96, 96, PixelFormats.Pbgra32); image.Render(root);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = new FileStream(Path.Combine(output, name + ".png"), FileMode.CreateNew); encoder.Save(stream);
    }
    private sealed class DetachedSource : PresentationSource
    {
        public override Visual RootVisual { get; set; } = null!;
        public override bool IsDisposed => false;
        protected override CompositionTarget GetCompositionTargetCore() => null!;
    }
    private sealed class StateFakeClient : IPipeApiClient
    {
        internal ControllerStatus Status = HealthyStatus();
        internal readonly List<PipeRequest> Requests = [];
        internal readonly List<int> SpeedTargets = [];
        internal bool DelaySpeed;
        internal TaskCompletionSource<PipeResponse>? PendingSpeed;
        internal int ReleasedRequests;
        private PipeRequest? pendingRequest;
        public Task<PipeResponse> SendAsync(PipeRequest request, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            object result;
            switch (request.Operation)
            {
                case PipeOperations.SpeedSet:
                    SpeedTargets.Add(request.Payload.GetProperty("percent").GetInt32());
                    if (DelaySpeed)
                    {
                        if (PendingSpeed is not null) throw new InvalidOperationException("Unexpected concurrent synthetic speed request.");
                        pendingRequest = request; PendingSpeed = new(); return PendingSpeed.Task;
                    }
                    AcceptSpeed((byte)SpeedTargets[^1]); result = Status; break;
                case PipeOperations.StatusGet: result = Status; break;
                case PipeOperations.ModeSet when request.Payload.GetProperty("mode").GetString() == "released":
                    ReleasedRequests++;
                    Status = Status with { Owner = "none", Fan = Status.Fan with { Mode = ControlMode.Released },
                        Device = Status.Device with { Connected = false } };
                    result = Status; break;
                case PipeOperations.CurveGet: result = FanCurve.RemoteHostSafe; break;
                case PipeOperations.LightingGet: result = new { supported = false, settings = (object?)null }; break;
                default: throw new InvalidOperationException("Unexpected operation in isolated state check: " + request.Operation);
            }
            return Task.FromResult(PipeResponse.Success(request.RequestId, result));
        }
        internal void CompleteSpeed()
        {
            var request = pendingRequest ?? throw new InvalidOperationException("No pending synthetic speed request.");
            var completion = PendingSpeed!; PendingSpeed = null; pendingRequest = null;
            AcceptSpeed(request.Payload.GetProperty("percent").GetByte());
            completion.SetResult(PipeResponse.Success(request.RequestId, Status));
        }
        private void AcceptSpeed(byte percent) => Status = Status with
        {
            Fan = Status.Fan with { RequestedPercent = percent, ConfiguredManualPercent = percent,
                ReportedControlPercent = percent, PoweredOn = true }, LastSuccessfulRead = DateTimeOffset.UtcNow,
        };
    }
}
