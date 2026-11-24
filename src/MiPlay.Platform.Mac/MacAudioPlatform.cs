using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using DLNACast.Core.Abstractions;
using DLNACast.Core.Audio;
using DLNACast.Core.Models;
using MiPlay.Desktop.Services;

namespace MiPlay.Platform.Mac;

internal static class NativeAudio
{
    private const string Library = "MiPlayAudio";
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void SleepCallback();
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int mp_default_output(StringBuilder uid, int capacity);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int mp_outputs_json(StringBuilder json, int capacity);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int mp_processes_json(StringBuilder json, int capacity);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int mp_start_process(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string uid, int pid, int includeChildren, int mute, out IntPtr handle);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int mp_start([MarshalAs(UnmanagedType.LPUTF8Str)] string uid, int mute, out IntPtr handle);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int mp_read(IntPtr handle, [Out] byte[] pcm, int capacity, out ulong endTimeNs);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int mp_raw_format_json(IntPtr handle, StringBuilder json, int capacity);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int mp_read_raw(IntPtr handle, [Out] byte[] pcm, int capacity, out ulong endTimeNs, out int discontinuity);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int mp_wait_capture(IntPtr handle, int timeoutMilliseconds);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int mp_pcm_converter_create([MarshalAs(UnmanagedType.LPUTF8Str)] string formatJson, out IntPtr converter);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern unsafe int mp_pcm_convert(IntPtr converter, byte* source, int byteCount, byte* output, int capacity);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int mp_pcm_converter_info_json(IntPtr converter, StringBuilder json, int capacity);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int mp_pcm_converter_reset(IntPtr converter);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void mp_pcm_converter_destroy(IntPtr converter);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int mp_capture_info_json(IntPtr handle, StringBuilder json, int capacity);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void mp_stop(IntPtr handle);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void mp_wait_nanos(ulong nanos);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern ulong mp_time_nanos();
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr mp_watch_sleep(SleepCallback callback);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void mp_unwatch_sleep(IntPtr observer);
}

public sealed class MacAudioPlatform : IDesktopAudioPlatform, IRawAudioSourceCatalog
{
    private readonly NativeAudio.SleepCallback sleepCallback;
    private IntPtr sleepObserver;
    public bool SupportsLocalMute => true;
    public event EventHandler? Suspending;
    public MacAudioPlatform()
    {
        if (!OperatingSystem.IsMacOSVersionAtLeast(14, 2)) throw new PlatformNotSupportedException("需要 macOS 14.2 或更新版本。");
        sleepCallback = () => Suspending?.Invoke(this, EventArgs.Empty);
        sleepObserver = NativeAudio.mp_watch_sleep(sleepCallback);
    }
    public AudioSourceItem GetDefaultOutputDevice()
    {
        var uid = new StringBuilder(2048);
        var status = NativeAudio.mp_default_output(uid, uid.Capacity);
        if (status != 0) throw new AudioCaptureException($"无法读取系统默认音频输出（{status}）。");
        return new(uid.ToString(), "系统默认输出");
    }
    public IReadOnlyList<AudioSourceItem> GetOutputDevices() => ReadSources(NativeAudio.mp_outputs_json);
    public IReadOnlyList<AudioSourceItem> GetCandidateProcesses() => ReadSources(NativeAudio.mp_processes_json);
    private static IReadOnlyList<AudioSourceItem> ReadSources(Func<StringBuilder, int, int> read)
    {
        var buffer = new StringBuilder(128 * 1024);
        var status = read(buffer, buffer.Capacity);
        if (status < 0) throw new AudioCaptureException($"无法枚举音频来源（{status}）。");
        using var document = JsonDocument.Parse(buffer.ToString());
        return document.RootElement.EnumerateArray().Select(value => new AudioSourceItem(
            value.GetProperty("id").GetString()!, value.GetProperty("name").GetString()!,
            value.TryGetProperty("processId", out var pid) ? pid.GetInt32() : null)).ToArray();
    }
    public IAudioCaptureSource CreateCapture(CaptureSelection selection) => selection is CaptureSelection.SystemMix or CaptureSelection.Process
        ? new MacCaptureSource(selection) : throw new NotSupportedException("不支持此音频来源。");
    public IRawAudioCaptureSource CreateRawCapture(CaptureSelection selection) => selection is CaptureSelection.SystemMix or CaptureSelection.Process
        ? new MacCaptureSource(selection) : throw new NotSupportedException("不支持此音频来源。");
    public IMediaPacingClock CreateMediaClock() => new MacMediaClock();
    public void Dispose()
    {
        if (sleepObserver != IntPtr.Zero) NativeAudio.mp_unwatch_sleep(sleepObserver);
        sleepObserver = IntPtr.Zero;
        GC.KeepAlive(sleepCallback);
    }
}

internal sealed class MacMediaClock : IMediaPacingClock
{
    private readonly DedicatedMediaPacingClock clock = new(NativeAudio.mp_wait_nanos);
    public ValueTask WaitUntilAsync(long targetTimestamp, CancellationToken token) =>
        clock.WaitUntilAsync(targetTimestamp, token);
    public void Dispose() => clock.Dispose();
}
