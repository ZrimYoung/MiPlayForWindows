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
            try
            {
                var ffmpeg = DLNACast.Core.MiPlay.MiPlayFfmpegLocator.RequireExecutable();
                var start = new System.Diagnostics.ProcessStartInfo(ffmpeg)
                    { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                start.ArgumentList.Add("-hide_banner"); start.ArgumentList.Add("-encoders");
                using var encoder = System.Diagnostics.Process.Start(start)!;
                var output = encoder.StandardOutput.ReadToEndAsync();
                var error = encoder.StandardError.ReadToEndAsync();
                if (!encoder.WaitForExit(10000)) { encoder.Kill(true); throw new TimeoutException("Encoder check timed out."); }
                var expected = OperatingSystem.IsWindows() ? "aac_mf" : "aac";
                if (encoder.ExitCode != 0 || !output.GetAwaiter().GetResult().Split('\n').Any(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(expected)))
                    throw new InvalidOperationException($"Missing {expected} encoder: {error.GetAwaiter().GetResult()}");
                if (OperatingSystem.IsMacOS())
                {
                    var bridge = System.Runtime.InteropServices.NativeLibrary.Load(Path.Combine(AppContext.BaseDirectory, "libMiPlayAudio.dylib"));
                    try { System.Runtime.InteropServices.NativeLibrary.GetExport(bridge, "mp_start"); }
                    finally { System.Runtime.InteropServices.NativeLibrary.Free(bridge); }
                }
                Console.WriteLine("Self-check passed: runtime, AAC encoder and native bridge are loadable. No audio captured.");
            }
            catch (Exception exception) { Console.Error.WriteLine(exception.Message); Environment.ExitCode = 1; }
            return;
        }
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}
