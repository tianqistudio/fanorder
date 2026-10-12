using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using V12UltraController.Core.Configuration;
using V12UltraController.Core.Control;
using V12UltraController.Core.Ipc;
using V12UltraController.Core.Status;
using V12UltraController.Settings;

internal static class LayoutRefinementChecks
{
    internal static int Run(Action<bool, string> assert, string output)
    {
        var client = new NoDeviceClient();
        var profiles = new MemoryControlProfileStore([
            new("layout-manual", "日常手动", ControlMode.Manual, 59, FanCurve.RemoteHostSafe, null),
            new("layout-auto", "自动温控", ControlMode.Auto, 59, FanCurve.RemoteHostSafe, null)]);
        var window = new MainWindow(client, profileStore: profiles);
        var now = DateTimeOffset.UtcNow;
        var status = new ControllerStatus("Synthetic", true, "synthetic",
            new(false, "374A", "B101", "1602", true), new(ControlMode.Manual, 59, null, null, 59, ManualPowerSupported: true),
            new(83.5, [79.0], 83.5, true, now), new(false, FailSafeReason.None, null), null, null,
            new(false, null, null), Software: new("layout-test", UiCompatibility.ContractVersion, true, true, true, true),
            SafetyProtection: new(true, true, true, false, null, now));
        Invoke(window, "RenderStatus", status);
        assert(((Expander)window.FindName("CurveEditorExpander")).IsExpanded == false,
            "fan curve editor starts collapsed while manual speed remains primary");
        assert(new BrightnessPercentConverter().Convert(128d, typeof(string), null!, CultureInfo.InvariantCulture).Equals("50%"),
            "brightness presents a percentage without changing its 0–255 storage scale");
        var root = (FrameworkElement)window.Content;
        ((Panel)root).Background = window.Background;
        var screenshots = 0;
        foreach (var page in new[] { "overview", "fan", "profiles", "shortcuts" })
        {
            window.NavigateToPage(page);
            foreach (var size in new[] { new Size(980, 640), new Size(1180, 750) })
            {
                Arrange(root, size);
                if (page == "overview")
                {
                    var quick = Find<Button>(root).Single(button => Equals(button.Content, "风扇设置"));
                    var at = quick.TransformToAncestor(root).Transform(new Point());
                    assert(at.Y >= 0 && at.Y + quick.ActualHeight < size.Height - 34,
                        $"overview quick actions remain in the first viewport at {size.Width}");
                    var column = (ColumnDefinition)window.FindName("NavigationColumn");
                    assert(column.Width.Value == (size.Width < 1080 ? 152 : 184),
                        $"navigation responds to the actual detached viewport width {size.Width}");
                }
                if (page == "profiles")
                {
                    var more = Find<Button>(root).Single(button => AutomationProperties.GetName(button) == "方案更多操作");
                    assert(more.ContextMenu!.Items.OfType<MenuItem>().Count() == 3 &&
                        !Find<Button>(root).Any(button => Equals(button.Content, "复制") || Equals(button.Content, "重命名")),
                        $"profile secondary actions use one menu at {size.Width}");
                    assert(Find<TextBlock>(root).Any(text => text.Text.Contains("手动 59%")),
                        $"profile list exposes its saved control summary at {size.Width}");
                }
                Capture(root, size, Path.Combine(output, $"layout-{page}-{size.Width}.png")); screenshots++;
            }
        }

        window.NavigateToPage("fan");
        ((Expander)window.FindName("CurveEditorExpander")).IsExpanded = true;
        Arrange(root, new Size(980, 640));
        var fanScroll = Find<ScrollViewer>((TabControl)window.FindName("MainTabs")).OrderByDescending(scroll => scroll.ScrollableHeight).First();
        fanScroll.ScrollToEnd(); root.UpdateLayout();
        var curveApply = (Button)window.FindName("ApplyCurveButton");
        var curveAt = curveApply.TransformToAncestor(root).Transform(new Point());
        assert(curveAt.Y >= 0 && curveAt.Y + curveApply.ActualHeight <= 606,
            "expanded curve editor keeps its final apply operation reachable at minimum width");
        Capture(root, new Size(980, 640), Path.Combine(output, "layout-curve-expanded-980.png")); screenshots++;

        window.NavigateToPage("shortcuts");
        Arrange(root, new Size(980, 640));
        var search = Find<TextBox>(root).Single(text => AutomationProperties.GetName(text) == "搜索快捷键");
        var bound = Find<CheckBox>(root).Single(box => AutomationProperties.GetName(box) == "仅显示已绑定快捷键");
        var rows = Field<Dictionary<string, FrameworkElement>>(window, "shortcutRowViews");
        search.Text = "关闭灯光";
        assert(rows.Where(row => row.Value.Visibility == Visibility.Visible).Select(row => row.Key).SequenceEqual(["lighting.off"]),
            "shortcut search filters by action name without rebuilding bindings");
        bound.IsChecked = true; bound.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
        assert(rows.Values.All(row => row.Visibility == Visibility.Collapsed),
            "bound-only filter combines with the search and excludes an unbound action");
        search.Text = "窗口";
        assert(rows["window.toggle"].Visibility == Visibility.Visible,
            "bound-only search includes a configured global window shortcut");
        var moreAction = Find<Button>(root).Single(button => AutomationProperties.GetName(button) == "显示 / 隐藏窗口快捷键操作");
        var clear = (MenuItem)moreAction.ContextMenu!.Items[0];
        clear.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        assert(window.Shortcuts!.Bindings["window.toggle"].Chord is null && rows["window.toggle"].Visibility == Visibility.Collapsed,
            "row menu clears a shortcut and immediately updates the bound-only results");
        bound.IsChecked = false; bound.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent)); search.Text = "关闭灯光";
        Arrange(root, new Size(980, 640));
        Capture(root, new Size(980, 640), Path.Combine(output, "layout-shortcuts-filtered-980.png")); screenshots++;
        assert(client.Requests == 0, "layout and local shortcut checks send no service or device requests");
        return screenshots;
    }

    private static void Arrange(FrameworkElement root, Size size)
    {
        root.Measure(size); root.Arrange(new Rect(size)); root.UpdateLayout();
        root.Measure(size); root.Arrange(new Rect(size)); root.UpdateLayout();
    }
    private static void Capture(FrameworkElement root, Size size, string path)
    {
        var bitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new FileStream(path, FileMode.CreateNew); encoder.Save(stream);
    }
    private static IEnumerable<T> Find<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var descendant in Find<T>(child)) yield return descendant;
        }
    }
    private static T Field<T>(MainWindow window, string name) =>
        (T)typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
    private static object? Invoke(MainWindow window, string name, params object[] arguments) =>
        typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, arguments);
    private sealed class NoDeviceClient : IPipeApiClient
    {
        internal int Requests;
        public Task<PipeResponse> SendAsync(PipeRequest request, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Requests++;
            throw new InvalidOperationException("Layout-only check attempted IPC: " + request.Operation);
        }
    }
}
