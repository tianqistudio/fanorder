using System.Reflection;
using System.Windows.Controls;
using System.Windows.Data;
using V12UltraController.Core.Configuration;
using V12UltraController.Core.Control;
using V12UltraController.Core.Ipc;
using V12UltraController.Settings;

internal static class LocalizationChecks
{
    internal static void Run(Action<bool, string> assert)
    {
        var client = new NoDeviceClient();
        var profiles = new MemoryControlProfileStore([
            new("localization-manual", "保存", ControlMode.Manual, 59, FanCurve.RemoteHostSafe, null),
        ]);
        var language = new MemoryLanguagePreference();
        var window = new MainWindow(client, profileStore: profiles, languagePreference: language);
        var languageCombo = (ComboBox)window.FindName("LanguageCombo");
        var slider = (Slider)typeof(MainWindow).GetField("_profileManual", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(window)!;
        var label = ((Panel)slider.Parent).Children.OfType<TextBlock>().Single();
        var binding = BindingOperations.GetBindingExpression(label, TextBlock.TextProperty);
        assert(binding is not null && label.Text == "手动速度 · 59%",
            "profile speed starts with a live slider binding in Simplified Chinese");

        try
        {
            for (var cycle = 0; cycle < 2; cycle++)
            {
                languageCombo.SelectedIndex = 1;
                assert(ReferenceEquals(binding, BindingOperations.GetBindingExpression(label, TextBlock.TextProperty)) &&
                    label.Text.StartsWith("Manual speed · ", StringComparison.Ordinal),
                    $"English localization preserves the profile speed binding in cycle {cycle + 1}");

                var englishPercent = 62 + cycle;
                slider.Value = englishPercent;
                assert(label.Text == $"Manual speed · {englishPercent}%",
                    $"profile speed follows slider changes in English in cycle {cycle + 1}");

                languageCombo.SelectedIndex = 0;
                assert(ReferenceEquals(binding, BindingOperations.GetBindingExpression(label, TextBlock.TextProperty)) &&
                    label.Text == $"手动速度 · {englishPercent}%",
                    $"returning to Chinese restores current source text and keeps the binding in cycle {cycle + 1}");

                var chinesePercent = 67 + cycle;
                slider.Value = chinesePercent;
                assert(label.Text == $"手动速度 · {chinesePercent}%",
                    $"profile speed continues following slider changes in Chinese in cycle {cycle + 1}");
            }

            var host = new ContentControl();
            Localization.Attach(host);
            var content = new Border();
            host.Content = content;
            Localization.SetLanguage(UiLanguage.English);
            Localization.Attach(host);
            assert(ReferenceEquals(host.Content, content), "localization preserves a visual assigned after an initially empty content property");
            Localization.Attach(window);
            var title = (TextBlock)typeof(MainWindow).GetField("_profileTitle", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            assert(title.Text == "保存", "user profile names are not translated even when identical to a UI label");
            assert(Localization.Translate("手动目标 67% 已确认。") == "Manual target 67% confirmed.", "dynamic speed status translates without changing its value");
            assert(Localization.Translate("“保存”已应用并完成验证。") == "“保存” applied and verified.", "profile name inside complete status template stays verbatim");
            assert(Localization.Translate("语言已切换，但无法保存语言偏好；下次可能恢复默认。") == "Language changed, but the preference could not be saved; the default may return next time.", "language preference failure is localized");
            assert(Localization.Translate("安全保护已禁用：后台不会因温度或故障自动开扇。明确恢复保护后才会恢复自动控制。").StartsWith("Safety protection disabled:", StringComparison.Ordinal), "safety state has an English explanation");
            typeof(MainWindow).GetField("shortcutSearch", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, "Fan target");
            typeof(MainWindow).GetMethod("ApplyShortcutFilter", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
            var rows = (System.Collections.Generic.Dictionary<string, System.Windows.FrameworkElement>)typeof(MainWindow)
                .GetField("shortcutRowViews", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            assert(rows["fan.increase"].Visibility == System.Windows.Visibility.Visible
                && rows["window.toggle"].Visibility == System.Windows.Visibility.Collapsed,
                "shortcut search accepts visible English action labels");
            typeof(MainWindow).GetField("shortcutSearch", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, "");
            Localization.SetLanguage(UiLanguage.SimplifiedChinese);
            Localization.Attach(window);
            assert(profiles.Load().Single().ManualSpeedPercent == 59 && client.Requests == 0,
                "localization and slider draft checks neither save a profile nor send IPC");
            assert(language.Saves == 4 && language.Load() == UiLanguage.SimplifiedChinese,
                "repeated language changes use only the injected in-memory preference");
        }
        finally
        {
            // Localization is shared by the detached checks in this process.
            Localization.SetLanguage(UiLanguage.SimplifiedChinese);
        }
    }

    private sealed class MemoryLanguagePreference : ILanguagePreference
    {
        private UiLanguage language;
        internal int Saves { get; private set; }
        public UiLanguage Load() => language;
        public void Save(UiLanguage value) { language = value; Saves++; }
    }

    private sealed class NoDeviceClient : IPipeApiClient
    {
        internal int Requests { get; private set; }
        public Task<PipeResponse> SendAsync(PipeRequest request, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Requests++;
            throw new InvalidOperationException("Localization-only check attempted IPC: " + request.Operation);
        }
    }
}
