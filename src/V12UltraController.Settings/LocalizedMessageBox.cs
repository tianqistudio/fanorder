using System.Windows;
namespace V12UltraController.Settings;
internal static class LocalizedMessageBox
{
    internal static MessageBoxResult Show(string message, string title, MessageBoxButton buttons = MessageBoxButton.OK,
        MessageBoxImage image = MessageBoxImage.None, MessageBoxResult result = MessageBoxResult.None) =>
        MessageBox.Show(Localization.Translate(message), Localization.Translate(title), buttons, image, result);
    internal static MessageBoxResult Show(Window owner, string message, string title, MessageBoxButton buttons = MessageBoxButton.OK,
        MessageBoxImage image = MessageBoxImage.None, MessageBoxResult result = MessageBoxResult.None) =>
        MessageBox.Show(owner, Localization.Translate(message), Localization.Translate(title), buttons, image, result);
}
