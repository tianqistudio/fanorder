using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace V12UltraController.Settings;

public partial class MainWindow
{
    private bool languageReady;

    private void Language_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!languageReady || LanguageCombo is null) return;
        var language = LanguageCombo.SelectedIndex == 1 ? UiLanguage.English : UiLanguage.SimplifiedChinese;
        Localization.SetLanguage(language);
        Title = Localization.Translate("风序 · FanOrder 设置");
        try
        {
            _languagePreference?.Save(language);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ActivityText.Text = "语言已切换，但无法保存语言偏好；下次可能恢复默认。";
        }

        Localization.Attach(this);
        if (navigationReady)
        {
            var page = NavigationPages.SingleOrDefault(item => item.Id == CurrentPageId);
            if (page is not null) UpdateNavigation(page);
        }
    }
}
