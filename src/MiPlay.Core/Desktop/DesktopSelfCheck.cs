namespace MiPlay.Desktop.Services;

public static class DesktopSelfCheck
{
    public static void Run()
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
                var codecs = output.GetAwaiter().GetResult().Split('\n').SelectMany(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToHashSet();
                if (encoder.ExitCode != 0 || !codecs.Contains(expected) || !codecs.Contains("libmp3lame"))
                    throw new InvalidOperationException($"Missing {expected} / libmp3lame encoder: {error.GetAwaiter().GetResult()}");
                if (OperatingSystem.IsMacOS())
                {
                    var bridge = System.Runtime.InteropServices.NativeLibrary.Load(Path.Combine(AppContext.BaseDirectory, "libMiPlayAudio.dylib"));
                    try
                    {
                        foreach (var symbol in new[] { "mp_start", "mp_start_process", "mp_outputs_json", "mp_processes_json",
                            "mp_raw_format_json", "mp_read_raw", "mp_wait_capture", "mp_capture_info_json",
                            "mp_pcm_converter_create", "mp_pcm_convert", "mp_pcm_converter_info_json", "mp_pcm_converter_reset", "mp_pcm_converter_destroy" })
                            System.Runtime.InteropServices.NativeLibrary.GetExport(bridge, symbol);
                    }
                    finally { System.Runtime.InteropServices.NativeLibrary.Free(bridge); }
                }
                Console.WriteLine("Self-check passed: runtime, AAC/MP3 encoders and native audio-source bridge are loadable. No audio captured.");
            }
            catch (Exception exception) { Console.Error.WriteLine(exception.Message); Environment.ExitCode = 1; }
    }
}
