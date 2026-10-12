using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using V12UltraController.Core.Hardware;

namespace V12UltraController.Settings;

public partial class MainWindow
{
    internal ContextMenu BuildDeviceMenu()
    {
        var menu = new ContextMenu();
        foreach (var profile in DeviceProfileCatalog.Supported)
        {
            var item = new MenuItem
            {
                Header = profile.DisplayName + "（当前设备型号）",
                IsCheckable = true, IsChecked = profile.Id == DeviceProfileCatalog.V12Ultra.Id,
                IsEnabled = DeviceProfileCatalog.CanSelect(profile.Id),
            };
            item.Click += (_, _) =>
            {
                item.IsChecked = true;
                ActivityText.Text = "已选 V12 Ultra；切换不会调速或接管。";
            };
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "更多设备将在后续版本适配", IsEnabled = false });
        Localization.Attach(menu);
        return menu;
    }

    private void DeviceSwitchButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = BuildDeviceMenu();
        menu.PlacementTarget = DeviceSwitchButton;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }
}
