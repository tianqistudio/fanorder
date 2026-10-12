using System.Reflection;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using V12UltraController.Core.Control;
using V12UltraController.Core.Hardware;
using V12UltraController.Core.Ipc;
using V12UltraController.Core.Status;
using V12UltraController.Settings;

internal static class QuickToggleChecks
{
    internal static int Run(Action<bool, string> assert, string output)
    {
        var client = new Client();
        var window = new MainWindow(client);
        Button Button(string name) => (Button)window.FindName(name);
        string Text(string name) => Button(name).Content.ToString()!;
        void Render() => Call(window, "RenderStatus", client.Status);
        void Click(string name) => Button(name).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        Render();
        assert(Text("HomeFanToggleButton") == "风扇已开 · 关闭…", "quick fan button reflects confirmed ON");
        var confirmations = 0;
        window.ConfirmFanOff = _ => { confirmations++; return false; };
        Click("HomeFanToggleButton");
        assert(confirmations == 1 && client.Power.Count == 0, "cancelled OFF confirmation sends no command");
        window.ConfirmFanOff = _ => true;
        Click("HomeFanToggleButton");
        assert(client.Power.SequenceEqual([false]) && Text("HomeFanToggleButton") == "风扇已关 · 开启", "confirmed OFF changes one button to ON action");
        Click("HomeFanToggleButton");
        assert(client.Power.SequenceEqual([false, true]) && Text("HomeFanToggleButton") == "风扇已开 · 关闭…", "same button restores ON after verified response");
        client.Status = client.Status with { LastSuccessfulRead = DateTimeOffset.UtcNow.AddMinutes(-1) };
        Render(); var count = client.Power.Count;
        Click("HomeFanToggleButton");
        assert(Text("HomeFanToggleButton") == "风扇未知 · 刷新" && client.Power.Count == count, "stale fan state refreshes without guessing power");
        client.Status = client.Status with { LastSuccessfulRead = DateTimeOffset.UtcNow.AddMinutes(1) };
        Render(); assert(Text("HomeFanToggleButton").Contains("未知"), "future fan timestamp remains unknown");
        client.Status = client.Status with { LastSuccessfulRead = DateTimeOffset.UtcNow };
        Render();
        client.RejectPower = true; Click("HomeFanToggleButton");
        assert(Text("HomeFanToggleButton") == "风扇已开 · 关闭…", "rejected power request never optimistically flips displayed state");
        client.RejectPower = false;
        window.RefreshQuickLightingAsync().GetAwaiter().GetResult();
        assert(Text("HomeLightToggleButton") == "灯光已开 · 关闭", "lighting toggle reads backend cache independently of editor");
        Capture(window, output, "quick-controls-on");
        var draft = (Slider)window.FindName("LightingBrightnessSlider"); draft.Value = 111;
        Click("HomeLightToggleButton");
        assert(client.Lighting is { Enabled: false, Brightness: 153 } && draft.Value == 111 &&
            Text("HomeLightToggleButton") == "灯光已关 · 开启", "lighting toggle preserves backend preset and unsaved editor draft");
        Capture(window, output, "quick-controls-lights-off");
        Click("HomeLightToggleButton");
        assert(client.Lighting.Enabled && Text("HomeLightToggleButton") == "灯光已开 · 关闭", "lighting toggles back after confirmation");
        client.LightRead = DateTimeOffset.UtcNow.AddMinutes(-1);
        window.RefreshQuickLightingAsync().GetAwaiter().GetResult(); var writes = client.LightWrites;
        Click("HomeLightToggleButton");
        assert(Text("HomeLightToggleButton").Contains("未知") && client.LightWrites == writes, "stale lighting permits cache refresh only");
        Capture(window, output, "quick-controls-stale");
        client.LightRead = DateTimeOffset.UtcNow;
        window.RefreshQuickLightingAsync().GetAwaiter().GetResult();
        client.Status = client.Status with { Device = client.Status.Device with { Connected = false } }; Render();
        assert(Text("HomeFanToggleButton").Contains("未知") && Text("HomeLightToggleButton").Contains("未知"), "disconnection invalidates both visible states");
        client.Status = client.Status with { Device = client.Status.Device with { Connected = true } }; Render();
        Set(window, "_commandInProgress", true); Call(window, "UpdateControlButtonsForBusyState");
        assert(!Button("HomeFanToggleButton").IsEnabled && !Button("HomeLightToggleButton").IsEnabled, "busy state disables both toggles");
        Set(window, "_commandInProgress", false); Call(window, "UpdateControlButtonsForBusyState");
        ((ComboBox)window.FindName("LanguageCombo")).SelectedIndex = 1;
        assert(Text("HomeFanToggleButton").Contains("Fan on") && Text("HomeLightToggleButton").Contains("Lights on"), "single buttons retain English localization");
        Capture(window, output, "quick-controls-english");
        ((ComboBox)window.FindName("LanguageCombo")).SelectedIndex = 0;
        client.Lighting = client.Lighting with { Mode = 99 };
        window.RefreshQuickLightingAsync().GetAwaiter().GetResult();
        assert(Text("HomeLightToggleButton").Contains("未知"), "invalid lighting cache is unknown without an unhandled exception");
        client.Lighting = client.Lighting with { Mode = 0 };
        client.DelayRead = true;
        var pending = window.RefreshQuickLightingAsync();
        Call(window, "InvalidateQuickLighting");
        client.CompleteRead(); pending.GetAwaiter().GetResult();
        assert(Text("HomeLightToggleButton").Contains("未知"), "late lighting cache response cannot restore state invalidated by a newer command");
        assert(!client.Operations.Contains(PipeOperations.ModeSet), "quick controls never change control mode");
        return 4;
    }

    private static void Capture(MainWindow window, string output, string name)
    {
        var root = (FrameworkElement)window.Content;
        ((Panel)root).Background = window.Background;
        root.Measure(new Size(980, 640)); root.Arrange(new Rect(0, 0, 980, 640)); root.UpdateLayout();
        var bitmap = new RenderTargetBitmap(980, 640, 96, 96, PixelFormats.Pbgra32); bitmap.Render(root);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(output, name + ".png")); png.Save(stream);
    }

    private static void Call(MainWindow window, string method, params object[] args) =>
        typeof(MainWindow).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, args);
    private static void Set(MainWindow window, string field, object value) =>
        typeof(MainWindow).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, value);

    private sealed class Client : IPipeApiClient
    {
        internal ControllerStatus Status;
        internal LightingSettings Lighting = new(true, 0, 1, 153, 2);
        internal DateTimeOffset LightRead = DateTimeOffset.UtcNow;
        internal List<bool> Power = [];
        internal List<string> Operations = [];
        internal int LightWrites;
        internal bool RejectPower;
        internal bool DelayRead;
        private TaskCompletionSource<PipeResponse>? pendingRead;
        private string? pendingId;
        internal void CompleteRead() => pendingRead!.SetResult(PipeResponse.Success(pendingId!,
            new { supported = true, settings = Lighting, lastSuccessfulRead = LightRead }));
        internal Client()
        {
            var now = DateTimeOffset.UtcNow;
            LightRead = now;
            Status = new("Synthetic", true, "synthetic", new(true, "374A", "B101", "1602", true),
                new(ControlMode.Manual, 59, 59, true, 59, true), new(54, [52.0], 54, true, now),
                new(false, FailSafeReason.None, null), now, now, new(false, null, null),
                Software: new("quick-toggle-test", UiCompatibility.ContractVersion, true, true, true, true),
                SafetyProtection: new(true, true, true, false, null, now));
        }
        public Task<PipeResponse> SendAsync(PipeRequest request, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Operations.Add(request.Operation);
            object result;
            switch (request.Operation)
            {
                case PipeOperations.StatusGet: result = Status; break;
                case PipeOperations.LightingGet:
                    if (DelayRead) { DelayRead = false; pendingId = request.RequestId; pendingRead = new(); return pendingRead.Task; }
                    result = new { supported = true, settings = Lighting, lastSuccessfulRead = LightRead }; break;
                case PipeOperations.LightingSet:
                    LightWrites++;
                    Lighting = Lighting with { Enabled = request.Payload.GetProperty("enabled").GetBoolean() };
                    LightRead = DateTimeOffset.UtcNow;
                    result = new { readbackConfirmed = true }; break;
                case PipeOperations.PowerSet:
                    var poweredOn = request.Payload.GetProperty("poweredOn").GetBoolean(); Power.Add(poweredOn);
                    if (RejectPower) return Task.FromResult(PipeResponse.Failure(request.RequestId, "test-rejected", "Synthetic rejection"));
                    Status = Status with { Fan = Status.Fan with { PoweredOn = poweredOn }, LastSuccessfulRead = DateTimeOffset.UtcNow };
                    result = new { readbackConfirmed = true, poweredOn }; break;
                default: throw new InvalidOperationException("Unexpected fake request: " + request.Operation);
            }
            return Task.FromResult(PipeResponse.Success(request.RequestId, result));
        }
    }
}
