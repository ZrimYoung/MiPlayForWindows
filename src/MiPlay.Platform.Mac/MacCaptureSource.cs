using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using DLNACast.Core.Abstractions;
using DLNACast.Core.Audio;
using DLNACast.Core.Models;
using MiPlay.Desktop.Services;

namespace MiPlay.Platform.Mac;

internal interface IMacRawCaptureBackend
{
    int Start(CaptureSelection selection, out IntPtr handle);
    string ReadFormat(IntPtr handle);
    int Read(IntPtr handle, byte[] destination, out ulong endTimeNanoseconds, out int discontinuity);
    int Wait(IntPtr handle, int timeoutMilliseconds);
    AudioCaptureDiagnostics? ReadDiagnostics(IntPtr handle);
    ulong TimeNanoseconds();
    void Stop(IntPtr handle);
}

internal sealed class MacNativeCaptureBackend : IMacRawCaptureBackend
{
    private readonly StringBuilder json = new(8192);
    public int Start(CaptureSelection selection, out IntPtr handle) => selection switch
    {
        CaptureSelection.SystemMix system => NativeAudio.mp_start(system.EndpointId, system.MuteLocalOutput ? 1 : 0, out handle),
        // This mode retains the existing cross-device process mixdown behavior.
        CaptureSelection.Process process => NativeAudio.mp_start_process("", process.ProcessId, process.IncludeChildren ? 1 : 0,
            process.MuteLocalOutput ? 1 : 0, out handle),
        _ => throw new NotSupportedException("不支持此音频来源。")
    };
    public string ReadFormat(IntPtr handle)
    {
        json.Clear();
        var result = NativeAudio.mp_raw_format_json(handle, json, json.Capacity);
        if (result < 0) throw new AudioCaptureException($"无法读取原始音频格式（{result}）。");
        return json.ToString();
    }
    public int Read(IntPtr handle, byte[] destination, out ulong endTimeNanoseconds, out int discontinuity) =>
        NativeAudio.mp_read_raw(handle, destination, destination.Length, out endTimeNanoseconds, out discontinuity);
    public int Wait(IntPtr handle, int timeoutMilliseconds) => NativeAudio.mp_wait_capture(handle, timeoutMilliseconds);
    public AudioCaptureDiagnostics? ReadDiagnostics(IntPtr handle)
    {
        json.Clear();
        return NativeAudio.mp_capture_info_json(handle, json, json.Capacity) > 0
            ? AudioCaptureDiagnostics.FromNativeJson(json.ToString()) : null;
    }
    public ulong TimeNanoseconds() => NativeAudio.mp_time_nanos();
    public void Stop(IntPtr handle) => NativeAudio.mp_stop(handle);
}

internal interface IMacPcmSendAdapter : IRawPcmSink, IDisposable
{
    AudioCaptureDiagnostics DecorateDiagnostics(AudioCaptureDiagnostics capture);
}

internal delegate IMacPcmSendAdapter MacPcmSendAdapterFactory(string formatJson, RawPcmFormat format,
    PcmFrameBuffer destination, CaptureHealthTracker health);

internal sealed class MacCaptureSource(CaptureSelection selection, IMacRawCaptureBackend? captureBackend = null,
    MacPcmSendAdapterFactory? adapterFactory = null) : IAudioCaptureSource, IRawAudioCaptureSource
{
    private readonly IMacRawCaptureBackend backend = captureBackend ?? new MacNativeCaptureBackend();
    private readonly MacPcmSendAdapterFactory createAdapter = adapterFactory ??
        ((json, format, destination, health) => new MacPcmSendAdapter(json, format, destination, health));
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly CaptureHealthTracker health = new();
    private AudioCaptureDiagnostics? audioCapture;
    private IntPtr handle;
    private CancellationTokenSource? lifetime;
    private Task? worker;
    public CaptureSelection Selection => selection;
    public bool IsRunning => worker is { IsCompleted: false };
    // Consumers only read immutable snapshots, never a live native handle.
    public CaptureHealth Health => health.Snapshot() with { AudioCapture = Volatile.Read(ref audioCapture) };
    public event EventHandler<Exception>? CaptureFailed;

    public Task StartAsync(PcmFrameBuffer destination, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(destination);
        return StartCoreAsync(null, destination, token);
    }

    public Task StartRawAsync(IRawPcmSink destination, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(destination);
        return StartCoreAsync(destination, null, token);
    }

    private async Task StartCoreAsync(IRawPcmSink? rawDestination, PcmFrameBuffer? pcmDestination, CancellationToken token)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        IMacPcmSendAdapter? adapter = null;
        var creatingCapture = false;
        try
        {
            if (handle != IntPtr.Zero) throw new InvalidOperationException("音频采集已经启动。");
            token.ThrowIfCancellationRequested();
            creatingCapture = true;
            var status = backend.Start(selection, out handle);
            if (status != 0)
            {
                if (status == 0x7065726d) throw new UnauthorizedAccessException("系统未允许音频录制。");
                if (status == -11) throw new AudioCaptureException("所选应用尚未输出音频或已经退出，请开始播放后重新选择。");
                throw new AudioCaptureException($"无法开始音频采集（{status}），请检查系统音频录制权限和输出设备。");
            }
            var json = backend.ReadFormat(handle);
            var format = MacRawPcmFormat.Parse(json);
            if (pcmDestination is not null) adapter = createAdapter(json, format, pcmDestination, health);
            var destination = rawDestination ?? adapter!;
            lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
            health.Reset();
            Volatile.Write(ref audioCapture, null);
            var offset = checked((long)PcmPresentationTimestamp.GetElapsedRealtimeNanoseconds() - (long)backend.TimeNanoseconds());
            var captureHandle = handle;
            var workerToken = lifetime.Token;
            // Native waits are bounded but blocking. Keep them off the shared
            // thread pool that feeds the AAC encoder and network sessions.
            worker = Task.Factory.StartNew(() => CaptureLoop(captureHandle, format, destination, adapter, offset, workerToken),
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }
        catch
        {
            // A rejected second Start must not destroy the running capture.
            if (!creatingCapture) throw;
            adapter?.Dispose();
            if (handle != IntPtr.Zero) backend.Stop(handle);
            handle = IntPtr.Zero;
            lifetime?.Dispose(); lifetime = null;
            throw;
        }
        finally { gate.Release(); }
    }

    private void CaptureLoop(IntPtr captureHandle, RawPcmFormat format, IRawPcmSink destination,
        IMacPcmSendAdapter? adapter, long offset, CancellationToken token)
    {
        long nextDiagnosticTimestamp = 0;
        void RefreshDiagnostics()
        {
            if (backend.ReadDiagnostics(captureHandle) is not { } snapshot) return;
            snapshot = snapshot with
            {
                DeliveredFormat = new(format.SampleRate, format.Channels, format.BitsPerSample,
                    format.SampleType == RawPcmSampleType.Float32 ? "Float" : "SignedInteger", true),
                SourceChannelLabels = format.ChannelLabels,
                ChannelLayoutNamespace = format.ChannelLayoutNamespace
            };
            Volatile.Write(ref audioCapture, adapter?.DecorateDiagnostics(snapshot) ?? snapshot);
        }
        try
        {
            var buffer = new byte[format.GetByteCount(MacRawPcmFormat.MaximumFramesPerBlock)];
            while (!token.IsCancellationRequested)
            {
                var now = Stopwatch.GetTimestamp();
                if (now >= nextDiagnosticTimestamp)
                {
                    RefreshDiagnostics();
                    nextDiagnosticTimestamp = now + Stopwatch.Frequency;
                }
                var count = backend.Read(captureHandle, buffer, out var timestamp, out var discontinuity);
                if (count == -11) throw new AudioCaptureException("所选应用已退出，音频捕获已停止。");
                if (count == -13) throw new AudioCaptureException("音源的采样率或声道格式已变化，请重新开始投放。");
                if (count == -14) throw new AudioCaptureException("无法读取音源的新格式，请重新选择音频来源。");
                if (count < 0) throw new AudioCaptureException($"原始音频采集失败（{count}）。");
                if (count == 0)
                {
                    var waited = backend.Wait(captureHandle, 20);
                    if (waited < 0) throw new AudioCaptureException($"等待原始音频失败（{waited}）。");
                    continue;
                }
                if (count > buffer.Length || count % format.BytesPerFrame != 0)
                    throw new AudioCaptureException("采集到的原始音频不是完整采样帧。");
                var bytes = buffer.AsSpan(0, count);
                if (adapter is null) RecordRawHealth(bytes, format);
                var block = new RawPcmBlockView(bytes, format, count / format.BytesPerFrame,
                    checked((ulong)((long)timestamp + offset)), discontinuity != 0);
                destination.Write(in block);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception) { CaptureFailed?.Invoke(this, exception); }
        finally
        {
            try { RefreshDiagnostics(); }
            finally { adapter?.Dispose(); }
        }
    }

    private void RecordRawHealth(ReadOnlySpan<byte> bytes, RawPcmFormat format)
    {
        if (format.SampleType == RawPcmSampleType.Float32)
        {
            // -0.0 has a nonzero sign byte but is still silent.
            foreach (var sample in MemoryMarshal.Cast<byte, float>(bytes))
                if (sample != 0) { health.Record(bytes); return; }
            health.Record(ReadOnlySpan<byte>.Empty);
        }
        else health.Record(bytes);
    }

    public async Task StopAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            lifetime?.Cancel();
            try
            {
                if (worker is not null) await worker.ConfigureAwait(false);
            }
            finally
            {
                // The worker has finished its final diagnostics and disposed
                // the converter before the callback queue is released.
                try { if (handle != IntPtr.Zero) backend.Stop(handle); }
                finally
                {
                    handle = IntPtr.Zero; worker = null;
                    lifetime?.Dispose(); lifetime = null;
                }
            }
        }
        finally { gate.Release(); }
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
