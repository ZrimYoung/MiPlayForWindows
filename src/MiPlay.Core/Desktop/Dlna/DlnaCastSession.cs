using DLNACast.Core.Audio;
using DLNACast.Core.MiPlay;
using DLNACast.Core.Models;

namespace MiPlay.Desktop.Services.Dlna;

/// <summary>DLNA live audio, independent of MiPlay timing/handshake. No capture starts until StartAsync.</summary>
public sealed class DlnaCastSession(IDesktopAudioPlatform platform) : ICastSession
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly RendererController controller = new();
    private CancellationTokenSource? lifetime;
    private Task? run;
    private SwitchableAudioCaptureSource? capture;
    private RendererDevice? renderer;
    private bool disposed;
    private int? receiverVolume;
    private long volumeRevision;
    public int? ReceiverVolume => receiverVolume;
    public MiPlayCastDiagnostics Diagnostics { get; private set; } = new(MiPlayCastState.Idle, "DLNA 已停止");
    public event EventHandler<MiPlayCastDiagnostics>? DiagnosticsChanged;
    public async Task StartAsync(MiPlaySystemAudioRequest request, CancellationToken token)
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (run is { IsCompleted: false }) throw new InvalidOperationException("请先停止当前 DLNA 投放。");
            ArgumentNullException.ThrowIfNull(request.Renderer);
            ArgumentNullException.ThrowIfNull(request.Selection);
            if (!Enum.IsDefined(request.ChannelRoute)) throw new ArgumentOutOfRangeException(nameof(request.ChannelRoute));
            lifetime?.Dispose(); lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
            renderer = request.Renderer;
            run = RunAsync(request, ready, lifetime.Token);
        }
        finally { gate.Release(); }
        await ready.Task.ConfigureAwait(false);
    }
    private async Task RunAsync(MiPlaySystemAudioRequest request, TaskCompletionSource ready, CancellationToken token)
    {
        await using var frames = new PcmFrameBuffer(request.ChannelRoute, PcmFrameFormat.MiPlayAac);
        await using var capturing = new SwitchableAudioCaptureSource(platform, request.Selection);
        try
        {
            capture = capturing;
            Publish(new(MiPlayCastState.Connecting, "正在连接 DLNA 接收端"));
            var sink = request.Renderer.SinkProtocolInfo;
            try { sink = await controller.GetSinkProtocolInfoAsync(request.Renderer, token).ConfigureAwait(false); }
            catch (Exception ex) when (ex is UpnpException or HttpRequestException or TaskCanceledException && !token.IsCancellationRequested) { }
            var profiles = ProtocolInfoMatcher.SelectProfiles(sink, allowMp3Fallback: true);
            await capturing.StartAsync(frames, token).ConfigureAwait(false);
            for (var index = 0; index < profiles.Count; index++)
            {
                var profile = profiles[index];
                var opened = false;
                await using var stream = new DlnaLiveStream(request.Renderer.Address, frames, profile, request.FfmpegPath, token);
                try
                {
                    var label = profile == DlnaStreamProfile.PcmWave ? "PCM/WAV" : "MP3 · 320 kbps";
                    Publish(new(MiPlayCastState.AwaitingReceiver, $"等待 DLNA 接收端读取 {label}"));
                    await controller.SetTransportUriAsync(request.Renderer, stream.Uri, profile, token).ConfigureAwait(false);
                    opened = true;
                    await controller.PlayAsync(request.Renderer, token).ConfigureAwait(false);
                    await stream.Connected.WaitAsync(TimeSpan.FromSeconds(8), token).ConfigureAwait(false);
                    // Detect immediate WAV rejection before declaring the first profile ready.
                    await Task.Delay(600, token).ConfigureAwait(false);
                    if (stream.Failed.IsCompleted) throw await stream.Failed.ConfigureAwait(false);
                    await ObserveVolumeAsync(request.Renderer, token).ConfigureAwait(false);
                    Publish(new(MiPlayCastState.Streaming, $"DLNA 正在投放 · {label}"));
                    ready.TrySetResult();
                    using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
                    var ticks = 0; var stoppedCount = 0;
                    while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
                    {
                        if (capturing.LastFailure is { } captureError) throw new AudioCaptureException("DLNA 音源采集失败。", captureError);
                        if (stream.Failed.IsCompleted) throw await stream.Failed.ConfigureAwait(false);
                        if (++ticks % 2 == 0) await ObserveVolumeAsync(request.Renderer, token).ConfigureAwait(false);
                        if (ticks % 3 == 0)
                        {
                            try
                            {
                                var status = await controller.GetTransportStatusAsync(request.Renderer, token).ConfigureAwait(false);
                                stoppedCount = status.State is "STOPPED" or "NO_MEDIA_PRESENT" ? stoppedCount + 1 : 0;
                            }
                            catch (UpnpException) { /* Optional status action may be unsupported. */ }
                            if (stoppedCount >= 2) throw new IOException("DLNA 接收端已停止播放。");
                        }
                        Publish(new(MiPlayCastState.Streaming, $"DLNA 正在投放 · {label}", frames.BufferedMilliseconds,
                            frames.Overruns, frames.Underruns, WireBytes: stream.WireBytes,
                            ProtocolEvidence: $"DLNA {label}; independent receiver buffering; no MiPlay synchronization"));
                    }
                    token.ThrowIfCancellationRequested();
                }
                catch (Exception ex) when (!token.IsCancellationRequested && index + 1 < profiles.Count &&
                    ex is not (AudioCaptureException or UnauthorizedAccessException))
                {
                    Publish(new(MiPlayCastState.Connecting, "DLNA PCM/WAV 未被接收，正在尝试 MP3"));
                }
                finally
                {
                    if (opened)
                    {
                        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                        try { await controller.StopAsync(request.Renderer, timeout.Token).ConfigureAwait(false); }
                        catch (Exception ex) when (ex is HttpRequestException or UpnpException or OperationCanceledException) { }
                    }
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { ready.TrySetCanceled(token); }
        catch (Exception ex)
        {
            Publish(new(MiPlayCastState.Error, "DLNA 投放失败", LastError: ex.Message,
                FailureKind: ex is AudioCaptureException ? MiPlayCastFailureKind.CaptureFailed : MiPlayCastFailureKind.TransportStall));
            ready.TrySetException(ex);
        }
        finally { capture = null; }
    }
    private async Task<int?> ReadVolumeAsync(RendererDevice device, CancellationToken token)
    {
        try { return await controller.GetVolumeAsync(device, token).ConfigureAwait(false); }
        catch (Exception ex) when (ex is UpnpException or HttpRequestException or TaskCanceledException && !token.IsCancellationRequested) { return null; }
    }
    private async Task ObserveVolumeAsync(RendererDevice device, CancellationToken token)
    {
        var revision = Interlocked.Read(ref volumeRevision);
        var observed = await ReadVolumeAsync(device, token).ConfigureAwait(false);
        if (observed is not null && revision == Interlocked.Read(ref volumeRevision)) receiverVolume = observed;
    }
    public async Task SetVolumeAsync(int volume, CancellationToken token)
    {
        var device = renderer ?? throw new InvalidOperationException("DLNA 尚未连接。");
        if (device.RenderingControl is null) throw new NotSupportedException("该 DLNA 接收端没有音量控制接口。");
        volume = Math.Clamp(volume, 0, 100);
        Interlocked.Increment(ref volumeRevision);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime?.Token ?? CancellationToken.None);
        try { await controller.SetVolumeAsync(device, volume, linked.Token).ConfigureAwait(false); }
        catch (UpnpException error)
        { throw new NotSupportedException("该 DLNA 接收端未接受音量控制，音频投放可以继续。", error); }
        receiverVolume = volume;
    }
    public Task SetCaptureAsync(CaptureSelection selection, CancellationToken token) =>
        capture?.SwitchAsync(selection, token) ?? Task.FromException(new InvalidOperationException("DLNA 音源尚未开始采集。"));
    public Task SetDelayAsync(int milliseconds, CancellationToken token) =>
        Task.FromException(new NotSupportedException("DLNA 由接收端管理缓冲，不支持 MiPlay 实时延迟调整。"));
    public async Task StopAsync()
    {
        Task? pending;
        await gate.WaitAsync().ConfigureAwait(false);
        try { lifetime?.Cancel(); pending = run; }
        finally { gate.Release(); }
        if (pending is not null) await pending.ConfigureAwait(false);
        Publish(new(MiPlayCastState.Idle, "DLNA 已停止"));
    }
    private void Publish(MiPlayCastDiagnostics diagnostic) { Diagnostics = diagnostic; DiagnosticsChanged?.Invoke(this, diagnostic); }
    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        await StopAsync().ConfigureAwait(false);
        lifetime?.Dispose(); controller.Dispose();
    }
}
