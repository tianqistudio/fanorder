using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using V12UltraController.Core.Configuration;
using V12UltraController.Core.Control;
using V12UltraController.Core.Hardware;
using V12UltraController.Core.Ipc;
using V12UltraController.Core.Status;

namespace V12UltraController.Settings;

public partial class MainWindow
{
    private IControlProfileStore _profileStore = new MemoryControlProfileStore();
    private IReadOnlyList<ControlProfileSnapshot> _savedProfiles = [];
    private readonly Dictionary<string, ProfileEditorDraft> _profileDrafts = new(StringComparer.Ordinal);
    private readonly ObservableCollection<ProfileListEntry> _profileEntries = [];
    private ListBox? _profileList;
    private StackPanel? _profileEditor;
    private TextBlock? _profileTitle;
    private TextBlock? _profileDraftLabel;
    private TextBlock? _profileResult;
    private ComboBox? _profileMode;
    private Slider? _profileManual;
    private StackPanel? _profileManualPanel;
    private StackPanel? _profileCurvePanel;
    private DataGrid? _profileCurveGrid;
    private CheckBox? _profileIncludeLighting;
    private CheckBox? _profileLightEnabled;
    private ComboBox? _profileLightMode;
    private ComboBox? _profileLightColor;
    private Slider? _profileLightBrightness;
    private Slider? _profileLightSpeed;
    private StackPanel? _profileLightingPanel;
    private Button? _profileCreate;
    private Button? _profileSave;
    private Button? _profileApply;
    private MenuItem? _profileCopy;
    private MenuItem? _profileRename;
    private MenuItem? _profileDelete;
    private string? _selectedProfileId;
    private bool _renderingProfile;
    private bool _profileStoreFailed;
    private CancellationTokenSource? _profileRequestCancellation;

    internal IReadOnlyList<ControlProfileSnapshot> SavedProfiles => ControlProfileStoreData.Copy(_savedProfiles);
    internal event Action? ProfilesChanged;
    internal Func<string, bool>? ConfirmProfileAction { get; set; }
    internal Func<string, string?>? RequestProfileName { get; set; }
    internal Action? BringProfileConfirmationToFront { get; set; }

    internal void InitializeProfiles(IControlProfileStore? store = null)
    {
        _profileStore = store ?? new MemoryControlProfileStore();
        try
        {
            _savedProfiles = _profileStore.Load();
            foreach (var profile in _savedProfiles)
            {
                var draft = new ProfileEditorDraft(profile, saved: true);
                ObserveProfileDraft(draft);
                _profileDrafts.Add(profile.Id, draft);
            }
        }
        catch (Exception exception) when (IsProfileException(exception))
        {
            _profileStoreFailed = true;
            ActivityText.Text = "本地方案未载入：" + exception.Message;
        }
        ProfilesHost.Content = BuildProfilesView();
        RebuildProfileList(_savedProfiles.FirstOrDefault()?.Id);
        if (_profileStoreFailed) SetProfileMessage("本地方案未载入；原文件已保留。关闭设置并修复文件后重试。");
        RenderProfilesStatus();
    }

    private FrameworkElement BuildProfilesView()
    {
        var layout = new Grid();
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(208) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var listPanel = new DockPanel();
        _profileCreate = ProfileButton("从当前配置创建", async () => await CreateProfileFromCurrentAsync(),
            "读取当前配置，创建本地草稿；保存和应用分开。");
        DockPanel.SetDock(_profileCreate, Dock.Top);
        _profileCreate.Margin = new Thickness(0, 0, 0, 12);
        listPanel.Children.Add(_profileCreate);
        _profileList = new ListBox { ItemsSource = _profileEntries, ItemTemplate = ProfileListTemplate(), BorderThickness = new Thickness(0) };
        var itemStyle = new Style(typeof(ListBoxItem));
        itemStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        itemStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(10, 12, 10, 12)));
        _profileList.ItemContainerStyle = itemStyle;
        ScrollViewer.SetHorizontalScrollBarVisibility(_profileList, ScrollBarVisibility.Disabled);
        _profileList.SetResourceReference(BackgroundProperty, "SurfaceBrush");
        AutomationProperties.SetName(_profileList, "命名方案");
        _profileList.SelectionChanged += (_, _) => SelectProfile((_profileList.SelectedItem as ProfileListEntry)?.Id);
        listPanel.Children.Add(_profileList);
        layout.Children.Add(listPanel);

        var content = new Grid();
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetColumn(content, 2);
        layout.Children.Add(content);
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(0, 0, 8, 12) };
        _profileEditor = new StackPanel();
        scroll.Content = _profileEditor;
        content.Children.Add(scroll);
        var heading = new DockPanel { Margin = new Thickness(0, 0, 0, 16) };
        var help = ProfileInfo("方案保存在本机。保存风扇模式、手动速度和曲线，可选灯光；不保存停扇、纯手动、释放、接管或关机策略。");
        _profileTitle = new TextBlock { Text = "选择或创建方案", FontSize = 24, FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        _profileTitle.SetBinding(ToolTipProperty, new Binding(nameof(TextBlock.Text)) { Source = _profileTitle });
        var more = new Button { Content = "⋯", ToolTip = "方案操作", Margin = new Thickness(8, 0, 0, 0) };
        more.SetResourceReference(StyleProperty, "CompactActionButtonStyle");
        AutomationProperties.SetName(more, "方案更多操作");
        var menu = new ContextMenu();
        _profileCopy = ProfileMenuItem("复制", DuplicateProfile);
        _profileRename = ProfileMenuItem("重命名", RenameProfile);
        _profileDelete = ProfileMenuItem("删除…", DeleteProfile);
        menu.Items.Add(_profileCopy); menu.Items.Add(_profileRename); menu.Items.Add(new Separator()); menu.Items.Add(_profileDelete);
        more.ContextMenu = menu;
        Localization.Attach(menu);
        more.Click += (_, _) => OpenActionMenu(more);
        DockPanel.SetDock(more, Dock.Right); heading.Children.Add(more);
        DockPanel.SetDock(help, Dock.Right);
        heading.Children.Add(help);
        heading.Children.Add(_profileTitle);
        _profileEditor.Children.Add(heading);

        var fanCard = new StackPanel();
        fanCard.Children.Add(ProfileSectionTitle("风扇", "曲线 2–20 点，温度递增，速度不降；保护状态看概览。手动方案不解除保护。"));
        _profileMode = new ComboBox { ItemsSource = new[] { "自动曲线", "手动速度" }, SelectedIndex = 0, Margin = new Thickness(0, 12, 0, 16) };
        AutomationProperties.SetName(_profileMode, "方案风扇模式");
        _profileMode.SelectionChanged += (_, _) => ProfileControlsChanged();
        fanCard.Children.Add(_profileMode);
        _profileManualPanel = new StackPanel();
        _profileManual = ProfileSlider(_profileManualPanel, "手动速度", "方案手动速度", 10, 100, "%");
        fanCard.Children.Add(_profileManualPanel);
        _profileCurvePanel = new StackPanel();
        _profileCurveGrid = new DataGrid
        {
            AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false,
            HeadersVisibility = DataGridHeadersVisibility.Column, MinHeight = 140, MaxHeight = 320,
            HorizontalAlignment = HorizontalAlignment.Stretch, ColumnHeaderHeight = 36,
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
        };
        _profileCurveGrid.SetResourceReference(Control.BackgroundProperty, "SurfaceBrush");
        _profileCurveGrid.SetResourceReference(Control.ForegroundProperty, "TextPrimaryBrush");
        _profileCurveGrid.SetResourceReference(Control.BorderBrushProperty, "BorderBrush");
        _profileCurveGrid.SetResourceReference(DataGrid.RowBackgroundProperty, "SurfaceBrush");
        var headerStyle = new Style(typeof(DataGridColumnHeader));
        headerStyle.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension("SurfaceRaisedBrush")));
        headerStyle.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("TextPrimaryBrush")));
        headerStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(12, 8, 12, 8)));
        headerStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Left));
        _profileCurveGrid.ColumnHeaderStyle = headerStyle;
        var cellStyle = new Style(typeof(TextBlock));
        cellStyle.Setters.Add(new Setter(MarginProperty, new Thickness(12, 6, 12, 6)));
        _profileCurveGrid.Columns.Add(new DataGridTextColumn { Header = "温度 °C", ElementStyle = cellStyle, MinWidth = 125,
            Binding = new Binding(nameof(ProfileCurveRow.TemperatureCelsius)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged, ValidatesOnExceptions = true }, Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        _profileCurveGrid.Columns.Add(new DataGridTextColumn { Header = "风扇 %", ElementStyle = cellStyle, MinWidth = 125,
            Binding = new Binding(nameof(ProfileCurveRow.FanPercent)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged, ValidatesOnExceptions = true }, Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        _profileCurveGrid.SizeChanged += (_, _) =>
        {
            // A DataGrid inside a vertical draft editor is measured before its
            // scroll viewport settles. Use its arranged width for equal columns.
            var width = Math.Max(125, (_profileCurveGrid.ActualWidth - 24) / 2);
            foreach (var column in _profileCurveGrid.Columns)
                if (Math.Abs(column.Width.Value - width) > 0.5 || column.Width.IsStar)
                    column.Width = new DataGridLength(width);
        };
        AutomationProperties.SetName(_profileCurveGrid, "方案自动曲线");
        _profileCurvePanel.Children.Add(_profileCurveGrid);
        var curveActions = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        curveActions.Children.Add(ProfileButton("添加点", () => { AddProfileCurvePoint(); return Task.CompletedTask; }, "最多 20 个点；新增后需保证温度严格递增。"));
        curveActions.Children.Add(ProfileButton("删除选中点", () => { RemoveProfileCurvePoint(); return Task.CompletedTask; }, "至少保留 2 个点。"));
        _profileCurvePanel.Children.Add(curveActions);
        fanCard.Children.Add(_profileCurvePanel);
        _profileEditor.Children.Add(ProfileCard(fanCard));

        var lightCard = new StackPanel();
        lightCard.Children.Add(ProfileSectionTitle("灯光", "灯光只用设备预设；取消勾选则不改灯光。"));
        _profileIncludeLighting = new CheckBox { Content = "同时应用灯光", Margin = new Thickness(0, 12, 0, 12) };
        _profileIncludeLighting.Checked += ProfileLightingInclusionChanged;
        _profileIncludeLighting.Unchecked += ProfileLightingInclusionChanged;
        lightCard.Children.Add(_profileIncludeLighting);
        _profileLightingPanel = new StackPanel();
        _profileLightEnabled = new CheckBox { Content = "开启灯光", Margin = new Thickness(0, 0, 0, 12) };
        _profileLightEnabled.Checked += (_, _) => ProfileControlsChanged();
        _profileLightEnabled.Unchecked += (_, _) => ProfileControlsChanged();
        _profileLightingPanel.Children.Add(_profileLightEnabled);
        _profileLightMode = ProfileCombo(_profileLightingPanel, "灯效", ["单色常亮", "单色呼吸", "多彩 · 01", "多彩 · 02"]);
        _profileLightColor = ProfileCombo(_profileLightingPanel, "颜色", ["红色", "蓝色", "绿色", "紫色", "橙色"]);
        _profileLightBrightness = ProfileSlider(_profileLightingPanel, "亮度", "方案灯光亮度", 0, 255, "");
        _profileLightSpeed = ProfileSlider(_profileLightingPanel, "变化速度", "方案灯光变化速度", 0, 3, "");
        lightCard.Children.Add(_profileLightingPanel);
        _profileEditor.Children.Add(ProfileCard(lightCard));

        var footer = new StackPanel();
        _profileDraftLabel = new TextBlock { Text = "暂无方案", Margin = new Thickness(0, 0, 0, 8) };
        footer.Children.Add(_profileDraftLabel);
        _profileResult = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
        AutomationProperties.SetLiveSetting(_profileResult, AutomationLiveSetting.Polite);
        footer.Children.Add(_profileResult);
        var actions = new WrapPanel();
        _profileSave = ProfileButton("保存方案", SaveProfileDraftAsync, "只保存草稿，不控制设备。", primary: true);
        _profileApply = ProfileButton("应用已保存方案", async () => { if (_selectedProfileId is { } id) await ApplySavedProfileAsync(id); }, "应用已保存版本，保留未保存草稿；后台负责验证。", primary: true);
        actions.Children.Add(_profileSave);
        actions.Children.Add(_profileApply);
        footer.Children.Add(actions);
        Grid.SetRow(footer, 1);
        content.Children.Add(footer);
        return layout;
    }

    private MenuItem ProfileMenuItem(string label, Action action)
    {
        var item = new MenuItem { Header = label };
        AutomationProperties.SetName(item, label + "方案");
        ToolTipService.SetShowOnDisabled(item, true);
        item.Click += (_, _) =>
        {
            try { action(); }
            catch (Exception exception) when (IsProfileException(exception)) { SetProfileMessage(exception.Message); }
        };
        return item;
    }

    private static DataTemplate ProfileListTemplate()
    {
        var panel = new FrameworkElementFactory(typeof(StackPanel));
        void Text(string property, string brush, double size, bool bold = false)
        {
            var text = new FrameworkElementFactory(typeof(TextBlock));
            text.SetBinding(TextBlock.TextProperty, new Binding(property));
            text.SetValue(TextBlock.FontSizeProperty, size);
            text.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
            text.SetValue(MarginProperty, new Thickness(0, 0, 0, 4));
            if (bold) text.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
            text.SetResourceReference(TextBlock.ForegroundProperty, brush);
            panel.AppendChild(text);
        }
        Text(nameof(ProfileListEntry.Label), "TextPrimaryBrush", 14, true);
        Text(nameof(ProfileListEntry.Summary), "TextSecondaryBrush", 12);
        Text(nameof(ProfileListEntry.ApplicationText), "PrimaryBrush", 12);
        return new DataTemplate { VisualTree = panel };
    }

    private Button ProfileButton(string label, Func<Task> action, string help, bool primary = false)
    {
        var button = new Button { Content = label, Margin = new Thickness(0, 0, 8, 4), ToolTip = help };
        button.SetResourceReference(StyleProperty, primary ? "ActionButtonStyle" : "SecondaryButtonStyle");
        AutomationProperties.SetName(button, label);
        button.Click += async (_, _) =>
        {
            try { await action(); }
            catch (Exception exception) when (IsProfileException(exception)) { SetProfileMessage(exception.Message); }
        };
        return button;
    }

    private static Border ProfileCard(UIElement content)
    {
        var card = new Border { Child = content, Margin = new Thickness(0, 0, 0, 16) };
        card.SetResourceReference(StyleProperty, "CardStyle");
        return card;
    }

    private static Button ProfileInfo(string explanation, string accessibleName = "方案说明")
    {
        var tip = new ToolTip { Content = new TextBlock { Text = explanation, TextWrapping = TextWrapping.Wrap, MaxWidth = 360 } };
        var button = new Button { Content = "i", ToolTip = tip, VerticalAlignment = VerticalAlignment.Center };
        button.SetResourceReference(StyleProperty, "InfoButtonStyle");
        AutomationProperties.SetName(button, accessibleName);
        AutomationProperties.SetHelpText(button, explanation);
        return button;
    }

    private static StackPanel ProfileSectionTitle(string title, string help)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 17, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        panel.Children.Add(ProfileInfo(help, title + "方案说明"));
        return panel;
    }

    private ComboBox ProfileCombo(Panel panel, string label, string[] values)
    {
        panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 5) });
        var combo = new ComboBox { ItemsSource = values, SelectedIndex = 0, Margin = new Thickness(0, 0, 0, 12) };
        AutomationProperties.SetName(combo, "方案灯光" + label);
        combo.SelectionChanged += (_, _) => ProfileControlsChanged();
        panel.Children.Add(combo);
        return combo;
    }

    private Slider ProfileSlider(Panel panel, string label, string accessibleName, int minimum, int maximum, string suffix)
    {
        var slider = new Slider { Minimum = minimum, Maximum = maximum, TickFrequency = 1, IsSnapToTickEnabled = true, Margin = new Thickness(0, 5, 0, 16) };
        var value = new TextBlock();
        value.SetBinding(TextBlock.TextProperty, maximum == 255
            ? new Binding(nameof(Slider.Value)) { Source = slider, Converter = new BrightnessPercentConverter(), StringFormat = label + " · {0}" }
            : new Binding(nameof(Slider.Value)) { Source = slider, StringFormat = label + " · {0:F0}" + suffix });
        panel.Children.Add(value);
        AutomationProperties.SetName(slider, accessibleName);
        slider.ValueChanged += (_, _) => ProfileControlsChanged();
        panel.Children.Add(slider);
        return slider;
    }

    private ProfileEditorDraft? SelectedProfileDraft => _selectedProfileId is { } id && _profileDrafts.TryGetValue(id, out var draft) ? draft : null;

    private void SelectProfile(string? id)
    {
        _selectedProfileId = id;
        var draft = SelectedProfileDraft;
        _renderingProfile = true;
        try
        {
            _profileTitle!.Text = draft?.Name ?? "创建你的第一个方案";
            _profileEditor!.IsEnabled = draft is not null;
            if (draft is not null)
            {
                _profileMode!.SelectedIndex = draft.Mode == ControlMode.Auto ? 0 : 1;
                _profileManual!.Value = draft.ManualSpeedPercent;
                _profileCurveGrid!.ItemsSource = draft.Points;
                _profileIncludeLighting!.IsChecked = draft.IncludeLighting;
                var lighting = draft.Lighting;
                _profileLightEnabled!.IsChecked = lighting?.Enabled ?? true;
                _profileLightMode!.SelectedIndex = lighting?.Mode ?? 0;
                _profileLightColor!.SelectedIndex = lighting?.Color ?? 0;
                _profileLightBrightness!.Value = lighting?.Brightness ?? 128;
                _profileLightSpeed!.Value = lighting?.Speed ?? 1;
            }
            else _profileCurveGrid!.ItemsSource = null;
        }
        finally { _renderingProfile = false; }
        RenderProfilesStatus();
    }

    private void ProfileControlsChanged()
    {
        if (_renderingProfile || SelectedProfileDraft is not { } draft || _profileLightSpeed is null) return;
        draft.Mode = _profileMode!.SelectedIndex == 0 ? ControlMode.Auto : ControlMode.Manual;
        draft.ManualSpeedPercent = (byte)_profileManual!.Value;
        draft.IncludeLighting = _profileIncludeLighting!.IsChecked == true;
        if (draft.IncludeLighting)
            draft.Lighting = new LightingSettings(_profileLightEnabled!.IsChecked == true, _profileLightMode!.SelectedIndex,
                _profileLightColor!.SelectedIndex, (int)_profileLightBrightness!.Value, (int)_profileLightSpeed.Value);
        MarkProfileDirty(draft);
    }

    private void ProfileLightingInclusionChanged(object sender, RoutedEventArgs e)
    {
        if (_renderingProfile) return;
        ProfileControlsChanged();
    }

    private void MarkProfileDirty(ProfileEditorDraft draft)
    {
        draft.Dirty = true;
        _profileEntries.FirstOrDefault(entry => entry.Id == draft.Id)?.Update(draft);
        RenderProfilesStatus();
    }

    private void RebuildProfileList(string? selectId)
    {
        _profileEntries.Clear();
        foreach (var draft in _profileDrafts.Values) _profileEntries.Add(new ProfileListEntry(draft));
        _profileList!.SelectedItem = _profileEntries.FirstOrDefault(entry => entry.Id == selectId) ?? _profileEntries.FirstOrDefault();
        if (_profileList.SelectedItem is null) SelectProfile(null);
    }

    private void ObserveProfileDraft(ProfileEditorDraft draft)
    {
        foreach (var row in draft.Points) row.PropertyChanged += (_, _) => MarkProfileDirty(draft);
    }

    private async Task CreateProfileFromCurrentAsync()
    {
        if (!ServiceReadAvailable || _profileStoreFailed) return;
        await RunCommandAsync(async () =>
        {
            var statusResponse = await SendCheckedAsync(PipeOperations.StatusGet, new { });
            var status = statusResponse.Data!.Value.Deserialize<ControllerStatus>(JsonOptions)
                ?? throw new InvalidDataException("未读取到有效后台状态。");
            if (status.Fan.ConfiguredManualPercent is not { } manual)
                throw new InvalidDataException("后台未返回持久化手动速度，不能猜测方案设置。");
            var curveResponse = await SendCheckedAsync(PipeOperations.CurveGet, new { });
            var curveData = curveResponse.Data!.Value;
            if (curveData.TryGetProperty("curve", out var nested)) curveData = nested;
            var curve = curveData.Deserialize<FanCurve>(JsonOptions) ?? throw new InvalidDataException("后台未返回有效自动曲线。");
            curve.Validate();
            LightingSettings? lighting = null;
            if (status.Software?.LightingSupported == true)
            {
                var lightingResponse = await SendCheckedAsync(PipeOperations.LightingGet, new { });
                var data = lightingResponse.Data!.Value;
                if ((data.TryGetProperty("supported", out var supported) && supported.ValueKind == JsonValueKind.True ||
                     status.Software.ManualActionsWhileProtectionDisabledSupported) &&
                    data.TryGetProperty("settings", out var settings) && settings.ValueKind == JsonValueKind.Object)
                {
                    lighting = settings.Deserialize<LightingSettings>(JsonOptions);
                    lighting?.Validate();
                }
            }
            var snapshot = new ControlProfileSnapshot(Guid.NewGuid().ToString("N"), UniqueProfileName("新方案"),
                status.Fan.Mode == ControlMode.Auto ? ControlMode.Auto : ControlMode.Manual, manual, curve, lighting);
            snapshot.Validate();
            var draft = new ProfileEditorDraft(snapshot, saved: false);
            ObserveProfileDraft(draft);
            _profileDrafts.Add(draft.Id, draft);
            RebuildProfileList(draft.Id);
            SetProfileMessage(lighting is null ? "已从后台创建草稿；未包含灯光。" : "已从后台当前配置与灯光缓存创建草稿。");
        });
        RenderProfilesStatus();
    }

    internal Task SaveProfileDraftAsync()
    {
        if (_profileStoreFailed || SelectedProfileDraft is not { } draft) return Task.CompletedTask;
        try
        {
            if (_profileCurveGrid is not null &&
                (!_profileCurveGrid.CommitEdit(DataGridEditingUnit.Cell, true) || !_profileCurveGrid.CommitEdit(DataGridEditingUnit.Row, true)))
                throw new InvalidDataException("请先修正曲线中的无效数值。");
            var snapshot = draft.Snapshot();
            snapshot.Validate();
            var replacement = _savedProfiles.Where(profile => profile.Id != snapshot.Id).Append(snapshot).ToArray();
            _profileStore.Save(replacement);
            _savedProfiles = ControlProfileStoreData.Copy(replacement);
            draft.Saved = true;
            draft.Dirty = false;
            _profileEntries.FirstOrDefault(entry => entry.Id == draft.Id)?.Update(draft);
            ProfilesChanged?.Invoke();
            SetProfileMessage("方案已保存到本机。");
        }
        catch (Exception exception) when (IsProfileException(exception)) { SetProfileMessage(exception.Message); }
        RenderProfilesStatus();
        return Task.CompletedTask;
    }

    private void DuplicateProfile()
    {
        if (_profileStoreFailed || _savedProfiles.FirstOrDefault(profile => profile.Id == _selectedProfileId) is not { } saved) return;
        var snapshot = ControlProfileStoreData.Copy(saved) with { Id = Guid.NewGuid().ToString("N"), Name = UniqueProfileName(saved.Name + " 副本") };
        var draft = new ProfileEditorDraft(snapshot, saved: false);
        ObserveProfileDraft(draft);
        _profileDrafts.Add(draft.Id, draft);
        RebuildProfileList(draft.Id);
        SetProfileMessage("已复制已保存版本，等待保存新方案。");
    }

    private void RenameProfile()
    {
        if (_profileStoreFailed || SelectedProfileDraft is not { } draft) return;
        var requested = RequestProfileName?.Invoke(draft.Name) ?? (RequestProfileName is null ? ShowProfileNameDialog(draft.Name) : null);
        if (requested is null) return;
        var name = requested.Trim();
        if (name.Length is < 1 or > 80 || name.Any(char.IsControl))
            throw new InvalidDataException("方案名称必须为 1–80 个非控制字符。");
        if (_profileDrafts.Values.Any(other => other.Id != draft.Id && string.Equals(other.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("该方案名称已经存在。");
        if (draft.Saved)
        {
            var updated = _savedProfiles.Select(profile => profile.Id == draft.Id ? profile with { Name = name } : profile).ToArray();
            _profileStore.Save(updated);
            _savedProfiles = ControlProfileStoreData.Copy(updated);
        }
        draft.Name = name;
        _profileTitle!.Text = name;
        _profileEntries.FirstOrDefault(entry => entry.Id == draft.Id)?.Update(draft);
        ProfilesChanged?.Invoke();
        SetProfileMessage("方案已重命名，快捷键绑定保留。");
    }

    private void DeleteProfile()
    {
        if (_profileStoreFailed || SelectedProfileDraft is not { } draft) return;
        if (!ConfirmProfile($"删除方案“{draft.Name}”？本地草稿和此方案的快捷键绑定也会删除。")) return;
        var remaining = _savedProfiles.Where(profile => profile.Id != draft.Id).ToArray();
        if (draft.Saved) _profileStore.Save(remaining);
        _savedProfiles = ControlProfileStoreData.Copy(remaining);
        _profileDrafts.Remove(draft.Id);
        RebuildProfileList(null);
        ProfilesChanged?.Invoke();
        SetProfileMessage("方案已删除。");
    }

    internal async Task ApplySavedProfileAsync(string id)
    {
        var profile = _savedProfiles.FirstOrDefault(item => item.Id == id);
        var unavailable = ProfileApplyBlockedReason(profile);
        if (unavailable.Length > 0)
        {
            SetProfileMessage(unavailable);
            return;
        }
        if (profile is null) { SetProfileMessage("方案尚未保存或已删除。"); return; }
        profile = ControlProfileStoreData.Copy(profile);
        var keepProtectionDisabled = SafetyProtectionDisabled;
        var resume = !SafetyProtectionDisabled && (_lastStatus!.Fan.Mode is ControlMode.PureManual or ControlMode.Released);
        var fanOff = _lastStatus!.Fan.PoweredOn == false || _lastStatus.Fan.OffRequested;
        if (resume || fanOff)
        {
            if (BringProfileConfirmationToFront is { } bringToFront) bringToFront();
            else ShowFromTray();
            var explanation = resume ? "应用方案会恢复温控保护并重新接管风扇。" : "";
            if (fanOff) explanation += "当前风扇关闭；应用方案会开启风扇。";
            if (!ConfirmProfile(explanation + $"\n\n确认应用“{profile.Name}”？")) return;
        }
        // Drop accumulated speed input before applying a named snapshot; a delayed
        // tray wheel request must not overwrite the mode immediately afterwards.
        trayDebounce?.Stop();
        trayPendingPercent = null;
        trayWheel.Reset();
        await RunCommandAsync(async () =>
        {
            var requestGeneration = _controlRequestGeneration;
            using var requestLifetime = CancellationTokenSource.CreateLinkedTokenSource(_windowLifetime.Token);
            _profileRequestCancellation = requestLifetime;
            SetProfileMessage($"正在应用“{profile.Name}”…");
            try
            {
                var response = await SendCheckedAsync(PipeOperations.ProfileApply, new ProfileApplyRequest(profile, resume),
                    requestLifetime.Token, TimeSpan.FromSeconds(25));
                if (requestGeneration != _controlRequestGeneration) return;
                var result = response.Data!.Value.Deserialize<ProfileApplyResult>(JsonOptions)
                    ?? throw new InvalidDataException("后台没有返回方案应用状态。");
                if (result.Application is null || result.Status is null ||
                    !string.Equals(result.Application.ProfileId, profile.Id, StringComparison.Ordinal) ||
                    !string.Equals(result.Application.OperationId, response.RequestId, StringComparison.Ordinal))
                    throw new InvalidDataException("后台返回的方案标识不匹配，请刷新确认。");
                if (keepProtectionDisabled && result.Status.SafetyProtection is not { Disabled: true, AutomaticOutputStopped: true })
                    throw new InvalidDataException("后台未确认方案应用后仍保持停控，请重新读取状态。");
                RenderStatus(result.Status);
                SetProfileMessage(result.Application.Outcome == ProfileApplyOutcome.Completed
                    ? $"“{profile.Name}”已应用并完成验证。"
                    : $"“{profile.Name}”未完整应用：{result.Application.Reason}");
            }
            catch (Exception exception) when (IsExpectedClientException(exception))
            {
                if (requestGeneration != _controlRequestGeneration) return;
                SetProfileMessage("方案应用结果未确认：" + ExplainException(exception));
                throw;
            }
            finally
            {
                if (ReferenceEquals(_profileRequestCancellation, requestLifetime)) _profileRequestCancellation = null;
            }
        }, refreshAfterSuccess: true);
        RenderProfilesStatus();
    }

    private string ProfileApplyBlockedReason(ControlProfileSnapshot? profile) => profile is null ? "请先选择并保存一个方案。" :
        !StateMutationAvailable || _lastStatus?.Commissioned != true || _serviceCommissioningActive ||
        _currentMode == ControlMode.Released ? ControlBlockedReason :
        _lastStatus.Software?.ProfileApplySupported != true ? "当前后台不支持方案应用，请更新后台。" :
        SafetyProtectionDisabled && profile.Mode == ControlMode.Auto ? "自动方案暂停；可以编辑和保存，恢复保护后才可应用自动方案。" :
        SafetyProtectionDisabled && !ExplicitLightingSupported ? "此后台不支持停控状态下应用手动方案，请更新后台；草稿仍可编辑保存。" :
        !_lastStatus.Device.Connected && !ExplicitManualContext ? "设备尚未连接，不能应用方案。" : "";

    internal void RenderProfilesStatus()
    {
        if (_profileList is null) return;
        foreach (var entry in _profileEntries)
            entry.UpdateApplication(_lastStatus?.ProfileApplication is { } applied && applied.ProfileId == entry.Id
                ? "最近应用 · " + ProfileOutcomeText(applied.Outcome) : "");
        var draft = SelectedProfileDraft;
        var editable = draft is not null && !_profileStoreFailed;
        _profileCreate!.IsEnabled = !_profileStoreFailed && ServiceReadAvailable;
        _profileCreate.ToolTip = _profileStoreFailed ? "方案文件读取失败，原文件已保留。" :
            !ServiceReadAvailable ? "连接后台读取配置；本地方案仍可编辑。" :
            "读取后台配置，创建本地草稿；不控制设备。";
        _profileSave!.IsEnabled = editable && (draft!.Dirty || !draft.Saved);
        _profileCopy!.IsEnabled = editable && draft!.Saved;
        _profileRename!.IsEnabled = editable;
        _profileDelete!.IsEnabled = editable;
        var applyReason = ProfileApplyBlockedReason(draft?.Saved == true ? _savedProfiles.FirstOrDefault(item => item.Id == draft.Id) : null);
        _profileApply!.IsEnabled = applyReason.Length == 0;
        _profileApply.ToolTip = applyReason.Length > 0 ? applyReason : SafetyProtectionDisabled
            ? "应用已保存的手动转速和灯光；自动调节仍暂停。"
            : "应用已保存版本，保留未保存草稿；后台负责验证。";
        _profileSave.ToolTip = !editable ? "请先选择或创建本地方案。" : !draft!.Dirty && draft.Saved
            ? "当前没有未保存的修改。" : "只保存本地草稿；不会应用到设备。";
        _profileCopy.ToolTip = !editable ? "请先选择一个方案。" : !draft!.Saved ? "先保存新方案，再复制已保存版本。" : "复制已保存版本为独立草稿。";
        _profileRename.ToolTip = _profileDelete.ToolTip = !editable ? "请先选择一个本地方案。" : "只修改本机方案，不改变设备。";
        _profileManualPanel!.Visibility = draft?.Mode == ControlMode.Manual ? Visibility.Visible : Visibility.Collapsed;
        _profileCurvePanel!.Visibility = draft?.Mode == ControlMode.Auto ? Visibility.Visible : Visibility.Collapsed;
        _profileLightingPanel!.Visibility = draft?.IncludeLighting == true ? Visibility.Visible : Visibility.Collapsed;
        _profileLightColor!.IsEnabled = _profileLightMode?.SelectedIndex is 0 or 1;
        _profileDraftLabel!.Text = draft is null ? "暂无方案" : !draft.Saved ? "● 尚未保存" : draft.Dirty ? "● 有未保存修改 · 应用使用已保存版本" : "已保存";
        if (draft?.Saved == true && _lastStatus?.ProfileApplication is { } application && application.ProfileId == draft.Id)
            _profileDraftLabel.Text += " · " + (application.Outcome switch
            {
                ProfileApplyOutcome.Completed => "上次应用已确认",
                ProfileApplyOutcome.Applying => SafetyProtectionDisabled ? "自动输出已停止" : "正在应用",
                ProfileApplyOutcome.Partial => "部分完成",
                ProfileApplyOutcome.Superseded => "后台设置已更改",
                ProfileApplyOutcome.Interrupted => "应用已中断",
                _ => "应用未确认",
            });
    }

    private void AddProfileCurvePoint()
    {
        if (SelectedProfileDraft is not { } draft || draft.Points.Count >= 20) return;
        var existing = draft.Points.Select(point => point.TemperatureCelsius).ToHashSet();
        var temperature = Enumerable.Range(0, 101).Select(value => (double)value).FirstOrDefault(value => !existing.Contains(value), double.NaN);
        if (double.IsNaN(temperature)) return;
        var insertion = draft.Points.TakeWhile(point => point.TemperatureCelsius < temperature).Count();
        var percent = insertion == 0 ? draft.Points[0].FanPercent : draft.Points[insertion - 1].FanPercent;
        var row = new ProfileCurveRow(temperature, percent);
        row.PropertyChanged += (_, _) => MarkProfileDirty(draft);
        draft.Points.Insert(insertion, row);
        _profileCurveGrid!.SelectedItem = row;
        MarkProfileDirty(draft);
    }

    private void RemoveProfileCurvePoint()
    {
        if (SelectedProfileDraft is not { } draft || draft.Points.Count <= 2 || _profileCurveGrid?.SelectedItem is not ProfileCurveRow row) return;
        draft.Points.Remove(row);
        MarkProfileDirty(draft);
    }

    private string UniqueProfileName(string prefix)
    {
        if (prefix.Length > 48) prefix = prefix[..48];
        var name = prefix;
        for (var index = 2; _profileDrafts.Values.Any(draft => string.Equals(draft.Name, name, StringComparison.OrdinalIgnoreCase)); index++) name = prefix + " " + index;
        return name;
    }

    private void SetProfileMessage(string message)
    {
        if (_profileResult is not null) _profileResult.Text = message;
        ActivityText.Text = message;
    }

    private bool ConfirmProfile(string message) => ConfirmProfileAction?.Invoke(message) ??
        MessageBox.Show(this, message, "风序 · 方案", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;

    private string? ShowProfileNameDialog(string currentName)
    {
        var dialog = new Window { Owner = this, Title = "重命名方案", Width = 380, SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false };
        dialog.SetResourceReference(BackgroundProperty, "WindowBrush");
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = "方案名称", Margin = new Thickness(0, 0, 0, 8) });
        var input = new TextBox { Text = currentName, MaxLength = 64, Margin = new Thickness(0, 0, 0, 20) };
        AutomationProperties.SetName(input, "方案名称");
        panel.Children.Add(input);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "取消", IsCancel = true, Margin = new Thickness(0, 0, 8, 0) };
        cancel.SetResourceReference(StyleProperty, "SecondaryButtonStyle");
        var save = new Button { Content = "重命名", IsDefault = true };
        save.SetResourceReference(StyleProperty, "ActionButtonStyle");
        save.Click += (_, _) => dialog.DialogResult = true;
        actions.Children.Add(cancel); actions.Children.Add(save); panel.Children.Add(actions);
        dialog.Content = panel;
        dialog.Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
        return dialog.ShowDialog() == true ? input.Text : null;
    }

    private static bool IsProfileException(Exception exception) => exception is IOException or UnauthorizedAccessException or OverflowException or
        InvalidOperationException or ArgumentException or JsonException or System.Security.SecurityException;

    private sealed class ProfileEditorDraft
    {
        internal ProfileEditorDraft(ControlProfileSnapshot snapshot, bool saved)
        {
            Id = snapshot.Id; Name = snapshot.Name; Mode = snapshot.Mode; ManualSpeedPercent = snapshot.ManualSpeedPercent;
            Points = new ObservableCollection<ProfileCurveRow>(snapshot.Curve.Points.Select(point => new ProfileCurveRow(point.TemperatureCelsius, point.FanPercent)));
            IncludeLighting = snapshot.Lighting is not null; Lighting = snapshot.Lighting; Saved = saved; Dirty = !saved;
        }
        internal string Id { get; }
        internal string Name { get; set; }
        internal ControlMode Mode { get; set; }
        internal byte ManualSpeedPercent { get; set; }
        internal ObservableCollection<ProfileCurveRow> Points { get; }
        internal bool IncludeLighting { get; set; }
        internal LightingSettings? Lighting { get; set; }
        internal bool Saved { get; set; }
        internal bool Dirty { get; set; }
        internal ControlProfileSnapshot Snapshot() => new(Id, Name, Mode, ManualSpeedPercent,
            new FanCurve(Points.Select(point => new FanCurvePoint(point.TemperatureCelsius, checked((byte)point.FanPercent))).ToArray()),
            IncludeLighting ? Lighting : null);
    }

    private sealed class ProfileListEntry : INotifyPropertyChanged
    {
        internal ProfileListEntry(ProfileEditorDraft draft) { Id = draft.Id; Update(draft); }
        public string Id { get; }
        public string Label { get; private set; } = "";
        public string Summary { get; private set; } = "";
        public string ApplicationText { get; private set; } = "";
        public event PropertyChangedEventHandler? PropertyChanged;
        internal void Update(ProfileEditorDraft draft)
        {
            Label = draft.Name + (draft.Dirty || !draft.Saved ? "  ●" : "");
            Summary = (draft.Mode == ControlMode.Manual ? $"手动 {draft.ManualSpeedPercent}%" : $"自动 · {draft.Points.Count} 个曲线点")
                + (draft.IncludeLighting ? " · 含灯光" : " · 仅风扇");
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Label)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Summary)));
        }
        internal void UpdateApplication(string text)
        {
            if (ApplicationText == text) return;
            ApplicationText = text;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ApplicationText)));
        }
    }

    public sealed class ProfileCurveRow : INotifyPropertyChanged
    {
        private double _temperature;
        private int _percent;
        internal ProfileCurveRow(double temperature, int percent) { _temperature = temperature; _percent = percent; }
        public double TemperatureCelsius { get => _temperature; set { if (_temperature.Equals(value)) return; _temperature = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TemperatureCelsius))); } }
        public int FanPercent { get => _percent; set { if (_percent == value) return; _percent = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FanPercent))); } }
        public event PropertyChangedEventHandler? PropertyChanged;
    }
}

