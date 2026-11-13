using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using MiPlay.Desktop.Services;

namespace MiPlay.Desktop;

public partial class App : Application
{
    private IClassicDesktopStyleApplicationLifetime? desktop;
    private IDesktopAudioPlatform? platform;
    private MainViewModel? viewModel;
    private MainWindow? window;
    private bool quitting;
    public override void Initialize() => AvaloniaXamlLoader.Load(this);
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime lifetime)
        {
            desktop = lifetime;
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var store = new DesktopStore();
            try
            {
#if WINDOWS_DESKTOP
                platform = new Platform.Windows.WindowsAudioPlatform();
#else
                platform = new Platform.Mac.MacAudioPlatform();
#endif
                var discovery = new SpeakerDiscovery();
                viewModel = new MainViewModel(new CastService(platform, discovery), discovery, store);
                window = new MainWindow { DataContext = viewModel };
                window.Closing += (_, e) => { if (!quitting) { e.Cancel = true; window.Hide(); } };
                desktop.MainWindow = window;
                desktop.ShutdownRequested += (_, e) => { if (!quitting) { e.Cancel = true; Quit(this, EventArgs.Empty); } };
                window.Opened += async (_, _) => await viewModel.RefreshAsync();
            }
            catch (Exception exception)
            {
                store.Log("startup", exception.Message);
                desktop.MainWindow = new Window { Title = "MiPlay Desktop", Width = 500, Height = 200,
                    Content = new TextBlock { Text = $"无法启动：{exception.Message}\n请确认安装包完整，Mac 系统版本不低于 14.2。", Margin = new Thickness(24), TextWrapping = Avalonia.Media.TextWrapping.Wrap } };
                desktop.MainWindow.Closed += (_, _) => desktop.Shutdown(1);
            }
        }
        base.OnFrameworkInitializationCompleted();
    }
    private void ShowWindow(object? sender, EventArgs e) { window?.Show(); window?.Activate(); }
    private async void StopCasting(object? sender, EventArgs e) { if (viewModel is not null) await viewModel.StopAsync(); }
    private async void Quit(object? sender, EventArgs e)
    {
        if (quitting) return;
        quitting = true;
        try { if (viewModel is not null) await viewModel.DisposeAsync(); }
        finally { platform?.Dispose(); desktop?.Shutdown(); }
    }
}
