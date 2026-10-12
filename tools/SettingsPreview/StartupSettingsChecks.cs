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

internal static class StartupSettingsChecks
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    { Converters = { new JsonStringEnumConverter() } };

    internal static int Run(Action<bool, string> assert, string output)
    {
        var priorContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        try { Check(assert, output); }
        finally { SynchronizationContext.SetSynchronizationContext(priorContext); }
        return 6;
    }

    private static void Check(Action<bool, string> assert, string output)
    {
        var client = new StartupFakeClient();
        var window = Create(client);
        assert(client.Reads == 0 && CheckBox(window).IsChecked is null, "startup remains unknown until the general page is opened");
        SetField(window, "_commandInProgress", true);
        window.NavigateToPage("general");
        assert(client.Reads == 1 && client.Writes.Count == 0 && CheckBox(window).IsChecked == true,
            "first general-page visit reads startup despite unrelated fan busy and recovery barriers");
        assert(!Button(window, "SaveStartupButton").IsEnabled && Text(window).Contains("系统已确认"),
            "confirmed initial startup value stays clean without requiring an explicit read");
        var startupFanSlider = (Slider)window.FindName("StartupFanPercentSlider");
        assert(Math.Round(startupFanSlider.Value) == 50 && !Button(window, "SaveStartupFanButton").IsEnabled,
            "startup fan kick defaults to the confirmed 50% value");
        startupFanSlider.Value = 62;
        assert(Button(window, "SaveStartupFanButton").IsEnabled,
            "changing startup fan kick enables its independent save");
        Wait(window.SaveStartupFanSettingsAsync());
        assert(client.StartupFanPercent == 62 && !Button(window, "SaveStartupFanButton").IsEnabled,
            "startup fan kick saves and confirms without changing startup mode");
        SetField(window, "_commandInProgress", false);
        for (var i = 0; i < 3; i++) { window.NavigateToPage("general"); Render(window, client.Status); }
        assert(client.Reads == 1, "duplicate navigation and periodic status updates cannot repeat automatic startup reads");
        Capture(window, "startup-confirmed", output);

        CheckBox(window).IsChecked = false;
        assert(Button(window, "SaveStartupButton").IsEnabled && Text(window).Contains("未保存"),
            "only a changed startup selection enables save");
        Wait(window.ReadStartupSettingsAsync());
        assert(CheckBox(window).IsChecked == false && Button(window, "SaveStartupButton").IsEnabled,
            "explicit refresh updates baseline without replacing an unsaved startup choice");
        Capture(window, "startup-dirty", output);
        client.DelaySet = true;
        var save = window.SaveStartupSettingsAsync();
        assert(!save.IsCompleted && !CheckBox(window).IsEnabled && !Button(window, "ReadStartupButton").IsEnabled &&
            client.Writes.SequenceEqual([false]), "pending save captures one explicit false value and locks only its own setting");
        client.CompleteSet(); Wait(save);
        assert(CheckBox(window).IsChecked == false && CheckBox(window).IsEnabled &&
            !Button(window, "SaveStartupButton").IsEnabled && Text(window).Contains("不自动运行"),
            "matching system readback confirms save and returns the startup setting to clean state");

        CheckBox(window).IsChecked = true;
        client.FailSet = true;
        Wait(window.SaveStartupSettingsAsync());
        assert(CheckBox(window).IsChecked == true && Text(window).Contains("未确认") &&
            Button(window, "SaveStartupButton").Content.ToString() == "重试保存",
            "failed save preserves user intent and offers explicit retry without claiming success");
        var writesBeforeRefresh = client.Writes.Count;
        client.Value = true; // Simulate an accepted write whose acknowledgement was lost.
        Wait(window.ReadStartupSettingsAsync());
        assert(client.Writes.Count == writesBeforeRefresh && !Button(window, "SaveStartupButton").IsEnabled &&
            Text(window).Contains("系统已确认"), "refresh can confirm an uncertain save without replaying StartupSet");
        CheckBox(window).IsChecked = false;
        client.MismatchSetReadback = true;
        Wait(window.SaveStartupSettingsAsync());
        assert(Text(window).Contains("未确认") && CheckBox(window).IsChecked == false,
            "mismatched system readback cannot be reported as a successful startup save");
        Wait(window.SaveStartupSettingsAsync());
        assert(!Button(window, "SaveStartupButton").IsEnabled && client.Writes[^1] == false,
            "explicit save retry uses the preserved choice and requires matching confirmation");

        var failureClient = new StartupFakeClient { FailGet = true };
        var failureWindow = Create(failureClient); failureWindow.NavigateToPage("general");
        assert(CheckBox(failureWindow).IsChecked is null && Text(failureWindow).Contains("无法读取") &&
            Button(failureWindow, "ReadStartupButton").Content.ToString() == "重试刷新",
            "failed first automatic read stays indeterminate and exposes an inline retry");
        for (var i = 0; i < 3; i++)
        {
            Render(failureWindow, failureClient.Status);
            failureWindow.NavigateToPage("fan"); failureWindow.NavigateToPage("general");
        }
        assert(failureClient.Reads == 1 && failureClient.Writes.Count == 0,
            "read failure cannot become a two-second or navigation retry storm");
        Capture(failureWindow, "startup-retry", output);
        Wait(failureWindow.ReadStartupSettingsAsync());
        assert(failureClient.Reads == 2 && CheckBox(failureWindow).IsChecked == true,
            "explicit refresh retries a failed automatic startup read");

        var delayedClient = new StartupFakeClient { DelayGet = true };
        var delayedWindow = Create(delayedClient); delayedWindow.NavigateToPage("general");
        assert(CheckBox(delayedWindow).IsChecked is null && !CheckBox(delayedWindow).IsEnabled,
            "loading state never represents an unchecked preference as confirmed disabled");
        Disconnect(delayedWindow);
        delayedClient.Value = false;
        Render(delayedWindow, delayedClient.Status);
        assert(delayedClient.Reads == 2 && CheckBox(delayedWindow).IsChecked == false,
            "reconnection on the current general page automatically reads a fresh startup baseline");
        delayedClient.CompleteGet(true); Pump();
        assert(CheckBox(delayedWindow).IsChecked == false && !Button(delayedWindow, "SaveStartupButton").IsEnabled,
            "late pre-disconnect startup response cannot replace the reconnected baseline");
        CheckBox(delayedWindow).IsChecked = true;
        Disconnect(delayedWindow);
        assert(CheckBox(delayedWindow).IsChecked == true && !Button(delayedWindow, "SaveStartupButton").IsEnabled,
            "disconnect retains a dirty choice while removing permission to save stale state");
        Render(delayedWindow, delayedClient.Status);
        assert(CheckBox(delayedWindow).IsChecked == true && Button(delayedWindow, "SaveStartupButton").IsEnabled,
            "reconnection refresh preserves the dirty choice against a fresh system baseline");

        foreach (var closing in new[] { false, true })
        {
            var staleClient = new StartupFakeClient { DelayGet = true };
            var staleWindow = Create(staleClient); staleWindow.NavigateToPage("general");
            if (closing) { SetField(staleWindow, "_closing", true); Invoke(staleWindow, "InvalidateStartupConnection"); }
            else Render(staleWindow, staleClient.Status with { Software = staleClient.Status.Software! with { UiContractVersion = 999 } });
            staleClient.CompleteGet(true); Pump();
            assert(CheckBox(staleWindow).IsChecked is null && !Button(staleWindow, "SaveStartupButton").IsEnabled,
                closing ? "closing rejects late startup confirmation" : "protocol change rejects late startup confirmation");
        }
        assert(client.Operations.All(op => op is PipeOperations.StartupGet or PipeOperations.StartupSet or PipeOperations.StartupFanSet),
            "startup interaction sends no mode, speed, lighting, safety or shutdown request");
    }

    private static MainWindow Create(StartupFakeClient client)
    {
        var window = new MainWindow(client); Render(window, client.Status); return window;
    }
    private static void Render(MainWindow window, ControllerStatus status)
    {
        typeof(MainWindow).GetMethod("RenderStatus", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [status]);
        typeof(MainWindow).GetMethod("SetConnectionState", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [true, "本地服务已连接"]);
    }
    private static void Disconnect(MainWindow window)
    {
        SetField(window, "_lastStatus", null);
        typeof(MainWindow).GetMethod("SetConnectionState", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [false, "本地服务不可用"]);
    }
    private static CheckBox CheckBox(MainWindow window) => (CheckBox)window.FindName("StartupEnabledCheckBox");
    private static Button Button(MainWindow window, string name) => (Button)window.FindName(name);
    private static string Text(MainWindow window) => ((TextBlock)window.FindName("StartupSettingText")).Text;
    private static void SetField(MainWindow window, string name, object? value) =>
        typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, value);
    private static void Invoke(MainWindow window, string name) =>
        typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
    private static void Wait(Task task)
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        while (!task.IsCompleted && DateTime.UtcNow < until) Pump();
        if (!task.IsCompleted) throw new InvalidOperationException("Synthetic startup request did not complete.");
        task.GetAwaiter().GetResult();
    }
    private static void Capture(MainWindow window, string name, string output)
    {
        var root = (FrameworkElement)window.Content; ((Panel)root).Background = window.Background;
        foreach (var (width, height) in new[] { (980, 640), (1180, 750) })
        {
            root.Measure(new Size(width, height)); root.Arrange(new Rect(0, 0, width, height)); root.UpdateLayout();
            var image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); image.Render(root);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
            using var stream = new FileStream(Path.Combine(output, $"{name}-{width}.png"), FileMode.CreateNew); encoder.Save(stream);
        }
    }

    private sealed class StartupFakeClient : IPipeApiClient
    {
        internal ControllerStatus Status = new("Synthetic", true, "synthetic", new(false, "374A", "B101", "1602", true),
            new(ControlMode.Manual, 59, null, null, 59), new(83.5, [], 83.5, true, DateTimeOffset.UtcNow),
            new(false, FailSafeReason.None, null), null, null, new(true, "synthetic unrelated barrier", DateTimeOffset.UtcNow),
            AuditMutation: new(true, true, "synthetic", "synthetic", "pending", "synthetic"),
            Software: new("startup-test", UiCompatibility.ContractVersion, true, true, true, true),
            SafetyProtection: new(true, true, true, false, null, DateTimeOffset.UtcNow));
        internal bool Value = true, FailGet, FailSet, DelayGet, DelaySet, MismatchSetReadback;
        internal byte StartupFanPercent = 50;
        internal int Reads;
        internal readonly List<bool> Writes = [];
        internal readonly List<string> Operations = [];
        private PipeRequest? pendingGet, pendingSet;
        private readonly TaskCompletionSource<PipeResponse> getCompletion = new();
        private readonly TaskCompletionSource<PipeResponse> setCompletion = new();
        public Task<PipeResponse> SendAsync(PipeRequest request, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Operations.Add(request.Operation);
            if (request.Operation == PipeOperations.StartupGet)
            {
                Reads++;
                if (FailGet) { FailGet = false; return Task.FromException<PipeResponse>(new IOException("synthetic read failure")); }
                if (DelayGet) { DelayGet = false; pendingGet = request; return getCompletion.Task; }
            }
            else if (request.Operation == PipeOperations.StartupSet)
            {
                var desired = request.Payload.GetProperty("enabled").GetBoolean(); Writes.Add(desired);
                if (FailSet) { FailSet = false; return Task.FromException<PipeResponse>(new IOException("synthetic acknowledgement loss")); }
                if (DelaySet) { DelaySet = false; pendingSet = request; return setCompletion.Task; }
                if (MismatchSetReadback) { MismatchSetReadback = false; return Task.FromResult(Response(request, !desired)); }
                Value = desired;
            }
            else if (request.Operation == PipeOperations.StartupFanSet)
            {
                StartupFanPercent = request.Payload.GetProperty("percent").GetByte();
            }
            else throw new InvalidOperationException("Unexpected startup-only request: " + request.Operation);
            return Task.FromResult(Response(request, Value));
        }
        internal void CompleteGet(bool value) => getCompletion.SetResult(Response(pendingGet!, value));
        internal void CompleteSet()
        {
            Value = pendingSet!.Payload.GetProperty("enabled").GetBoolean(); setCompletion.SetResult(Response(pendingSet, Value));
        }
        private PipeResponse Response(PipeRequest request, bool enabled) => new(PipeProtocol.ApiVersion, request.RequestId,
            true, JsonSerializer.SerializeToElement(new
            {
                enabled,
                readbackConfirmed = true,
                startupFanPercent = StartupFanPercent,
                defaultStartupFanPercent = 50,
                percent = request.Operation == PipeOperations.StartupFanSet ? StartupFanPercent : (byte?)null,
            }, JsonOptions), null, DateTimeOffset.UtcNow);
    }
}
