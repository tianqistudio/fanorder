using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using V12UltraController.Settings;

internal static class Program
{
    private static readonly List<object> checks = [];
    private static readonly List<object> errors = [];
    private static readonly Stopwatch elapsed = Stopwatch.StartNew();
    private static int failures;
    private static int tooltipWindows;
    private static int helpWindows;
    private static string scenario = "initialization";
    private static PopupDriver driver = null!;
    private static App app = null!;
    private static Window window = null!;
    private static Button first = null!;
    private static Button second = null!;
    private static TextBlock dynamicText = null!;
    private static ToolTip explicitTip = null!;
    private static int startupCallbacks;
    private static bool defaultStartupSuppressed;
    private static bool unexpectedWindowObserved;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 2 || args[0] != "--output")
        {
            Console.WriteLine("Synthetic WPF HWND and PopupControlService regression; no MainWindow, pipe or device. Usage: --output <new-report.json>");
            return args.Length == 0 ? 0 : 2;
        }
        var output = Path.GetFullPath(args[1]);
        if (File.Exists(output)) { Console.Error.WriteLine("Existing evidence will not be overwritten."); return 2; }
        var nativeWindowCreated = false;
        var cleanupComplete = false;
        // A separate thread bounds even a stalled synchronous Dispatcher callback.
        // This process contains only synthetic UI; process exit releases its HWNDs.
        using var watchdog = new System.Threading.Timer(_ =>
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                using var file = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                JsonSerializer.Serialize(file, new { timedOut = true, failures = 1, timeoutSeconds = 45,
                    nativeWindowShown = nativeWindowCreated, hardwareAccessed = false, realPipeAccessed = false,
                    cleanupComplete = false, error = "Synthetic native UI lifecycle exceeded the hard process bound." });
            }
            catch (IOException) { /* Keep any evidence already written by the main thread. */ }
            finally { Environment.Exit(124); }
        }, null, TimeSpan.FromSeconds(45), Timeout.InfiniteTimeSpan);
        try
        {
            app = new App();
            app.InitializeComponent();
            // Application queues startup in its constructor, independent of Run().
            // .NET 10 rejects null StartupUri and BAML rejects a cross-assembly App
            // subclass. Suppress the queued default action BEFORE the first pump.
            var performDefaultAction = typeof(StartupEventArgs).GetProperty("PerformDefaultAction", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new MissingMemberException("WPF StartupEventArgs.PerformDefaultAction changed.");
            app.Startup += (_, e) =>
            {
                startupCallbacks++;
                performDefaultAction.SetValue(e, false);
                defaultStartupSuppressed = !(bool)performDefaultAction.GetValue(e)!;
            };
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            app.DispatcherUnhandledException += (_, e) => { RecordError(e.Exception, "dispatcher"); e.Handled = true; };
            dynamicText = new TextBlock { Text = "Dynamic help: original", TextWrapping = TextWrapping.Wrap };
            explicitTip = new ToolTip { Content = new TextBlock { Text = "Explicit help", TextWrapping = TextWrapping.Wrap }, StaysOpen = true };
            first = MakeButton("Dynamic text", dynamicText);
            second = MakeButton("Explicit ToolTip", explicitTip);
            var panel = new StackPanel { Background = Brushes.Transparent };
            panel.Children.Add(first); panel.Children.Add(second);
            panel.Children.Add(new TextBlock { Text = "Synthetic owner", Height = 30 });
            window = new Window
            {
                Title = "FanOrder synthetic tooltip lifecycle checks", Width = 360, Height = 140,
                Left = -32000, Top = -32000, WindowStartupLocation = WindowStartupLocation.Manual,
                ShowInTaskbar = false, ShowActivated = false, WindowStyle = WindowStyle.None, Content = panel,
            };
            app.MainWindow = window;
            window.Show();
            nativeWindowCreated = new WindowInteropHelper(window).Handle != IntPtr.Zero;
            Pump(100);
            driver = new PopupDriver();
            Check(nativeWindowCreated, "synthetic owner has a real HWND");
            Check(startupCallbacks == 1 && defaultStartupSuppressed, "queued default StartupUri action is suppressed before the first Dispatcher pump");
            Check(app.Windows.Cast<Window>().All(item => ReferenceEquals(item, window)), "no production MainWindow was constructed");

            Run("hover then click", () =>
            {
                Hover(first);
                Check(driver.CurrentToolTip?.IsOpen == true, "hover opens a native WPF ToolTip");
                Click(first);
                Check(HelpPopup()?.IsOpen == true, "click opens a separate help Popup after hover dismissal");
                Pump(260);
                Check(HelpPopup()?.IsOpen == true, "late native tooltip cleanup does not close the help Popup");
            });
            Run("click then hover", () =>
            {
                Click(first);
                var originalPopup = HelpPopup();
                Hover(first);
                Check(ReferenceEquals(HelpPopup(), originalPopup) && originalPopup?.IsOpen == true,
                    "same-owner hover does not replace the active click Popup");
                Check(driver.CurrentToolTip?.IsOpen != true, "same-owner native ToolTip is suppressed while click help is open");
            });
            Run("rapid alternating buttons", () =>
            {
                for (var index = 0; index < 8; index++) Click(index % 2 == 0 ? first : second, 8);
                var latest = HelpPopup();
                Check(latest?.IsOpen == true && ReferenceEquals(HelpButton(), second), "latest button owns the live help Popup");
                Pump(300);
                Check(ReferenceEquals(latest, HelpPopup()) && latest?.IsOpen == true,
                    "asynchronous Closed from earlier Popups cannot clear a newer Popup");
            });
            Run("dynamic content and stable references", () =>
            {
                Click(first);
                dynamicText.Text = "Dynamic help: changed while open";
                Pump(60);
                Check(Descendants<TextBlock>(HelpPopup()?.Child).Any(text => text.Text == dynamicText.Text),
                    "click help reflects updates to the original dynamic TextBlock");
                Check(ReferenceEquals(first.ToolTip, dynamicText), "raw TextBlock tooltip retains its original identity");
                Click(second);
                Check(ReferenceEquals(second.ToolTip, explicitTip) && explicitTip.StaysOpen,
                    "explicit native ToolTip identity and StaysOpen remain unchanged");
            });
            Run("click close then delayed hover", () =>
            {
                Click(second); Click(second);
                Check(HelpPopup() is null || !HelpPopup()!.IsOpen, "second click closes click help");
                Pump(400);
                Hover(second);
                Check(driver.CurrentToolTip?.IsOpen == true && ReferenceEquals(driver.CurrentToolTip, explicitTip),
                    "the same explicit ToolTip reopens on delayed hover after click close");
            });
            Run("escape dismisses help", () =>
            {
                Click(first);
                window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!,
                    Environment.TickCount, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                Pump(220);
                Check(HelpPopup() is null || !HelpPopup()!.IsOpen, "owner Escape closes click help");
                Check(Mouse.Captured is null, "Escape releases mouse capture");
            });
            Run("outside capture dismissal", () =>
            {
                Click(first);
                Check(Mouse.Captured is not null, "StaysOpen=false help acquires native popup capture");
                // Exercise the WPF lost-capture path used when another surface receives
                // the outside interaction; do not move the user's pointer or SendInput.
                Mouse.Capture(null);
                Pump(220);
                Check(HelpPopup() is null || !HelpPopup()!.IsOpen, "loss of capture closes the outside-dismissable Popup");
                Check(Mouse.Captured is null, "outside dismissal leaves no mouse capture");
            });
            Run("owner geometry changes", () =>
            {
                var locationEvents = 0;
                void LocationObserved(object? sender, EventArgs e) => locationEvents++;
                window.LocationChanged += LocationObserved;
                // Moving farther negative can be clamped at the desktop's minimum
                // native coordinate. Move toward the desktop while staying outside.
                Click(first); window.Left += 240; Pump(220);
                Check(locationEvents > 0, "the synthetic owner actually raised LocationChanged");
                window.LocationChanged -= LocationObserved;
                Check(HelpPopup() is null || !HelpPopup()!.IsOpen, "owner location change dismisses click help");
                Click(second); window.Width += 3; Pump(220);
                Check(HelpPopup() is null || !HelpPopup()!.IsOpen, "owner size change dismisses click help");
            });
            Run("owner deactivation", () =>
            {
                Click(first);
                typeof(Window).GetMethod("OnDeactivated", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(window, [EventArgs.Empty]);
                Pump(220);
                Check(HelpPopup() is null || !HelpPopup()!.IsOpen, "owner deactivation dismisses click help");
                Check(Mouse.Captured is null, "deactivation releases mouse capture");
            });
            Run("button unload and reattach", () =>
            {
                Click(first); panel.Children.Remove(first); window.UpdateLayout(); Pump(220);
                Check(HelpPopup() is null || !HelpPopup()!.IsOpen, "unloading the help button dismisses its Popup");
                Check(Mouse.Captured is null, "unloading releases mouse capture");
                panel.Children.Insert(0, first); window.UpdateLayout(); Pump(80);
                Hover(first);
                Check(driver.CurrentToolTip?.IsOpen == true, "hover still works after the original button is reattached");
            });
            Run("owner closes with active help", () =>
            {
                Click(second); window.Close(); Pump(400);
                Check(HelpPopup() is null || !HelpPopup()!.IsOpen, "closing the synthetic window dismisses active help");
                Check(Mouse.Captured is null, "window closure leaves no mouse capture");
            }, resetAfter: false);
            Check(errors.Count == 0, "no synchronous or dispatcher exceptions across all lifecycle scenarios");
            Check(tooltipWindows > 0 && helpWindows > 0, "both native tooltip and independent help popup HWNDs were observed");
            Check(ReferenceEquals(first.ToolTip, dynamicText) && ReferenceEquals(second.ToolTip, explicitTip) && explicitTip.StaysOpen,
                "all tooltip references and explicit StaysOpen survive the complete sequence");
            cleanupComplete = Mouse.Captured is null && app.Windows.Count == 0;
        }
        catch (Exception exception) { RecordError(Unwrap(exception), "harness"); }
        finally
        {
            try
            {
                if (app is not null)
                {
                    CloseHelp();
                    driver?.Dismiss();
                    if (window?.IsVisible == true) window.Close();
                    Mouse.Capture(null);
                }
            }
            catch (Exception exception) { RecordError(Unwrap(exception), "cleanup"); }
        }
        var report = new
        {
            timestamp = DateTimeOffset.UtcNow, checks = checks.Count, failures, errors, results = checks,
            elapsedMs = elapsed.ElapsedMilliseconds, nativeWindowShown = nativeWindowCreated, syntheticOwnerHwnd = nativeWindowCreated,
            nativeToolTipHwndObservations = tooltipWindows, independentPopupHwndObservations = helpWindows,
            hardwareAccessed = false, realPipeAccessed = false, productionMainWindowCreated = unexpectedWindowObserved,
            defaultStartupSuppressed, isolatedStartupCallbacks = startupCallbacks,
            applicationRunCalled = false, desktopInputInjected = false, mouseCaptureRemaining = Mouse.Captured is not null,
            frameworkVersion = Environment.Version.ToString(), wpfAssembly = typeof(ToolTip).Assembly.FullName,
            settingsAssembly = typeof(App).Assembly.FullName,
            cleanupComplete, hoverDriver = "WPF .NET 10 PopupControlService.BeginShowToolTip(Mouse) with real Dispatcher timers",
            outsideDismissalDriver = "public Mouse.Capture(null), exercising WPF Popup lost-capture dismissal; no physical click injected",
            frameworkSource = "https://github.com/dotnet/wpf/blob/release/10.0/src/Microsoft.DotNet.Wpf/src/PresentationFramework/System/Windows/Controls/PopupControlService.cs",
        };
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        using (var file = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(file)) writer.Write(json);
        Console.WriteLine(JsonSerializer.Serialize(new { report.checks, report.failures, exceptions = errors.Count,
            report.nativeWindowShown, report.hardwareAccessed, report.realPipeAccessed, report.cleanupComplete, output }));
        app?.Shutdown();
        return failures == 0 && errors.Count == 0 && cleanupComplete ? 0 : 1;
    }

    private static Button MakeButton(string name, object tip)
    {
        var button = new Button { Content = "i", ToolTip = tip, Name = name.Replace(" ", ""), Margin = new Thickness(8) };
        button.SetResourceReference(FrameworkElement.StyleProperty, "InfoButtonStyle");
        ToolTipService.SetInitialShowDelay(button, 25);
        ToolTipService.SetBetweenShowDelay(button, 60);
        ToolTipService.SetShowDuration(button, 1500);
        return button;
    }

    private static void Run(string name, Action action, bool resetAfter = true)
    {
        scenario = name;
        try { Reset(); action(); }
        catch (Exception exception) { RecordError(Unwrap(exception), "scenario"); }
        finally
        {
            if (resetAfter)
            {
                try { Reset(); Check(Mouse.Captured is null, "scenario cleanup releases capture"); }
                catch (Exception exception) { RecordError(Unwrap(exception), "scenario-cleanup"); }
            }
        }
    }

    private static void Check(bool passed, string message)
    {
        checks.Add(new { scenario, passed, message });
        if (!passed) failures++;
    }

    private static void RecordError(Exception exception, string origin)
    {
        failures++;
        errors.Add(new { scenario, origin, type = exception.GetType().FullName, exception.Message, exception.StackTrace });
    }

    private static Exception Unwrap(Exception exception) => exception is TargetInvocationException { InnerException: { } inner } ? inner : exception;
    private static Popup? HelpPopup() => (Popup?)typeof(App).GetField("_helpPopup", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(app);
    private static Button? HelpButton() => (Button?)typeof(App).GetField("_helpButton", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(app);
    private static void CloseHelp() => typeof(App).GetMethod("CloseHelpPopup", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(app, null);

    private static void Reset()
    {
        CloseHelp(); driver.Dismiss(); driver.ClearMouseOwner(); Pump(220);
    }

    private static void Hover(Button button)
    {
        driver.ClearMouseOwner(); driver.Hover(button); Pump(90);
        if (driver.CurrentToolTip is { IsOpen: true } tip && PresentationSource.FromVisual(tip) is HwndSource { Handle: var handle } && handle != IntPtr.Zero)
            tooltipWindows++;
    }

    private static void Click(Button button, int delay = 40)
    {
        // PopupControlService dismisses native tooltips during input MouseDown,
        // before Button.Click. Keep its asynchronous close pending to expose races.
        driver.Dismiss();
        button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, button));
        Pump(delay);
        if (HelpPopup() is { IsOpen: true, Child: Visual child } && PresentationSource.FromVisual(child) is HwndSource { Handle: var handle } && handle != IntPtr.Zero)
            helpWindows++;
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject? parent) where T : DependencyObject
    {
        if (parent is null) yield break;
        if (parent is T match) yield return match;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            foreach (var child in Descendants<T>(VisualTreeHelper.GetChild(parent, index))) yield return child;
    }

    private static void Pump(int milliseconds)
    {
        if (elapsed.Elapsed > TimeSpan.FromSeconds(40)) throw new TimeoutException("Tooltip regression exceeded its 40-second bound.");
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        try { Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); }
        unexpectedWindowObserved |= app.Windows.Cast<Window>().Any(item => !ReferenceEquals(item, window));
    }

    private sealed class PopupDriver
    {
        private readonly Type type = typeof(ToolTip).Assembly.GetType("System.Windows.Controls.PopupControlService", throwOnError: true)!;
        private readonly object service;
        private readonly MethodInfo begin;
        private readonly object mouseTrigger;
        public PopupDriver()
        {
            service = type.GetProperty("Current", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            begin = type.GetMethod("BeginShowToolTip", BindingFlags.Instance | BindingFlags.NonPublic)!;
            mouseTrigger = Enum.Parse(begin.GetParameters()[1].ParameterType, "Mouse");
        }
        public ToolTip? CurrentToolTip => (ToolTip?)type.GetProperty("CurrentToolTip", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service);
        public void Hover(DependencyObject owner) => Invoke(begin, [owner, mouseTrigger]);
        public void ClearMouseOwner() => Invoke(begin, [null, mouseTrigger]);
        public void Dismiss() => Invoke(type.GetMethod("DismissToolTips", BindingFlags.Instance | BindingFlags.NonPublic)!, null);
        private void Invoke(MethodInfo method, object?[]? args)
        {
            try { method.Invoke(service, args); }
            catch (TargetInvocationException exception) when (exception.InnerException is { } inner)
            { ExceptionDispatchInfo.Capture(inner).Throw(); }
        }
    }
}
