using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using V12UltraController.Core.Status;

namespace V12UltraController.Settings;

public partial class MainWindow
{
    private sealed record PageDefinition(string Id, string Group, string Title);
    private static readonly PageDefinition[] NavigationPages =
    [
        new("overview", "control", "概览"), new("fan", "control", "风扇"),
        new("lighting", "control", "灯光"), new("profiles", "control", "方案"),
        new("trends", "monitor", "实时趋势"), new("analysis", "monitor", "历史分析"),
        new("general", "settings", "常规"), new("shortcuts", "settings", "快捷键"),
        new("maintenance", "settings", "设备与维护"), new("about", "settings", "关于"),
    ];
    private readonly Dictionary<string, string> lastPages = new()
    { ["control"] = "overview", ["monitor"] = "trends", ["settings"] = "general" };
    private string navigationGroup = "control";
    private bool navigationReady;
    private string? curveBaseline;
    private bool curveWasRead;
    internal Func<string, bool>? ConfirmDraftReplacement { get; set; }
    internal string CurrentPageId => (MainTabs.SelectedItem as TabItem)?.Tag as string ?? "overview";

    private void InitializeNavigation()
    {
        foreach (var (key, title, glyph) in new[]
        { ("control", "控制", "\uE9D9"), ("monitor", "监测", "\uE9D2"), ("settings", "设置", "\uE713") })
        {
            var localizedTitle = Localization.Translate(title);
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 18, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0,0,12,0) });
            content.Children.Add(new TextBlock { Text = localizedTitle, FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
            var button = new Button { Content = content, Tag = key, Style = (Style)FindResource("NavigationButtonStyle") };
            System.Windows.Automation.AutomationProperties.SetName(button, localizedTitle);
            button.Click += (_, _) => NavigatePrimary(key);
            PrimaryNavigationPanel.Children.Add(button);
        }
        navigationReady = true;
        OverviewMetricsGrid.Columns = 3;
        ProfilesChanged += UpdateOverviewProfiles;
        OverviewProfileCombo.SelectionChanged += (_, _) => RenderNavigationStatus();
        UpdateOverviewProfiles();
        CurvePoints.CollectionChanged += (_, _) => RenderCurveDraft();
        curveBaseline = CurveFingerprint();
        NavigateToPage("overview");
        RenderCurveDraft();
    }

    internal void NavigatePrimary(string key)
    {
        if (lastPages.TryGetValue(key, out var page)) NavigateToPage(page);
    }

    internal void NavigateToPage(string id)
    {
        var page = NavigationPages.SingleOrDefault(p => p.Id == id);
        if (page is null) return;
        var item = MainTabs.Items.Cast<TabItem>().Single(t => (string)t.Tag == id);
        MainTabs.SelectedItem = item;
        UpdateNavigation(page);
    }

    private void MainTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!navigationReady || e.Source != MainTabs) return;
        var page = NavigationPages.SingleOrDefault(p => p.Id == CurrentPageId);
        if (page is not null) UpdateNavigation(page);
    }

    private void UpdateNavigation(PageDefinition page)
    {
        if (!navigationReady) return;
        var rebuild = navigationGroup != page.Group || SecondaryNavigationPanel.Children.Count == 0;
        navigationGroup = page.Group;
        lastPages[page.Group] = page.Id;
        PageTitleText.Text = Localization.Translate(page.Group switch { "control" => "控制", "monitor" => "监测", _ => "设置" });
        if (rebuild)
        {
            SecondaryNavigationPanel.Children.Clear();
            foreach (var peer in NavigationPages.Where(p => p.Group == page.Group))
            {
                var button = new Button { Content = Localization.Translate(peer.Title), Tag = peer.Id, MinWidth = 72,
                    Padding = new Thickness(14,8,14,8), Margin = new Thickness(0,0,8,0),
                    Style = (Style)FindResource("SecondaryButtonStyle") };
                button.Click += (_, _) => NavigateToPage(peer.Id);
                SecondaryNavigationPanel.Children.Add(button);
            }
        }
        foreach (var button in PrimaryNavigationPanel.Children.OfType<Button>())
            MarkNavigation(button, (string)button.Tag == page.Group, primary: true);
        foreach (var button in SecondaryNavigationPanel.Children.OfType<Button>())
            MarkNavigation(button, (string)button.Tag == page.Id, primary: false);
        UpdateStartupSettingsAvailability();
        UpdatePageReadAvailability();
        RenderPageFeedback();
    }

    private void MarkNavigation(Button button, bool selected, bool primary)
    {
        button.SetResourceReference(BackgroundProperty, selected ? "SurfaceRaisedBrush" : "WindowBrush");
        button.SetResourceReference(ForegroundProperty, selected ? "PrimaryBrush" : "TextPrimaryBrush");
        button.SetResourceReference(BorderBrushProperty, selected ? "PrimaryBrush" : "BorderBrush");
        button.BorderThickness = selected ? primary ? new Thickness(3,0,0,0) : new Thickness(0,0,0,2) : new Thickness(0);
        System.Windows.Automation.AutomationProperties.SetItemStatus(button, selected ? Localization.Translate("已选中") : "");
    }

    private void UpdateOverviewProfiles()
    {
        var selected = OverviewProfileCombo.SelectedValue as string;
        OverviewProfileCombo.ItemsSource = SavedProfiles;
        OverviewProfileCombo.SelectedValue = selected;
        if (OverviewProfileCombo.SelectedIndex < 0 && SavedProfiles.Count > 0) OverviewProfileCombo.SelectedIndex = 0;
        RenderNavigationStatus();
    }

    private async void OverviewApplyProfile_Click(object sender, RoutedEventArgs e)
    {
        if (OverviewProfileCombo.SelectedValue is string id) await ApplySavedProfileAsync(id);
    }
    private void OpenProfiles_Click(object sender, RoutedEventArgs e) => NavigateToPage("profiles");
    private void OpenFan_Click(object sender, RoutedEventArgs e) => NavigateToPage("fan");
    private void OpenMaintenance_Click(object sender, RoutedEventArgs e)
    {
        NavigateToPage("maintenance");
        if (_lastStatus?.Commissioned != true || _lastStatus?.ForcedUninstallRecovery?.Required == true)
            CommissionExpander.IsExpanded = true;
    }

    private void RenderNavigationStatus()
    {
        if (!navigationReady) return;
        StartBackendButton.Visibility = _lastStatus is null ? Visibility.Visible : Visibility.Collapsed;
        RenderGlobalStatusSummary();
        var overviewProfile = SavedProfiles.FirstOrDefault(profile => profile.Id == (OverviewProfileCombo.SelectedValue as string));
        var overviewApplyReason = ProfileApplyBlockedReason(overviewProfile);
        OverviewApplyProfileButton.IsEnabled = overviewApplyReason.Length == 0;
        OverviewApplyProfileButton.ToolTip = overviewApplyReason.Length > 0 ? overviewApplyReason :
            SafetyProtectionDisabled ? "明确应用选中的手动方案；不会恢复自动调节。" : "应用选中的已保存方案。";
        OverviewProfileText.Text = _lastStatus?.ProfileApplication is { } application
            ? $"最近应用：{application.ProfileName} · {(SafetyProtectionDisabled && application.Outcome == ProfileApplyOutcome.Applying ? "自动输出已停止" : ProfileOutcomeText(application.Outcome))}"
            : SavedProfiles.Count == 0 ? "从当前设置创建第一个方案" : "";
        OverviewProfileText.Visibility = OverviewProfileText.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        RenderProfilesStatus();
        RenderCurveDraft();
    }

    private static string ProfileOutcomeText(ProfileApplyOutcome outcome) => outcome switch
    {
        ProfileApplyOutcome.Applying => "正在应用", ProfileApplyOutcome.Completed => "已确认",
        ProfileApplyOutcome.Partial => "部分完成", ProfileApplyOutcome.Superseded => "已被后续操作替代",
        ProfileApplyOutcome.Interrupted => "已中断", _ => "未应用",
    };

    private string CurveFingerprint(IEnumerable<CurvePointRow>? rows = null) => string.Join(";", (rows ?? CurvePoints).Select(p =>
        p.TemperatureCelsius.ToString("R", CultureInfo.InvariantCulture) + ":" + p.FanPercent));
    internal bool CurveDraftChanged => curveBaseline is not null && curveBaseline != CurveFingerprint();
    private bool ConfirmCurveReplacement() => !CurveDraftChanged ||
        (ConfirmDraftReplacement?.Invoke("替换未应用的曲线草稿？") ??
        LocalizedMessageBox.Show(this, "此操作会替换未应用的曲线草稿。", "替换曲线草稿", MessageBoxButton.YesNo,
            MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes);
    private void AcceptCurveBaseline(string? fingerprint = null)
    {
        curveBaseline = fingerprint ?? CurveFingerprint(); curveWasRead = true; RenderCurveDraft();
    }
    private void CurveGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e) =>
        Dispatcher.BeginInvoke(new Action(RenderCurveDraft));
    private void RenderCurveDraft()
    {
        if (CurvePreviewPath is null) return;
        CurveDraftStateText.Text = CurveDraftChanged ? "曲线 · 未应用修改" : curveWasRead ? "曲线 · 已读取 / 保存" : "曲线 · 本地草稿";
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            var points = CurvePoints.Where(p => double.IsFinite(p.TemperatureCelsius)).ToArray();
            for (var i = 0; i < points.Length; i++)
            {
                var point = new Point(Math.Clamp(points[i].TemperatureCelsius,0,100)*6,
                    114-Math.Clamp(points[i].FanPercent,0,100)*1.12);
                if (i==0) context.BeginFigure(point,false,false); else context.LineTo(point,true,false);
            }
        }
        geometry.Freeze(); CurvePreviewPath.Data = geometry;
    }
}



