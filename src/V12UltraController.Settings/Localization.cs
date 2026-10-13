using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace V12UltraController.Settings;

internal enum UiLanguage
{
    SimplifiedChinese,
    English,
}

internal interface ILanguagePreference
{
    UiLanguage Load();
    void Save(UiLanguage language);
}

internal sealed class LocalLanguagePreference : ILanguagePreference
{
    private static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TianqiStudio", "V12UltraController");
    private static readonly string FilePath = Path.Combine(DirectoryPath, "language.txt");

    public UiLanguage Load()
    {
        try
        {
            var value = File.ReadAllText(FilePath).Trim();
            return value.Equals("en", StringComparison.OrdinalIgnoreCase)
                ? UiLanguage.English
                : UiLanguage.SimplifiedChinese;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return UiLanguage.SimplifiedChinese;
        }
    }

    public void Save(UiLanguage language)
    {
        Directory.CreateDirectory(DirectoryPath);
        var temporary = FilePath + ".tmp";
        File.WriteAllText(temporary, language == UiLanguage.English ? "en\n" : "zh-CN\n");
        File.Move(temporary, FilePath, overwrite: true);
    }
}

/// <summary>
/// Lightweight UI localization for the settings window. Chinese remains the
/// source language so existing persisted values and automation checks remain
/// stable. Runtime-created WPF controls are attached as they are created.
/// </summary>
internal static class Localization
{
    private sealed class ElementState
    {
        internal readonly Dictionary<DependencyProperty, string> Sources = new();
        internal readonly Dictionary<DependencyProperty, string> Rendered = new();
        internal readonly Dictionary<DependencyProperty, EventHandler> Handlers = new();
        internal bool Updating;
        internal bool ItemsHooked;
    }

    private sealed class ColumnState
    {
        internal string? Source;
    }

    private static readonly ConditionalWeakTable<DependencyObject, ElementState> States = new();
    private static readonly ConditionalWeakTable<DataGridColumn, ColumnState> ColumnStates = new();
    private static readonly Dictionary<string, string> ChineseToEnglish = CreateTranslations();
    private static readonly Dictionary<string, string> EnglishToChinese = ChineseToEnglish
        .GroupBy(pair => pair.Value, StringComparer.Ordinal)
        .ToDictionary(group => group.Key, group => group.First().Key, StringComparer.Ordinal);

    internal static UiLanguage CurrentLanguage { get; private set; } = UiLanguage.SimplifiedChinese;

    internal static void SetLanguage(UiLanguage language) => CurrentLanguage = language;

    internal static string Translate(string? source)
    {
        if (string.IsNullOrEmpty(source) || CurrentLanguage == UiLanguage.SimplifiedChinese)
            return source ?? string.Empty;
        if (ChineseToEnglish.TryGetValue(source, out var translated)) return translated;
        // Status text often contains a measured value or timestamp. Translate
        // known fragments while preserving the value supplied by the backend.
        foreach (var pair in ChineseToEnglish.OrderByDescending(pair => pair.Key.Length))
            if (pair.Key.Length >= 3 && source.Contains(pair.Key, StringComparison.Ordinal))
                source = source.Replace(pair.Key, pair.Value, StringComparison.Ordinal);
        return source;
    }

    internal static void Attach(DependencyObject root)
    {
        var visited = new HashSet<DependencyObject>();
        AttachNode(root, visited);
    }

    private static void AttachNode(DependencyObject node, HashSet<DependencyObject> visited)
    {
        if (!visited.Add(node)) return;
        var state = States.GetOrCreateValue(node);
        foreach (var property in LocalizedProperties(node))
            Watch(node, state, property);
        if (node is DataGrid grid)
            AttachColumns(grid);
        if (node is ItemsControl items && !state.ItemsHooked)
        {
            state.ItemsHooked = true;
            items.ItemContainerGenerator.StatusChanged += (_, _) =>
            {
                var generated = new HashSet<DependencyObject>();
                for (var i = 0; i < items.Items.Count; i++)
                {
                    if (items.ItemContainerGenerator.ContainerFromIndex(i) is DependencyObject child)
                        AttachNode(child, generated);
                }
            };
        }

        foreach (var child in Children(node))
            AttachNode(child, visited);
    }

    private static IEnumerable<DependencyProperty> LocalizedProperties(DependencyObject node)
    {
        if (node is Window)
            yield return Window.TitleProperty;
        if (node is TextBlock)
            yield return TextBlock.TextProperty;
        if (node is ContentControl)
            yield return ContentControl.ContentProperty;
        if (node is HeaderedContentControl)
            yield return HeaderedContentControl.HeaderProperty;
        if (node is HeaderedItemsControl)
            yield return HeaderedItemsControl.HeaderProperty;
        yield return ToolTipService.ToolTipProperty;
        yield return AutomationProperties.NameProperty;
    }

    private static void Watch(DependencyObject node, ElementState state, DependencyProperty property)
    {
        if (!state.Sources.ContainsKey(property))
        {
            var current = node.GetValue(property) as string;
            if (current is null) return;
            state.Sources[property] = SourceFor(current);
            state.Rendered[property] = current;
        }

        if (!state.Handlers.ContainsKey(property))
        {
            try
            {
                var descriptor = DependencyPropertyDescriptor.FromProperty(property, node.GetType());
                if (descriptor is not null)
                {
                    EventHandler handler = (_, _) => Changed(node, state, property);
                    descriptor.AddValueChanged(node, handler);
                    state.Handlers[property] = handler;
                }
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
            {
                // Some attached properties do not expose a descriptor on every
                // WPF control. They are still translated during each Attach pass.
            }
        }
        Apply(node, state, property);
    }

    private static string SourceFor(string value) => EnglishToChinese.TryGetValue(value, out var source) ? source : value;

    private static void Changed(DependencyObject node, ElementState state, DependencyProperty property)
    {
        if (state.Updating) return;
        var current = node.GetValue(property) as string;
        if (current is null) return;
        if (state.Rendered.TryGetValue(property, out var rendered) && current == rendered) return;
        state.Sources[property] = SourceFor(current);
        Apply(node, state, property);
    }

    private static void Apply(DependencyObject node, ElementState state, DependencyProperty property)
    {
        if (!state.Sources.TryGetValue(property, out var source)) return;
        var localized = Translate(source);
        state.Rendered[property] = localized;
        if ((node.GetValue(property) as string) == localized) return;
        state.Updating = true;
        // Localize the displayed value without replacing its binding or style.
        // A later source update must still reach Changed (for example a profile
        // speed slider after switching languages).
        try { node.SetCurrentValue(property, localized); }
        finally { state.Updating = false; }
    }

    private static void AttachColumns(DataGrid grid)
    {
        foreach (var column in grid.Columns)
        {
            if (column.Header is not string header) continue;
            var state = ColumnStates.GetOrCreateValue(column);
            state.Source ??= SourceFor(header);
            var translated = Translate(state.Source);
            if (!Equals(column.Header, translated)) column.Header = translated;
        }
    }

    private static IEnumerable<DependencyObject> Children(DependencyObject node)
    {
        if (node is FrameworkElement element)
        {
            IEnumerable logical;
            try { logical = LogicalTreeHelper.GetChildren(element); }
            catch (Exception exception) when (exception is InvalidOperationException or ArgumentException) { logical = Array.Empty<object>(); }
            foreach (var child in logical)
                if (child is DependencyObject dependencyObject) yield return dependencyObject;
        }

        if (node is ItemsControl items)
        {
            for (var i = 0; i < items.Items.Count; i++)
                if (items.ItemContainerGenerator.ContainerFromIndex(i) is DependencyObject child) yield return child;
        }

        var count = 0;
        try { count = VisualTreeHelper.GetChildrenCount(node); }
        catch (InvalidOperationException) { }
        for (var i = 0; i < count; i++)
            yield return VisualTreeHelper.GetChild(node, i);
    }

    private static Dictionary<string, string> CreateTranslations() => new(StringComparer.Ordinal)
    {
        ["风序 · FanOrder 设置"] = "FanOrder Settings",
        ["风序 · FanOrder"] = "FanOrder",
        ["本地服务已连接"] = "Connected",
        ["本地服务不可用"] = "Unavailable",
        ["当前适配 V12 Ultra；重新选择不会调速、重新配对或接管设备。"] = "V12 Ultra selected; changing this does not adjust speed, pair again or commission the device.",
        ["仅适配绿巨能 V12Ultra（原 V16）AI 智控款；选择不等于已连接。"] = "Only llano V12Ultra (formerly V16), AI control model; selection does not mean connected.",
        ["最后刷新"] = "Last refresh",
        ["启动后台"] = "Start backend",
        ["正在连接服务…"] = "Connecting to service…",
        ["刷新"] = "Refresh",
        ["查看详情"] = "View details",
        ["控制"] = "Control", ["监测"] = "Monitor", ["设置"] = "Settings",
        ["概览"] = "Overview", ["风扇"] = "Fan", ["灯光"] = "Lighting", ["方案"] = "Profiles",
        ["实时趋势"] = "Live trends", ["历史分析"] = "History analysis", ["常规"] = "General",
        ["快捷键"] = "Shortcuts", ["设备与维护"] = "Device & maintenance", ["关于"] = "About",
        ["仅适配绿巨能（llano）V12Ultra（原 V16）AI 智控款笔记本压风式散热器。"] = "Only supports the llano V12Ultra (formerly V16) AI-controlled laptop cooling pad.",
        ["京东商品编号：100211395916 · 国补备案型号：SV107。V11、V11 Pro、普通 V12 及其他型号不在适配范围内。"] = "JD item: 100211395916 · Subsidy registration model: SV107. V11, V11 Pro, standard V12 and other models are outside the supported range.",
        ["控制方式"] = "Control mode", ["风扇状态"] = "Fan status", ["设备"] = "Device",
        ["设备说明"] = "Device description", ["风扇设置"] = "Fan settings",
        ["快捷控制"] = "Quick controls", ["控制温度"] = "Control temperature", ["风扇目标"] = "Fan target",
        ["风扇控制量"] = "Fan control", ["风扇百分比"] = "Fan percent", ["目标"] = "Target",
        ["控制可用性"] = "Control availability", ["正常"] = "Normal", ["当前控制方式"] = "Current control mode",
        ["当前 %"] = "Current %", ["建议 %"] = "Suggested %", ["分钟样本"] = "Minute samples",
        ["风扇已开 · 关闭…"] = "Fan on · Turn off…", ["风扇已关 · 开启"] = "Fan off · Turn on",
        ["风扇未知 · 刷新"] = "Fan unknown · Refresh", ["灯光未知 · 刷新"] = "Lights unknown · Refresh",
        ["灯光已开 · 关闭"] = "Lights on · Turn off", ["灯光已关 · 开启"] = "Lights off · Turn on",
        ["状态未知或已过期；点击只刷新后台缓存，不切换电源。"] = "State unknown or stale. Click to refresh the backend cache without changing fan power.",
        ["状态未知或已过期；点击只刷新后台缓存，不切换灯光。"] = "State unknown or stale. Click to refresh the backend cache without changing lighting.",
        ["根据后台确认状态切换灯光；不提交灯光编辑页的草稿。"] = "Switch lighting from confirmed backend state without submitting editor drafts.",
        ["开启风扇"] = "Turn fan on", ["关闭风扇…"] = "Turn fan off…", ["手动转速"] = "Manual speed",
        ["只启动后台，不改自启；已接管时恢复保存策略。"] = "Start the backend only; keep startup unchanged and restore the saved policy when commissioned.",
        ["关闭风扇"] = "Turn fan off", ["开启灯光"] = "Turn lights on", ["关闭灯光"] = "Turn lights off",
        ["应用手动速度"] = "Apply manual speed", ["高级控制"] = "Advanced control",
        ["纯手动 / 停止温控"] = "Pure manual / stop automatic control", ["释放控制…"] = "Release control…",
        ["自动曲线编辑"] = "Auto curve editor", ["曲线草稿"] = "Curve draft", ["温控曲线"] = "Thermal curve",
        ["添加点"] = "Add point", ["删除选中点"] = "Delete selected point", ["恢复默认"] = "Restore defaults",
        ["读取已保存曲线"] = "Read saved curve", ["验证并应用"] = "Validate and apply",
        ["灯光设置"] = "Lighting settings", ["灯光预览"] = "Lighting preview", ["灯光模式"] = "Lighting mode",
        ["灯光亮度"] = "Lighting brightness", ["灯光变化速度"] = "Lighting speed", ["预设颜色"] = "Preset color",
        ["单色常亮"] = "Solid", ["单色呼吸"] = "Breathing", ["多彩 · 01"] = "Multicolor · 01",
        ["多彩 · 02"] = "Multicolor · 02", ["关灯"] = "Turn lights off", ["开灯"] = "Turn lights on",
        ["应用并验证"] = "Apply and verify", ["读取状态"] = "Read status",
        ["进入灯光页后读取状态"] = "Read status after opening Lighting",
        ["历史与曲线建议"] = "History and curve suggestions", ["分析本地历史"] = "Analyze local history",
        ["应用建议"] = "Apply suggestion", ["撤销本次"] = "Undo this change", ["导出结果"] = "Export results",
        ["最近 10 分钟"] = "Last 10 minutes",
        ["−10 分钟"] = "−10 minutes", ["−5 分钟"] = "−5 minutes", ["现在"] = "Now", ["等待样本"] = "Waiting for samples",
        ["尚未刷新"] = "Not refreshed", ["尚未读取"] = "Not read", ["预览"] = "Preview", ["回读"] = "Readback",
        ["界面配色"] = "Interface theme", ["界面风格"] = "Interface theme", ["经典深色"] = "Classic dark",
        ["界面语言"] = "Interface language", ["简体中文"] = "简体中文", ["托盘登录启动"] = "Tray startup",
        ["登录后启动到托盘"] = "Start in tray after sign-in", ["登录后启动到托盘并自动接管"] = "Start in tray and take control after sign-in", ["刷新登录启动状态"] = "Refresh tray startup status",
        ["后台服务自启动"] = "Backend service startup", ["开机时自动运行后台"] = "Start backend with Windows",
        ["正在确认系统状态…"] = "Checking system status…", ["刷新状态"] = "Refresh status", ["保存修改"] = "Save changes",
        ["窗口与托盘"] = "Window and tray", ["安全保护"] = "Safety protection", ["恢复安全保护"] = "Restore safety protection",
        ["禁用安全保护"] = "Disable safety protection", ["恢复保护…"] = "Restore protection…",
        ["设备与后台"] = "Device and backend", ["服务"] = "Service", ["接管"] = "Commissioning",
        ["最后读取"] = "Last read", ["最后写入"] = "Last write", ["导出诊断"] = "Export diagnostics",
        ["审计事务恢复屏障"] = "Audit transaction recovery barrier", ["安全操作恢复屏障"] = "Safety operation recovery barrier",
        ["配对、接管与回滚"] = "Pairing, commissioning and rollback", ["准备配对"] = "Prepare pairing",
        ["开始探测"] = "Start probe", ["探测身份"] = "Probe identity", ["等值写回"] = "Write-back equivalence",
        ["验证等值写回"] = "Verify write-back equivalence", ["分级测试"] = "Graduated test",
        ["执行分级测试"] = "Run graduated test", ["正式接管"] = "Formal commissioning",
        ["强制卸载后的有人看护恢复"] = "Attended recovery after forced uninstall", ["核验并恢复"] = "Verify and recover",
        ["回滚到 Myth.Cool"] = "Roll back to Myth.Cool", ["回滚"] = "Rollback", ["关机停扇"] = "Shutdown fan-off",
        ["有人看护的高风险资格测试"] = "Attended high-risk qualification test", ["1. 执行资格测试"] = "1. Run qualification test",
        ["2. 已观察恢复，确认启用"] = "2. Confirm enable after observing recovery",
        ["禁用并恢复 KeepAt100"] = "Disable and restore KeepAt100", ["版本信息"] = "Version information",
        ["关机并关闭风扇"] = "Shut down and turn off fan", ["仅在关机停扇已启用且设备身份匹配时可用。"] = "Available only when shutdown fan-off is enabled and the device identity matches.",
        ["制作者"] = "Creator", ["官网"] = "Website", ["打开官网 tianqistudio.com"] = "Open tianqistudio.com",
        ["实机验收边界"] = "Real-device acceptance boundary", ["已选中"] = "Selected",
        ["清除快捷键"] = "Clear shortcut", ["复制"] = "Duplicate", ["重命名"] = "Rename", ["删除…"] = "Delete…",
        ["更多设备将在后续版本适配"] = "More devices will be supported in a later release",
        ["打开风扇控制界面"] = "Open fan control", ["查看后台状态 / 启动后台"] = "View backend status / start backend",
        ["打开控制面板"] = "Open control panel", ["后台状态 / 启动"] = "Backend status / start",
        ["滚轮：每格 1%，范围 10–100%"] = "Wheel: 1% per step, range 10–100%",
        ["滚轮 ±1%（10–100%）"] = "Wheel ±1% (10–100%)",
        ["纯手动（停自动）"] = "Pure manual (stop auto)",
        ["退出并释放控制（仅实体按键）"] = "Exit and release control (physical buttons only)",
        ["退出并释放（实体按键）"] = "Exit and release (physical buttons)",
        ["就绪"] = "Ready", ["已连接"] = "Connected", ["未连接"] = "Disconnected", ["已开启"] = "On", ["已关闭"] = "Off",
        ["手动控制"] = "Manual control", ["自动曲线"] = "Auto curve", ["纯手动"] = "Pure manual",
        ["待确认"] = "Pending confirmation", ["转速目标未确认"] = "Speed target not confirmed",
        ["设备回读：未知"] = "Device readback: unknown", ["回读 —"] = "Readback —",
        ["按校准点估算 RPM，非实时读数。"] = "RPM is estimated from calibration points, not a live reading.",
        ["安全保护已禁用。"] = "Safety protection disabled.", ["安全保护已恢复，后台可按保存的控制模式运行。"] = "Safety protection restored. The backend can follow the saved control mode.",
        ["故障安全"] = "Fail-safe", ["故障安全已激活"] = "Fail-safe active", ["已激活"] = "Active", ["恢复阻断"] = "Recovery blocked",
        ["设备控制"] = "Device control", ["保存方案"] = "Save profile", ["从当前配置创建"] = "Create from current settings",
        ["选择或创建方案"] = "Select or create a profile", ["方案操作"] = "Profile actions", ["方案更多操作"] = "More profile actions",
        ["保存方案草稿"] = "Save profile draft", ["手动速度"] = "Manual speed", ["开启灯光 · 不改变风扇电源"] = "Turn lights on · fan power unchanged",
        ["关闭灯光 · 不改变风扇电源"] = "Turn lights off · fan power unchanged",
        ["蓝"] = "Blue", ["蓝色"] = "Blue", ["绿"] = "Green", ["绿色"] = "Green", ["橙"] = "Orange", ["橙色"] = "Orange",
        ["红"] = "Red", ["红色"] = "Red", ["紫"] = "Purple", ["紫色"] = "Purple", ["亮度"] = "Brightness",
        ["已保存方案"] = "Saved profiles", ["选择已保存方案"] = "Select a saved profile", ["应用方案"] = "Apply profile",
        ["管理…"] = "Manage…", ["尚未读取 · 仅本地草稿"] = "Not read · local draft only",
        ["草稿预览"] = "Draft preview", ["灯光草稿"] = "Lighting draft", ["变化速度 · 慢 → 快"] = "Change speed · slow → fast",
        ["温度 (°C)"] = "Temperature (°C)", ["温度 °C"] = "Temperature °C", ["温度分段的样本量、调整原因会显示在这里。"] = "Sample count and adjustment reason for the selected temperature range appear here.",
        ["尚未分析。历史不会追溯生成；手动模式、故障保护和旧曲线样本不会用于推导降速建议。"] = "Not analyzed. History is not generated retroactively; manual mode, fail-safe events and old curve samples are excluded from speed suggestions.",
        ["设备回读：未知 / RPM 未知"] = "Device readback: unknown / RPM unknown",
        ["信号健康"] = "Signal health", ["信号无效"] = "Signal invalid", ["最近采样"] = "Last sample",
        ["最近采样尝试"] = "Last sample attempt", ["状态未知"] = "Unknown status",
        ["自动曲线按曲线调节；是否正在运行以当前控制方式和安全保护状态为准。"] = "Automatic control follows the curve; its running state depends on the current mode and safety protection.",
        ["自动模式按曲线调节；是否正在运行以当前控制方式和安全保护状态为准。"] = "Automatic control follows the curve; its running state depends on the current mode and safety protection.",
        ["解除停止后才会自动调节；硬件故障需单独处理。"] = "Automatic control resumes after the stop is cleared; hardware faults need separate handling.",
        ["关扇仅限低温；升温或故障会自动开扇。退出窗口不影响后台。"] = "Fan off is limited to low temperature; heat or faults can turn it back on. Closing the window does not stop the backend.",
        ["自动调节按曲线运行；状态看当前模式和保护。"] = "Auto control follows the curve; status depends on the mode and protection.",
        ["仅示意颜色，不代表亮度或动画。\n设备只支持预设。"] = "Color preview only; it does not show brightness or animation.\nThe device supports presets only.",
        ["后台每分钟记录，保留 7 天；不联网分析。百分比不是实测 RPM。"] = "The backend records every minute for 7 days. Analysis is offline; percentage is not measured RPM.",
        ["建议仅供参考，不保证降温。应用会替换曲线并切到自动；90°C 保护不变。曲线被改过时不会覆盖。"] = "Suggestions are only a guide. Applying replaces the curve and switches to auto; 90°C protection stays. Changed curves are not overwritten.",
        ["未分析；手动和保护记录不用于建议。"] = "Not analyzed; manual and protected samples are excluded.",
        ["仅当前账户；勾选即保存。启动到托盘，不改后台或风扇。"] = "Current account only; changes save immediately. Starts in the tray without changing the backend or fan.",
        ["仅当前账户；勾选即保存。启动到托盘；已接管设备会自动恢复控制，手动和纯手动优先。"] = "Current account only; changes save immediately. Starts in the tray and restores control for commissioned devices; manual and pure-manual modes take priority.",
        ["控制后台是否随 Windows 启动；不改控制模式或关机选项。"] = "Choose whether the backend starts with Windows; control mode and shutdown options stay unchanged.",
        ["关闭窗口会收进托盘，快捷键仍有效；退出释放后失效。"] = "Closing sends the window to the tray; shortcuts stay active until control is released.",
        ["低负载接市电，退出 Myth.Cool 并留在设备旁。向导不会强退。"] = "Keep load low and use AC power, exit Myth.Cool, and stay by the device. The wizard will not force-close it.",
        ["验证 VID/PID、Usage、固件和报告长度。"] = "Verify VID/PID, Usage, firmware and report length.",
        ["写回原状态并复读，确认 RGB 不变。"] = "Write back the original state and reread it to confirm RGB is unchanged.",
        ["低于 60°C 依次测 50% → 70% → 100%；60–90°C 只测 100%。温度 ≥90°C 或失效即中止。"] = "Below 60°C, test 50% → 70% → 100%; at 60–90°C, test 100% only. Stop at ≥90°C or invalid temperature.",
        ["备份并停用 Myth.Cool 任务，服务独占写入。"] = "Back up and disable the Myth.Cool task; the service becomes the only writer.",
        ["只处理已有恢复日志；核验任务 XML 和状态，不执行任务命令。"] = "Handle existing recovery logs only; check task XML and status without running task commands.",
        ["尝试恢复 100%，停止自研控制并恢复原任务。"] = "Try to restore 100%, stop FanOrder control and restore the original task.",
        ["测试会短暂关扇，再恢复并回读。只生成 2 分钟内存资格，不启用关机关闭；确认恢复后再启用。需人在旁、低负载、低于 60°C，且 Myth.Cool 已退出。"] = "The test briefly stops the fan, then restores and reads it back. It creates a 2-minute in-memory qualification only; confirm recovery before enabling. Stay nearby, keep load low and temperature below 60°C, and exit Myth.Cool.",
        ["Restart 可能短暂关扇；快速启动、睡眠和休眠不保证。断电或驱动不可见时无法保证关机动作。"] = "Restart may briefly stop the fan; Fast Startup, sleep and hibernate are not guaranteed. Power loss or a missing driver cannot guarantee shutdown control.",
        ["此处不提供即时停扇、灯光或原始 HID。禁用只恢复 KeepAt100。"] = "This page does not provide instant fan-off, lighting or raw HID. Disable only restores KeepAt100.",
        ["我在旁看护、低负载、低于 60°C，且 Myth.Cool 已退出"] = "I am nearby, load is low, temperature is below 60°C, and Myth.Cool is closed",
        ["灯效、断电、睡眠/唤醒和 72 小时运行暂无实机记录；离线测试不等于验收。"] = "No real-device record exists for lighting, power loss, sleep/wake or 72-hour runs; offline tests are not acceptance.",
        ["应用手动转速，保持纯手动；必要时后台会重连。"] = "Apply the manual speed and keep pure manual mode; the backend may reconnect.",
        ["应用手动转速并进入手动；安全保护仍禁用，必要时后台会重连。"] = "Apply the manual speed and switch to manual; safety protection stays disabled.",
        ["应用转速并进入手动；保护设置不变，数值以回读为准。"] = "Apply the speed and switch to manual; protection stays unchanged and readback is authoritative.",
        ["当前后台不支持停控灯光，请更新后台；草稿仍可编辑。"] = "This backend does not support lighting while control is stopped; update it. The draft remains editable.",
        ["风扇已关闭；灯光操作不会开扇。"] = "The fan is off; lighting actions will not turn it on.",
        ["后台不支持灯光控制。"] = "This backend does not support lighting control.",
        ["温控保护中，暂不能调灯。"] = "Thermal protection is active; lighting cannot be changed yet.",
        ["正在读取后台曲线；可继续编辑。"] = "Reading the backend curve; you can keep editing.",
        ["读取后台曲线；未保存修改会先询问。"] = "Read the backend curve; unsaved changes will prompt first.",
        ["应用手动转速后会切到手动；保护设置不变。"] = "Applying a manual speed switches to manual; protection stays unchanged.",
        ["显示后台确认的控制方式；滑块和输入只改草稿，应用后提交。"] = "Shows the backend-confirmed mode; sliders and input edit a draft until applied.",
        ["自动调节已停止；保存曲线不会启用保护或输出100%。"] = "Automatic control is stopped; saving the curve will not enable protection or output 100%.",
        ["自动模式按曲线运行；90°C保护会请求100%。保存曲线不切换模式。"] = "Auto mode follows the curve; 90°C protection requests 100%. Saving does not switch mode.",
        ["停止软件控制，保留当前风扇状态；选择纯手动可重新控制。"] = "Stop software control and keep the current fan state; choose pure manual to control again.",
        ["输入10–100%；Enter应用，Esc恢复目标。纯手动保留原模式。"] = "Enter 10–100%; press Enter to apply or Esc to restore the target. Pure manual keeps its mode.",
        ["显示本窗口最近 10 分钟；缺失或过期留空。"] = "Shows the last 10 minutes while this window is open; missing or stale values stay blank.",
        ["部分数据不可用；空白表示缺失或过期。"] = "Some data is unavailable; blank areas mean missing or stale values.",
        ["进入页面自动读取缓存。"] = "Opening the page reads the cache automatically.",
        ["多彩模式不使用单色预设；切回时保留。"] = "Multicolor mode does not use a single-color preset; it is kept for when you switch back.",
        ["等待灯光操作确认。"] = "Waiting for the lighting action to be confirmed.",
        ["等待兼容后台；刷新不改变设备。"] = "Waiting for a compatible backend; refresh does not change the device.",
        ["只读后台缓存；保留未应用草稿。"] = "Read the backend cache only; keep the unapplied draft.",
        ["正在刷新缓存；可继续编辑。"] = "Refreshing the cache; you can keep editing.",
        ["应用草稿并由后台核验。"] = "Apply the draft and let the backend verify it.",
        ["请先读取后台缓存；失败可重试。"] = "Read the backend cache first; retry if it fails.",
        ["尚无已确认值；草稿已保留。"] = "No confirmed value yet; the draft is kept.",
        ["没有可撤销的修改。"] = "There are no changes to undo.",
        ["恢复最近确认值，不改变设备。"] = "Restore the latest confirmed value without changing the device.",
        ["正在应用灯光，等待确认…"] = "Applying lighting; waiting for confirmation…",
        ["正在读取缓存；可继续编辑…"] = "Reading the cache; you can keep editing…",
        ["最近回读："] = "Last readback: ",
        ["回读时间未知；预览不代表当前设备。"] = "Readback time is unknown; the preview is not the current device state.",
        ["修改先保存在草稿；点击应用才发送。"] = "Changes stay in the draft until you click Apply.",
        ["读取当前配置，创建本地草稿；保存和应用分开。"] = "Read the current configuration and create a local draft; save and apply are separate.",
        ["方案保存在本机。保存风扇模式、手动速度和曲线，可选灯光；不保存停扇、纯手动、释放、接管或关机策略。"] = "Profiles stay on this PC. They save fan mode, manual speed and curve, with optional lighting; stop, pure manual, release, commissioning and shutdown policies are not saved.",
        ["曲线 2–20 点，温度递增，速度不降；保护状态看概览。手动方案不解除保护。"] = "Curves use 2–20 points with rising temperature and non-decreasing speed; check protection on Overview. Manual profiles do not clear protection.",
        ["灯光只用设备预设；取消勾选则不改灯光。"] = "Lighting uses device presets; clear the box to leave lighting unchanged.",
        ["只保存草稿，不控制设备。"] = "Save the draft only; do not control the device.",
        ["应用已保存版本，保留未保存草稿；后台负责验证。"] = "Apply the saved version and keep unsaved edits; the backend verifies it.",
        ["方案文件读取失败，原文件已保留。"] = "The profile file could not be read; the original file was kept.",
        ["连接后台读取配置；本地方案仍可编辑。"] = "Connect to the backend to read settings; local profiles remain editable.",
        ["读取后台配置，创建本地草稿；不控制设备。"] = "Read backend settings and create a local draft; the device is not controlled.",
        ["应用已保存的手动转速和灯光；自动调节仍暂停。"] = "Apply the saved manual speed and lighting; automatic control stays paused.",
        ["隐藏到托盘后可唤回；仅支持全局快捷键。"] = "After hiding to the tray, restore it with the global shortcut.",
        ["应用已保存方案；未保存修改不会应用。"] = "Apply the saved profile; unsaved edits are not applied.",
        ["按托盘滚轮调速，范围 10–100%；需后台和设备可用。"] = "Adjust speed like the tray wheel, from 10–100%; the backend and device must be available.",
        ["同界面开扇，结果以设备确认。"] = "Turn the fan on like the main window; use the device confirmation.",
        ["沿用当前灯光预设，不提交 RGB 草稿，不改风扇电源。"] = "Use the current lighting preset; do not submit the RGB draft or change fan power.",
        ["保存方案草稿，不向设备发送指令。"] = "Save the profile draft without sending a device command.",
        ["点击录入；Esc 取消，Delete 清除。全局快捷键在窗口隐藏时也可用。录入时暂停其他快捷键。"] = "Click to record; Esc cancels and Delete clears. Global shortcuts work while hidden; other shortcuts pause during recording.",
        ["最近确认："] = "Last confirmed: ",
        ["尚未确认；未勾选不代表关闭。"] = "Not confirmed; unchecked does not mean disabled.",
        ["重新确认设置；保留未保存选择。"] = "Confirm the setting again; keep unsaved choices.",
        ["等待后台连接确认；未保存选择会保留。"] = "Wait for backend connection; unsaved choices are kept.",
        ["上次未确认；可重试保存或刷新确认。"] = "The last result was unconfirmed; retry Save or refresh to confirm.",
        ["等待自动确认；失败可重试刷新。"] = "Wait for automatic confirmation; retry refresh if it fails.",
        ["只保存开机启动，不改风扇或安全保护。"] = "Save startup only; fan and safety protection stay unchanged.",
        ["选择随 Windows 开机运行后台，改后点击保存。"] = "Choose whether the backend runs with Windows, then click Save.",
        ["没有完整灯光缓存；请先在灯光页应用完整设置。"] = "No complete lighting cache; apply the full setup on the Lighting page first.",
        ["没有灯光预设；请先在灯光页应用完整设置。"] = "No lighting preset; apply the full setup on the Lighting page first.",
        ["自动调节已暂停；手动转速和开关可用。后台会核验设备。"] = "Automatic control is paused; manual speed and power are available. The backend verifies the device.",
        ["只读后台缓存，修改灯光开关；请先完成灯光设置。"] = "Read the backend cache and change the lighting switch; finish lighting setup first.",
        ["按保存目标开扇；必要时后台重连。"] = "Turn on the fan at the saved target; the backend may reconnect.",
        ["开扇，后台确认恢复散热。"] = "Turn on the fan; the backend confirms cooling is restored.",
        ["只保存自动模式；自动调节仍暂停，恢复保护需单独操作。"] = "Save auto mode only; automatic control stays paused and protection must be restored separately.",
        ["保存手动模式；保护仍禁用，也可直接应用转速。"] = "Save manual mode; protection stays disabled, or apply a speed directly.",
        ["选择普通手动；保留温控保护，应用转速也会进入手动。"] = "Choose normal manual mode; thermal protection stays on and applying a speed also switches to manual.",
        ["Windows 禁用；请在启动应用中启用 FanOrder。"] = "Windows disabled it; enable FanOrder in Startup apps.",
        ["纯手动：关闭后不会自动开扇。窗口收进托盘；退出会释放控制。"] = "Pure manual: the fan will not restart automatically after off. The window moves to the tray; exit releases control.",
        ["普通模式：关闭仅限低温；升温或监控故障会自动开启。窗口收进托盘。"] = "Normal mode: fan off is limited to low temperature; heat or monitor faults turn it back on. The window moves to the tray.",
        ["已选绿巨能 V12Ultra（原 V16）AI 智控款；切换不会调速或接管。"] = "llano V12Ultra (formerly V16), AI control model selected; switching does not adjust speed or commission the device.",
        ["托盘滚轮不可用；请用界面滑块。"] = "The tray wheel is unavailable; use the window slider.",
        ["后台每分钟记 7 天；离线分析，百分比不是实测 RPM。"] = "The backend records every minute for 7 days; analysis is offline and percentage is not measured RPM.",
        ["建议仅供参考。应用会替换曲线并切自动；90°C保护不变，改过的曲线不覆盖。"] = "Suggestions are only a guide. Applying replaces the curve and switches to auto; 90°C protection stays, and changed curves are not overwritten.",
        ["控制后台是否随 Windows 启动；不改模式或关机设置。"] = "Choose whether the backend starts with Windows; mode and shutdown settings stay unchanged.",
        ["未转动时起始转速"] = "Startup speed when fan is stopped",
        ["启动后检测到风扇未转动时使用；默认 50%，范围 10–100%。"] = "Used when startup detects a stopped fan; default 50%, range 10–100%.",
        ["保存起始转速"] = "Save startup speed",
        ["输入 10–100%；Enter 保存，Esc 恢复已确认值。"] = "Enter 10–100%; press Enter to save or Esc to restore the confirmed value.",
        ["仅保存未转动时的启动起始转速。"] = "Save only the startup speed used when the fan is stopped.",
        ["启动起始转速 {0}% 已保存。仅在后台启动且风扇未转动时使用。"] = "Startup speed {0}% saved. Used only when the backend starts and the fan is stopped.",
        ["低负载接市电，退出 Myth.Cool 并留在旁边；向导不强退。"] = "Keep load low and use AC power, exit Myth.Cool, and stay nearby; the wizard will not force-close it.",
        ["只处理已有恢复日志；核验任务 XML 和状态，不运行命令。"] = "Handle existing recovery logs only; check task XML and status without running commands.",
        ["短暂关扇后恢复并回读；仅生成 2 分钟内存资格，不启用关机关闭。需人在旁、低负载、低于 60°C，且 Myth.Cool 已退出。"] = "The fan briefly stops, then restores and reads back; this creates a 2-minute in-memory qualification only and does not enable shutdown fan-off. Stay nearby, keep load low and temperature below 60°C, and exit Myth.Cool.",
        ["Restart 可能短暂关扇；快速启动、睡眠/休眠不保证。断电或驱动不可见时无法保证。"] = "Restart may briefly stop the fan; Fast Startup and sleep/hibernate are not guaranteed. Power loss or a missing driver cannot guarantee control.",
        ["不提供即时停扇、灯光或原始 HID；禁用只恢复 KeepAt100。"] = "Instant fan-off, lighting and raw HID are not provided; disable only restores KeepAt100.",
        ["低于60°C测 50% → 70% → 100%；60–90°C仅测100%；≥90°C或失效即停。"] = "Below 60°C test 50% → 70% → 100%; at 60–90°C test 100% only; stop at ≥90°C or invalid data.",
        ["灯效、断电、睡眠/唤醒、72小时运行暂无实机记录；离线测试不等于验收。"] = "No real-device record exists for lighting, power loss, sleep/wake or 72-hour runs; offline tests are not acceptance.",
        ["关闭风扇时同时熄灯；再次开启可恢复关闭前的灯光。设备按键和手动灯光的新设置优先。仅本次后台运行且设备未断开时保留恢复记录。"] = "Turning off the fan also turns off the lights; turning it on can restore the previous lighting. New physical-button and manual-lighting settings take priority. Restoration is kept only for this backend session while the device stays connected.",
        ["安全保护已禁用；设置不会自动恢复。手动转速仍可调，恢复保护需单独确认。"] = "Safety protection is disabled; settings do not restore it automatically. Manual speed remains adjustable; restoring protection needs separate confirmation.",
        ["纯手动已保存：无温控、满速保护或定时重申；重启不自动写入。"] = "Pure manual is saved: no thermal control, full-speed protection or timed refresh; restart will not write automatically.",
        ["软件已释放控制；可用实体按键。选择纯手动后可重新调速。"] = "Software control is released; use the physical buttons. Choose pure manual to adjust speed again.",
        ["普通手动受温控保护；选择纯手动会停自动调节。"] = "Normal manual mode keeps thermal protection; pure manual stops automatic control.",
        ["自动保护输出已停止；点击恢复保护才会重新启用。"] = "Automatic protection output is stopped; click Restore protection to enable it again.",
        ["禁用保护并恢复保存的手动转速（"] = "Disable protection and restore the saved manual speed (",
        ["禁用温控、满速保护和自动重申；无已确认目标时不发送调速。"] = "Disable thermal control, full-speed protection and automatic refresh; no speed command is sent without a confirmed target.",
        ["自动保护输出已停止；手动转速以设备回读为准，仍可主动设速。"] = "Automatic protection output is stopped; device readback is authoritative for manual speed, which you can still set.",
        ["正在分析；其他页面仍可用。"] = "Analyzing; other pages remain available.",
        ["先连接兼容后台。"] = "Connect to a compatible backend first.",
        ["分析本地历史；不改风扇设置。"] = "Analyze local history; fan settings stay unchanged.",
        ["历史存储有问题；处理后重试分析。"] = "History storage has a problem; fix it and analyze again.",
        ["建议已过期；请重新分析。"] = "The suggestion expired; analyze again.",
        ["建议与当前曲线相同，无需应用。"] = "The suggestion matches the current curve; nothing to apply.",
        ["保存建议曲线和自动模式；自动调节仍暂停。"] = "Save the suggested curve and auto mode; automatic control stays paused.",
        ["保存建议曲线并切到自动模式。"] = "Save the suggested curve and switch to auto mode.",
        ["导出当前分析结果，不改设备设置。"] = "Export the current analysis without changing device settings.",
        ["分析于 "] = "Analyzed at ",
    };
}
