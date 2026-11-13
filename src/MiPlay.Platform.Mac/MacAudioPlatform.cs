using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
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
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int mp_start([MarshalAs(UnmanagedType.LPUTF8Str)] string uid, out IntPtr handle);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int mp_read(IntPtr handle, [Out] byte[] pcm, int capacity, out ulong endTimeNs);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void mp_stop(IntPtr handle);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void mp_wait_nanos(ulong nanos);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern ulong mp_time_nanos();
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr mp_watch_sleep(SleepCallback callback);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void mp_unwatch_sleep(IntPtr observer);
}

public sealed class MacAudioPlatform : IDesktopAudioPlatform
{
    private readonly NativeAudio.SleepCallback sleepCallback;
    private IntPtr sleepObserver;
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
    public IReadOnlyList<AudioSourceItem> GetOutputDevices() => [GetDefaultOutputDevice()];
    public IReadOnlyList<AudioSourceItem> GetCandidateProcesses() => [];
    public IAudioCaptureSource CreateCapture(CaptureSelection selection) => selection is CaptureSelection.SystemMix system
        ? new MacCaptureSource(system) : throw new NotSupportedException("首版支持系统声音投放。");
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
    public ValueTask WaitUntilAsync(long targetTimestamp, CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var remaining = targetTimestamp - Stopwatch.GetTimestamp();
            if (remaining <= 0) return ValueTask.CompletedTask;
            var nanos = (ulong)Math.Min(20_000_000, remaining * 1_000_000_000d / Stopwatch.Frequency);
            if (nanos > 0) NativeAudio.mp_wait_nanos(nanos);
        }
    }
    public void Dispose() { }
}

internal sealed class MacCaptureSource(CaptureSelection.SystemMix selection) : IAudioCaptureSource
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly CaptureHealthTracker health = new();
    private IntPtr handle;
    private CancellationTokenSource? lifetime;
    private Task? worker;
    public CaptureSelection Selection => selection;
    public bool IsRunning => worker is { IsCompleted: false };
    public CaptureHealth Health => health.Snapshot();
    public event EventHandler<Exception>? CaptureFailed;

    public async Task StartAsync(PcmFrameBuffer destination, CancellationToken token)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (handle != IntPtr.Zero) throw new InvalidOperationException("音频采集已经启动。");
            token.ThrowIfCancellationRequested();
            var status = NativeAudio.mp_start(selection.EndpointId, out handle);
            if (status != 0)
            {
                if (status == 0x7065726d) throw new UnauthorizedAccessException("系统未允许音频录制。");
                throw new AudioCaptureException($"无法开始系统音频采集（{status}），请检查系统音频录制权限和输出设备。");
            }
            lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
            health.Reset();
            var offset = checked((long)PcmPresentationTimestamp.GetElapsedRealtimeNanoseconds() - (long)NativeAudio.mp_time_nanos());
            var captureHandle = handle;
            var workerToken = lifetime.Token;
            worker = Task.Run(async () =>
            {
                var buffer = new byte[65536];
                var assembler = new PcmFrameAssembler(destination);
                try
                {
                    while (!workerToken.IsCancellationRequested)
                    {
                        var count = NativeAudio.mp_read(captureHandle, buffer, buffer.Length, out var timestamp);
                        if (count < 0) throw new AudioCaptureException($"系统音频转换失败（{count}）。");
                        if (count > 0)
                        {
                            health.Record(buffer.AsSpan(0, count));
                            assembler.Push(buffer.AsSpan(0, count), checked((ulong)((long)timestamp + offset)));
                        }
                        else await Task.Delay(3, workerToken).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (workerToken.IsCancellationRequested) { }
                catch (Exception exception) { CaptureFailed?.Invoke(this, exception); }
            }, CancellationToken.None);
        }
        catch
        {
            if (handle != IntPtr.Zero) NativeAudio.mp_stop(handle);
            handle = IntPtr.Zero;
            throw;
        }
        finally { gate.Release(); }
    }

    public async Task StopAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            lifetime?.Cancel();
            if (worker is not null) await worker.ConfigureAwait(false);
            // Stop the HAL callback before destroying its native queue and converter.
            if (handle != IntPtr.Zero) NativeAudio.mp_stop(handle);
            handle = IntPtr.Zero;
            worker = null;
            lifetime?.Dispose();
            lifetime = null;
        }
        finally { gate.Release(); }
    }
    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
