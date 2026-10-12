using System.ComponentModel;
using System.Diagnostics;
using System.Windows;

namespace V12UltraController.Settings;

public partial class MainWindow
{
    private void OpenOfficialWebsite_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            using var browser = Process.Start(new ProcessStartInfo("https://tianqistudio.com")
            {
                UseShellExecute = true,
            });
            ActivityText.Text = "已请求打开官网。";
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            ActivityText.Text = $"无法打开官网：{exception.Message}";
        }
    }
}
