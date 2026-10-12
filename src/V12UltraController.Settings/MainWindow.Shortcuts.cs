using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;

namespace V12UltraController.Settings;

public partial class MainWindow
{
    private ShortcutManager? shortcutManager;
    private string? recordingShortcutId;
    private bool shortcutDispatchBusy;
    private bool shortcutsInitialized;
    private readonly Dictionary<string, (Button Record, CheckBox Global, TextBlock Error)> shortcutRows = new();
    private readonly Dictionary<string, FrameworkElement> shortcutRowViews = new();
    private readonly List<(FrameworkElement Card, string[] Ids)> shortcutGroupViews = [];
    private string shortcutSearch = "";
    private bool shortcutsBoundOnly;
    private TextBlock? shortcutFilterStatus;
    internal ShortcutManager? Shortcuts => shortcutManager;

    internal void InitializeShortcuts(bool enableNative, IShortcutStore? store = null, IHotkeyRegistrar? registrar = null)
    {
        if (shortcutsInitialized) return;
        shortcutsInitialized = true;
        void Initialize()
        {
            if (_closed) return;
            try
            {
                shortcutManager = new ShortcutManager(store ?? (enableNative
                    ? new LocalShortcutStore(ShortcutStorePaths.ForApplication("TianqiStudio", "V12UltraController"))
                    : new MemoryShortcutStore()),
                    registrar ?? (enableNative ? new WindowsHotkeyRegistrar() : new FakeHotkeyRegistrar()));
                shortcutManager.ActionRequested += DispatchShortcut;
                shortcutManager.ChordCaptured += CaptureShortcutChord;
                RebuildShortcuts();
            }
            catch (Win32Exception exception)
            {
                ShortcutsHost.Content = new TextBlock { Text = "快捷键初始化失败：" + exception.Message, TextWrapping = TextWrapping.Wrap };
            }
        }
        if (enableNative && new WindowInteropHelper(this).Handle == IntPtr.Zero)
            SourceInitialized += (_, _) => Initialize();
        else Initialize();
        ProfilesChanged += RebuildShortcuts;
        PreviewKeyDown += Shortcuts_PreviewKeyDown;
        Deactivated += (_, _) => EndShortcutRecording();
        Closed += (_, _) =>
        {
            EndShortcutRecording();
            shortcutManager?.Dispose();
        };
    }

    private IEnumerable<ShortcutAction> BuildShortcutActions()
    {
        var globalEmpty = new ShortcutBinding(null, true);
        var localEmpty = new ShortcutBinding(null, false);
        yield return new("window.toggle", "显示 / 隐藏窗口", "窗口", ShortcutScopePolicy.RequiredGlobal,
            new(new(0x46, ShortcutModifiers.Control | ShortcutModifiers.Alt), true), "隐藏到托盘后可唤回；仅支持全局快捷键。");
        foreach (var profile in SavedProfiles)
            yield return new("profile:" + profile.Id, profile.Name, "已保存方案", ShortcutScopePolicy.LocalOrGlobal,
                globalEmpty, "应用已保存方案；未保存修改不会应用。");
        yield return new("fan.increase", "风扇目标 +1%", "设备控制", ShortcutScopePolicy.LocalOrGlobal, globalEmpty,
            "按托盘滚轮调速，范围 10–100%；需后台和设备可用。");
        yield return new("fan.decrease", "风扇目标 −1%", "设备控制", ShortcutScopePolicy.LocalOrGlobal, globalEmpty,
            "按托盘滚轮调速，范围 10–100%；需后台和设备可用。");
        yield return new("fan.on", "开启风扇", "设备控制", ShortcutScopePolicy.LocalOrGlobal, localEmpty, "同界面开扇，结果以设备确认。");
        yield return new("fan.off", "关闭风扇", "设备控制", ShortcutScopePolicy.LocalOrGlobal, localEmpty,
            "始终先唤回窗口并显示关闭确认，确认前不发送关闭请求。");
        yield return new("lighting.on", "开启灯光", "设备控制", ShortcutScopePolicy.LocalOrGlobal, localEmpty,
            "沿用当前灯光预设，不提交 RGB 草稿，不改风扇电源。");
        yield return new("lighting.off", "关闭灯光", "设备控制", ShortcutScopePolicy.LocalOrGlobal, localEmpty,
            "沿用当前灯光预设，不提交 RGB 草稿，不改风扇电源。");
        yield return new("window.refresh", "刷新状态", "窗口", ShortcutScopePolicy.LocalOnly,
            new(new(0x74, ShortcutModifiers.None), false), "仅在本窗口中刷新后台状态。");
        yield return new("window.control", "打开控制", "窗口", ShortcutScopePolicy.LocalOnly,
            new(new(0x31, ShortcutModifiers.Control), false), "切换到控制一级菜单。");
        yield return new("window.monitor", "打开监测", "窗口", ShortcutScopePolicy.LocalOnly,
            new(new(0x32, ShortcutModifiers.Control), false), "切换到监测一级菜单。");
        yield return new("window.settings", "打开设置", "窗口", ShortcutScopePolicy.LocalOnly,
            new(new(0x33, ShortcutModifiers.Control), false), "切换到设置一级菜单。");
        yield return new("profile.save", "保存当前方案", "窗口", ShortcutScopePolicy.LocalOnly,
            new(new(0x53, ShortcutModifiers.Control), false), "保存方案草稿，不向设备发送指令。");
    }

    private void RebuildShortcuts()
    {
        if (shortcutManager is null) return;
        EndShortcutRecording();
        shortcutManager.ReplaceActions(BuildShortcutActions());
        shortcutRows.Clear();
        shortcutRowViews.Clear();
        shortcutGroupViews.Clear();
        var panel = new StackPanel();
        var filters = new Grid { Margin = new(0, 0, 0, 14) };
        filters.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        filters.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var search = new TextBox { Text = shortcutSearch, MinHeight = 36, Padding = new(10, 7, 10, 7),
            VerticalContentAlignment = VerticalAlignment.Center, ToolTip = "按动作名称、分组或组合键搜索。", Margin = new(0, 0, 16, 0) };
        AutomationProperties.SetName(search, "搜索快捷键");
        search.TextChanged += (_, _) => { shortcutSearch = search.Text; ApplyShortcutFilter(); };
        filters.Children.Add(search);
        var bound = new CheckBox { Content = "仅已绑定", IsChecked = shortcutsBoundOnly, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(bound, "仅显示已绑定快捷键");
        bound.Click += (_, _) => { shortcutsBoundOnly = bound.IsChecked == true; ApplyShortcutFilter(); };
        Grid.SetColumn(bound, 1); filters.Children.Add(bound);
        panel.Children.Add(new TextBlock { Text = "搜索快捷键", Margin = new(0, 0, 0, 6) });
        panel.Children.Add(filters);
        shortcutFilterStatus = new TextBlock { Margin = new(0, 0, 0, 10) };
        shortcutFilterStatus.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        AutomationProperties.SetLiveSetting(shortcutFilterStatus, AutomationLiveSetting.Polite);
        panel.Children.Add(shortcutFilterStatus);
        if (shortcutManager.StorageError is { } storageError)
            panel.Children.Add(new TextBlock { Text = storageError, TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 12) });
        foreach (var group in shortcutManager.Actions.GroupBy(action => action.Group))
        {
            var groupPanel = new StackPanel();
            var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new(0, 0, 0, 8) };
            header.Children.Add(new TextBlock { Text = group.Key, FontSize = 17, FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center });
            var help = new Button { Content = "i", VerticalAlignment = VerticalAlignment.Center };
            help.SetResourceReference(StyleProperty, "InfoButtonStyle");
            var tip = new ToolTip { Content = "点击录入；Esc 取消，Delete 清除。全局快捷键在窗口隐藏时也可用。录入时暂停其他快捷键。",
                PlacementTarget = help };
            help.ToolTip = tip;
            AutomationProperties.SetName(help, group.Key + "快捷键说明");
            header.Children.Add(help);
            groupPanel.Children.Add(header);
            foreach (var action in group)
                groupPanel.Children.Add(CreateShortcutRow(action));
            var card = new Border { Child = groupPanel, Margin = new(0, 0, 0, 12) };
            card.SetResourceReference(StyleProperty, "CardStyle");
            panel.Children.Add(card);
            shortcutGroupViews.Add((card, group.Select(action => action.Id).ToArray()));
        }
        ShortcutsHost.Content = panel;
        ApplyShortcutFilter();
    }

    private FrameworkElement CreateShortcutRow(ShortcutAction action)
    {
        var grid = new Grid { Margin = new(0, 5, 0, 5) };
        grid.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new() { Width = new(188) });
        grid.ColumnDefinitions.Add(new() { Width = new(86) });
        grid.ColumnDefinitions.Add(new() { Width = new(40) });
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var name = new TextBlock { Text = action.Name, VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = ShortcutActionHelp(action), Margin = new(0, 0, 10, 0) };
        name.ToolTipOpening += (_, _) => name.ToolTip = ShortcutActionHelp(action);
        grid.Children.Add(name);
        var record = new Button { Margin = new(0, 0, 10, 0), MinHeight = 36, ToolTip = ShortcutActionHelp(action) + "\n点击录入；Esc 取消，Delete 清除。" };
        record.ToolTipOpening += (_, _) => record.ToolTip = ShortcutActionHelp(action) + "\n点击录入；Esc 取消，Delete 清除。";
        record.SetResourceReference(StyleProperty, "KeycapButtonStyle");
        AutomationProperties.SetName(record, action.Name + "快捷键");
        record.Click += (_, _) => BeginShortcutRecording(action.Id);
        record.LostKeyboardFocus += (_, _) => { if (recordingShortcutId == action.Id) EndShortcutRecording(); };
        Grid.SetColumn(record, 1); grid.Children.Add(record);
        var global = new CheckBox { Content = "全局", VerticalAlignment = VerticalAlignment.Center,
            IsEnabled = action.Scope == ShortcutScopePolicy.LocalOrGlobal,
            ToolTip = action.Scope == ShortcutScopePolicy.RequiredGlobal ? "此动作始终使用全局快捷键。" :
                action.Scope == ShortcutScopePolicy.LocalOnly ? "此动作仅在窗口内生效。" : "勾选后，窗口隐藏时仍可使用；需包含 Ctrl 或 Alt。" };
        ToolTipService.SetShowOnDisabled(global, true);
        AutomationProperties.SetName(global, action.Name + "全局开关");
        global.Click += (_, _) =>
        {
            EndShortcutRecording();
            shortcutManager!.TryUpdate(action.Id, shortcutManager.Bindings[action.Id] with { Global = global.IsChecked == true }, out _);
            RefreshShortcutRow(action.Id);
        };
        Grid.SetColumn(global, 2); grid.Children.Add(global);
        var more = new Button { Content = "⋯", Padding = new(8, 7, 8, 7), ToolTip = "快捷键操作" };
        more.SetResourceReference(StyleProperty, "CompactActionButtonStyle");
        AutomationProperties.SetName(more, action.Name + "快捷键操作");
        var clear = new MenuItem { Header = "清除快捷键", ToolTip = "清除此动作的快捷键。" };
        AutomationProperties.SetName(clear, "清除" + action.Name + "快捷键");
        clear.Click += (_, _) =>
        {
            EndShortcutRecording();
            shortcutManager!.TryUpdate(action.Id, shortcutManager.Bindings[action.Id] with { Chord = null }, out _);
            RefreshShortcutRow(action.Id);
        };
        more.ContextMenu = new ContextMenu();
        more.ContextMenu.Items.Add(clear);
        Localization.Attach(more.ContextMenu);
        more.Click += (_, _) =>
        {
            clear.IsEnabled = shortcutManager!.Bindings[action.Id].Chord is not null;
            OpenActionMenu(more);
        };
        Grid.SetColumn(more, 3); grid.Children.Add(more);
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new(0, 5, 0, 0), Visibility = Visibility.Collapsed };
        error.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
        AutomationProperties.SetLiveSetting(error, AutomationLiveSetting.Polite);
        Grid.SetRow(error, 1); Grid.SetColumnSpan(error, 4); grid.Children.Add(error);
        shortcutRows[action.Id] = (record, global, error);
        shortcutRowViews[action.Id] = grid;
        RefreshShortcutRow(action.Id);
        return grid;
    }

    private string ShortcutActionHelp(ShortcutAction action) =>
        action.Help + (FanLightingLinkSupported && action.Id is "fan.on" or "fan.off" ? "\n" + FanLightingLinkHelp : "");

    private void RefreshShortcutRow(string id)
    {
        if (shortcutManager is null || !shortcutRows.TryGetValue(id, out var row)) return;
        var binding = shortcutManager.Bindings[id];
        row.Record.Content = FormatShortcut(binding.Chord);
        row.Global.IsChecked = binding.Global;
        row.Error.Text = shortcutManager.Errors.GetValueOrDefault(id) ?? "";
        row.Error.Visibility = row.Error.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        ApplyShortcutFilter();
    }

    private void ApplyShortcutFilter()
    {
        if (shortcutManager is null) return;
        var query = shortcutSearch.Trim();
        var visible = 0;
        foreach (var action in shortcutManager.Actions)
        {
            if (!shortcutRowViews.TryGetValue(action.Id, out var view)) continue;
            var chord = shortcutManager.Bindings[action.Id].Chord;
            var match = (!shortcutsBoundOnly || chord is not null) && (query.Length == 0 ||
                (action.Name + " " + action.Group + " " + FormatShortcut(chord)).Contains(query, StringComparison.OrdinalIgnoreCase));
            // Keep an active capture visible until it completes or is cancelled.
            match |= recordingShortcutId == action.Id;
            view.Visibility = match ? Visibility.Visible : Visibility.Collapsed;
            if (match) visible++;
        }
        foreach (var group in shortcutGroupViews)
            group.Card.Visibility = group.Ids.Any(id => shortcutRowViews.TryGetValue(id, out var view) && view.Visibility == Visibility.Visible)
                ? Visibility.Visible : Visibility.Collapsed;
        if (shortcutFilterStatus is not null)
            shortcutFilterStatus.Text = visible == 0 ? "没有符合条件的快捷键" : $"{visible} 个动作 · 点击按键框录入";
    }

    private void BeginShortcutRecording(string id)
    {
        EndShortcutRecording();
        recordingShortcutId = id;
        shortcutManager!.SuspendDispatch = true;
        shortcutRows[id].Record.Content = "请按组合键…";
        shortcutRows[id].Record.Focus();
    }

    private void EndShortcutRecording()
    {
        var id = recordingShortcutId;
        recordingShortcutId = null;
        if (shortcutManager is not null) shortcutManager.SuspendDispatch = false;
        if (id is not null) RefreshShortcutRow(id);
    }

    private void CaptureShortcutChord(ShortcutChord? chord)
    {
        if (recordingShortcutId is not { } id || shortcutManager is null) return;
        shortcutManager.TryUpdate(id, shortcutManager.Bindings[id] with { Chord = chord }, out _);
        EndShortcutRecording();
    }

    private void Shortcuts_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (shortcutManager is null) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.ImeProcessed) key = e.ImeProcessedKey;
        if (recordingShortcutId is not null)
        {
            e.Handled = true;
            if (e.IsRepeat) return;
            if (key == Key.Escape) { EndShortcutRecording(); return; }
            if (key == Key.Delete) { CaptureShortcutChord(null); return; }
            if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return;
            CaptureShortcutChord(CurrentChord(key));
            return;
        }
        var editing = Keyboard.FocusedElement is TextBoxBase or PasswordBox ||
            Keyboard.FocusedElement is ComboBox { IsEditable: true };
        if (!e.IsRepeat && shortcutManager.TryDispatchLocal(CurrentChord(key), textInput: editing)) e.Handled = true;
    }

    private static ShortcutChord CurrentChord(Key key) => new(KeyInterop.VirtualKeyFromKey(key),
        ((Keyboard.Modifiers & ModifierKeys.Control) != 0 ? ShortcutModifiers.Control : 0) |
        ((Keyboard.Modifiers & ModifierKeys.Alt) != 0 ? ShortcutModifiers.Alt : 0) |
        ((Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? ShortcutModifiers.Shift : 0) |
        ((Keyboard.Modifiers & ModifierKeys.Windows) != 0 ? ShortcutModifiers.Windows : 0));

    internal static string FormatShortcut(ShortcutChord? chord)
    {
        if (chord is null) return "未绑定";
        var parts = new List<string>();
        if (chord.Modifiers.HasFlag(ShortcutModifiers.Control)) parts.Add("Ctrl");
        if (chord.Modifiers.HasFlag(ShortcutModifiers.Alt)) parts.Add("Alt");
        if (chord.Modifiers.HasFlag(ShortcutModifiers.Shift)) parts.Add("Shift");
        var key = KeyInterop.KeyFromVirtualKey(chord.VirtualKey);
        parts.Add(key is >= Key.D0 and <= Key.D9 ? ((int)(key - Key.D0)).ToString(System.Globalization.CultureInfo.InvariantCulture) : key.ToString());
        return string.Join(" + ", parts);
    }

    private async void DispatchShortcut(string id)
    {
        if (_closing || _closed || recordingShortcutId is not null) return;
        if (id == "window.toggle")
        {
            if (IsVisible && WindowState != WindowState.Minimized) Hide(); else ShowFromTray();
            return;
        }
        if (id == "profile.save" && CurrentPageId != "profiles") return;
        if (_commandInProgress || shortcutDispatchBusy)
        {
            NotifyShortcutUnavailable("正在处理请求，请等待确认。");
            return;
        }
        shortcutDispatchBusy = true;
        try
        {
            switch (id)
            {
                case "window.refresh": await RefreshStatusAsync(showErrors: true); break;
                case "window.control": NavigatePrimary("control"); break;
                case "window.monitor": NavigatePrimary("monitor"); break;
                case "window.settings": NavigatePrimary("settings"); break;
                case "profile.save": await SaveProfileDraftAsync(); break;
                case "fan.increase":
                case "fan.decrease":
                    if (!TraySpeedAvailable) { NotifyShortcutUnavailable("当前无法调速，请查看设备与后台状态。"); break; }
                    QueueTrayWheel(id == "fan.increase" ? 120 : -120);
                    await FlushTraySpeedAsync();
                    break;
                case "fan.on":
                    if (FanOnButton.IsEnabled) await SetFanPowerAsync(true);
                    else NotifyShortcutUnavailable("当前无法开启风扇，请查看设备与后台状态。");
                    break;
                case "fan.off":
                    ShowFromTray();
                    if (FanOffButton.IsEnabled) FanOffButton_Click(this, new RoutedEventArgs());
                    else NotifyShortcutUnavailable("当前无法关闭风扇，请查看温控与设备状态。");
                    break;
                case "lighting.on":
                case "lighting.off":
                    if (LightingOutputAvailable && !_homeLightingCacheUnavailable) await SetHomeLightingAsync(id == "lighting.on");
                    else NotifyShortcutUnavailable("当前无法控制灯光，请查看风扇与后台状态。");
                    break;
                default:
                    if (id.StartsWith("profile:", StringComparison.Ordinal))
                    {
                        await ApplySavedProfileAsync(id[8..]);
                        tray?.SetText("风序 · " + ActivityText.Text);
                    }
                    break;
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or OperationCanceledException)
        { NotifyShortcutUnavailable(exception.Message); }
        finally { shortcutDispatchBusy = false; }
    }

    private void NotifyShortcutUnavailable(string message)
    {
        ActivityText.Text = message;
        tray?.SetText("风序 · " + message);
    }
}
