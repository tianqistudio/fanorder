using System.IO;
using System.Windows.Controls;
using V12UltraController.Settings;

internal static class TrayStartupChecks
{
    internal static void Run(Action<bool, string> assert, MainWindow window)
    {
        var preference = new FakePreference();
        window.InitializeTrayStartup(preference);
        var check = (CheckBox)window.FindName("TrayStartupCheckBox");
        var text = (TextBlock)window.FindName("TrayStartupText");
        assert(check.IsEnabled && check.IsChecked == false && preference.Writes == 0,
            "tray startup reads local state without writing on initialization");
        check.IsChecked = true;
        assert(preference.Enabled && preference.Writes == 1 && text.Text.Contains("已启用"),
            "tray startup immediately saves and confirms enable without service access");
        check.IsChecked = false;
        assert(!preference.Enabled && preference.Writes == 2, "tray startup can be independently disabled");
        preference.FailWrite = true;
        check.IsChecked = true;
        assert(check.IsChecked == false && text.Text.Contains("保存失败"),
            "failed startup write restores confirmed checkbox and reports failure");
        preference.FailWrite = false;
        preference.Blocked = true;
        check.IsChecked = true;
        assert(text.Text.Contains("Windows 禁用"), "Windows-blocked startup never reports effective enabled");
        assert(TrayStartupPreference.Command(@"C:\Program Files\风序\settings.exe") ==
            "\"C:\\Program Files\\风序\\settings.exe\" --tray", "startup quotes spaced Unicode executable and includes tray flag");
    }

    private sealed class FakePreference : ITrayStartupPreference
    {
        public bool Enabled, Blocked, FailWrite;
        public int Writes;
        public bool ReadEnabled() => Enabled;
        public bool ReadBlocked() => Blocked;
        public void SetEnabled(bool enabled)
        {
            if (FailWrite) throw new IOException("synthetic write denied");
            Writes++;
            Enabled = enabled;
        }
    }
}
