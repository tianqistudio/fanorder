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
            new("localization-manual", "本地化回归方案", ControlMode.Manual, 59, FanCurve.RemoteHostSafe, null),
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
