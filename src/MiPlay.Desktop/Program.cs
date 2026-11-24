using Avalonia;

namespace MiPlay.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // A packaging check that never opens a capture device or a network socket.
        if (args.Contains("--self-check"))
        {
            Services.DesktopSelfCheck.Run();
            return;
        }
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}
