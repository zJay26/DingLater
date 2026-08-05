using System.Runtime.InteropServices;
using H.NotifyIcon;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace DingLater.App.Views;

public sealed partial class MainWindow : Window
{
    private const double DefaultWidth = 1180;
    private const double DefaultHeight = 740;
    private const double MinimumWidth = 640;
    private const double MinimumHeight = 520;
    private bool _allowClose;
    private bool _enforcingMinimum;
    private bool _sized;

    public MainWindow()
    {
        InitializeComponent();
        Title = "DingLater";
        AppWindow.Title = "DingLater";
        AppWindow.Closing += AppWindow_Closing;
        AppWindow.Changed += AppWindow_Changed;
        RootGrid.Loaded += RootGrid_Loaded;
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = true;
            presenter.IsMaximizable = true;
            presenter.IsMinimizable = true;
        }

        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "DingLater.ico");
        if (File.Exists(iconPath))
        {
            AppWindow.SetIcon(iconPath);
        }

        try
        {
            if (MicaController.IsSupported())
            {
                SystemBackdrop = new MicaBackdrop();
                RootGrid.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            }
        }
        catch
        {
            SystemBackdrop = null;
        }
    }

    internal event EventHandler? HideRequested;

    internal XamlRoot DialogXamlRoot => RootGrid.XamlRoot;

    internal void SetContent(UIElement content) => ContentHost.Content = content;

    internal void Dispatch(Action action)
    {
        if (DispatcherQueue.HasThreadAccess)
        {
            action();
        }
        else
        {
            DispatcherQueue.TryEnqueue(() => action());
        }
    }

    internal void ShowFromBackground()
    {
        this.Show(disableEfficiencyMode: true);
        Activate();
    }

    internal void HideToTray() => this.Hide(enableEfficiencyMode: true);

    internal void ForceClose()
    {
        _allowClose = true;
        Close();
    }

    internal async Task ShowErrorAsync(string title, string message)
    {
        if (RootGrid.XamlRoot is null)
        {
            ContentHost.Content = new InfoBar
            {
                IsOpen = true,
                IsClosable = false,
                Severity = InfoBarSeverity.Error,
                Title = title,
                Message = message,
                Margin = new Thickness(24)
            };
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = title,
            Content = new TextBlock
            {
                Text = message,
                FontFamily = new FontFamily("Segoe UI Variable Text"),
                FontSize = (double)Application.Current.Resources["DingBodyFontSize"],
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 520
            },
            CloseButtonText = "关闭",
            DefaultButton = ContentDialogButton.Close
        };
        await dialog.ShowAsync();
    }

    private void RootGrid_Loaded(object sender, RoutedEventArgs e)
    {
        if (_sized)
        {
            return;
        }

        _sized = true;
        var scale = GetScale();
        var displayArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        var workArea = displayArea.WorkArea;
        var width = Math.Min((int)Math.Round(DefaultWidth * scale), workArea.Width);
        var height = Math.Min((int)Math.Round(DefaultHeight * scale), workArea.Height);
        width = Math.Max(width, Math.Min((int)Math.Ceiling(MinimumWidth * scale), workArea.Width));
        height = Math.Max(height, Math.Min((int)Math.Ceiling(MinimumHeight * scale), workArea.Height));
        var x = workArea.X + Math.Max(0, (workArea.Width - width) / 2);
        var y = workArea.Y + Math.Max(0, (workArea.Height - height) / 2);
        AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
    }

    private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowClose)
        {
            return;
        }

        args.Cancel = true;
        HideRequested?.Invoke(this, EventArgs.Empty);
    }

    private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (!args.DidSizeChange || _enforcingMinimum)
        {
            return;
        }

        var scale = GetScale();
        var minimumWidth = (int)Math.Ceiling(MinimumWidth * scale);
        var minimumHeight = (int)Math.Ceiling(MinimumHeight * scale);
        var width = Math.Max(sender.Size.Width, minimumWidth);
        var height = Math.Max(sender.Size.Height, minimumHeight);
        if (width == sender.Size.Width && height == sender.Size.Height)
        {
            return;
        }

        _enforcingMinimum = true;
        sender.Resize(new SizeInt32(width, height));
        _enforcingMinimum = false;
    }

    private double GetScale()
    {
        var handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var dpi = GetDpiForWindow(handle);
        return dpi == 0 ? 1d : dpi / 96d;
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint window);
}
