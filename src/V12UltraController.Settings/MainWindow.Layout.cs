using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;

namespace V12UltraController.Settings;

public partial class MainWindow
{
    private static void OpenActionMenu(Button owner)
    {
        if (owner.ContextMenu is not { } menu) return;
        menu.PlacementTarget = owner;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void InitializeResponsiveLayout()
    {
        SizeChanged += (_, _) => UpdateResponsiveLayout();
        WindowLayoutGrid.SizeChanged += (_, _) => UpdateResponsiveLayout();
        UpdateResponsiveLayout();
    }

    private void UpdateResponsiveLayout()
    {
        var compact = (WindowLayoutGrid.ActualWidth > 0 ? WindowLayoutGrid.ActualWidth : ActualWidth > 0 ? ActualWidth : Width) < 1080;
        NavigationColumn.Width = new GridLength(compact ? 152 : 184);
        PageContentGrid.Margin = compact ? new Thickness(16, 12, 16, 8) : new Thickness(24, 18, 24, 12);
        PageTitleText.FontSize = compact ? 23 : 26;
        PageTitleText.Margin = new Thickness(0, 0, 0, compact ? 8 : 10);
        SecondaryNavigationPanel.Margin = new Thickness(0, 0, 0, compact ? 10 : 14);
        OverviewMetricsGrid.Columns = 3;
    }
}

public sealed class BrightnessPercentConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is double brightness ? $"{Math.Round(brightness / 255 * 100):0}%" : "—";
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
