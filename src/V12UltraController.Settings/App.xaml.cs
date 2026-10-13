using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace V12UltraController.Settings;

public partial class App : Application
{
    private Popup? _helpPopup;
    private Button? _helpButton;
    private Window? _helpWindow;

    private void InfoButton_ToolTipOpening(object sender, ToolTipEventArgs e)
    {
        if (_helpPopup?.IsOpen == true && ReferenceEquals(sender, _helpButton)) e.Handled = true;
    }

    private void InfoButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { ToolTip: { } content } button) return;
        e.Handled = true;
        if (ReferenceEquals(button, _helpButton) && _helpPopup?.IsOpen == true)
        {
            CloseHelpPopup();
            return;
        }
        CloseHelpPopup();
        if (Window.GetWindow(button) is not { } window || !button.IsLoaded) return;

        // Hover remains entirely owned by WPF's ToolTipService. In particular,
        // never replace Button.ToolTip, reparent its content, or change StaysOpen
        // while PopupControlService may be showing or closing that tooltip.
        var view = CreateHelpView(content, button.DataContext);
        var surface = new Border
        {
            Child = new ScrollViewer
            {
                Content = view, MaxHeight = 480,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            },
            MaxWidth = 460, Padding = new Thickness(14), CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1), Focusable = true,
        };
        surface.SetResourceReference(Border.BackgroundProperty, "SurfaceRaisedBrush");
        surface.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        var popup = new Popup
        {
            Child = surface, PlacementTarget = button, Placement = PlacementMode.Bottom,
            VerticalOffset = 4, AllowsTransparency = true, StaysOpen = false,
        };
        _helpPopup = popup;
        _helpButton = button;
        _helpWindow = window;

        void CloseThisPopup() { if (popup.IsOpen) popup.IsOpen = false; }
        void OwnerUnloaded(object ignored, RoutedEventArgs args) => CloseThisPopup();
        void WindowChanged(object? ignored, EventArgs args) => CloseThisPopup();
        void WindowResized(object ignored, SizeChangedEventArgs args) => CloseThisPopup();
        void EscapePressed(object ignored, KeyEventArgs args)
        {
            if (args.Key != Key.Escape) return;
            CloseThisPopup();
            args.Handled = true;
        }
        button.Unloaded += OwnerUnloaded;
        window.Deactivated += WindowChanged;
        window.LocationChanged += WindowChanged;
        window.SizeChanged += WindowResized;
        window.Closed += WindowChanged;
        window.PreviewKeyDown += EscapePressed;
        surface.PreviewKeyDown += EscapePressed;
        popup.Closed += (_, _) =>
        {
            button.Unloaded -= OwnerUnloaded;
            window.Deactivated -= WindowChanged;
            window.LocationChanged -= WindowChanged;
            window.SizeChanged -= WindowResized;
            window.Closed -= WindowChanged;
            window.PreviewKeyDown -= EscapePressed;
            surface.PreviewKeyDown -= EscapePressed;
            // Closed can arrive after another button has already opened a popup.
            if (ReferenceEquals(_helpPopup, popup))
            {
                _helpPopup = null;
                _helpButton = null;
                _helpWindow = null;
            }
            popup.Child = null;
        };
        popup.IsOpen = true;
    }

    private void CloseHelpPopup()
    {
        var popup = _helpPopup;
        _helpPopup = null;
        _helpButton = null;
        _helpWindow = null;
        if (popup is not null) popup.IsOpen = false;
    }

    private static FrameworkElement CreateHelpView(object content, object? dataContext)
    {
        if (content is ToolTip tip) return CreateHelpView(tip.Content ?? string.Empty, dataContext);
        if (content is StackPanel sourcePanel)
        {
            var panel = new StackPanel { DataContext = dataContext };
            foreach (var child in sourcePanel.Children.OfType<FrameworkElement>())
                panel.Children.Add(CreateHelpView(child, dataContext));
            return panel;
        }
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 420, DataContext = dataContext };
        if (content is TextBlock source)
        {
            text.Margin = source.Margin;
            text.SetBinding(TextBlock.ForegroundProperty, new Binding(nameof(TextBlock.Foreground)) { Source = source });
            text.SetBinding(TextBlock.FontWeightProperty, new Binding(nameof(TextBlock.FontWeight)) { Source = source });
            text.SetBinding(TextBlock.FontSizeProperty, new Binding(nameof(TextBlock.FontSize)) { Source = source });
            // Row-bound tooltip content may not have inherited its DataContext yet
            // when clicked before the first hover. Resolve that binding in the new
            // view; application-updated named TextBlocks stay live via their Text.
            var binding = BindingOperations.GetBinding(source, TextBlock.TextProperty);
            if (binding is { Source: null, RelativeSource: null } && string.IsNullOrEmpty(binding.ElementName))
                text.SetBinding(TextBlock.TextProperty, binding);
            else
                text.SetBinding(TextBlock.TextProperty, new Binding(nameof(TextBlock.Text)) { Source = source });
        }
        else text.Text = content.ToString() ?? string.Empty;
        return text;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // Synthetic test hosts load these resources but never own production UI.
        if (System.Reflection.Assembly.GetEntryAssembly() != typeof(App).Assembly) return;
        var trayLaunch = e.Args.Contains("--tray", StringComparer.OrdinalIgnoreCase);
        if (!EnterSingleInstance(trayLaunch))
        {
            Shutdown();
            return;
        }
        DispatcherUnhandledException += (_, args) =>
        {
            if (args.Handled) return;
            LocalizedMessageBox.Show(
                $"设置程序遇到未处理错误：\n\n{args.Exception.Message}",
                "风序 · FanOrder",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            args.Handled = true;
        };
        var window = new MainWindow();
        MainWindow = window;
        if (trayLaunch) window.StartInTray();
        else window.Show();
    }
}
