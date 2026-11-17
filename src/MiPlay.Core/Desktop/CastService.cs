using System.Net.Sockets;
using DLNACast.Core.MiPlay;
using DLNACast.Core.Models;

namespace MiPlay.Desktop.Services;

/// <summary>Owns one user-started session. Rediscovery never changes the selected speaker identity.</summary>
public sealed class CastService : IAsyncDisposable
{
    private readonly IDesktopAudioPlatform platform;
    private readonly ISpeakerDiscovery discovery;
    private readonly Func<ICastSession> sessionFactory;
    private readonly Func<string> encoderPath;
    private readonly TimeSpan retryDelay;
    private readonly SemaphoreSlim gate = new(1, 1);
    private CancellationTokenSource? lifetime;
    private Task? run;
    private ICastSession? active;
    private int? desiredVolume;
    private int desiredDelayMilliseconds = 1000;
    private string? volumeWarning;
    private int volumeWrites;
    private bool disposed;
    public CastStatus Status { get; private set; } = new(DesktopCastState.Idle, "选择音箱后开始投放");
    public event EventHandler<CastStatus>? StatusChanged;
    public event EventHandler<int>? ReceiverVolumeObserved;
    public event EventHandler<SpeakerIdentity>? ReceiverIdentityObserved;
    public int? ReceiverVolume => active?.ReceiverVolume;
    public bool HasUnconfirmedVolume => volumeWarning is not null;

    public CastService(IDesktopAudioPlatform platform, ISpeakerDiscovery discovery,
        Func<ICastSession>? sessionFactory = null, Func<string>? encoderPath = null, TimeSpan? retryDelay = null)
    {
        this.platform = platform;
        this.discovery = discovery;
        this.sessionFactory = sessionFactory ?? (() => new NativeCastSession(platform));
        this.encoderPath = encoderPath ?? MiPlayFfmpegLocator.RequireExecutable;
        this.retryDelay = retryDelay ?? TimeSpan.FromSeconds(2);
        platform.Suspending += OnSuspending;
    }

    public async Task StartAsync(Speaker speaker, CancellationToken token = default, int delayMilliseconds = 1000, int audioBitRate = MiPlayProtocolConstants.AacBitRate)
    {
        MiPlayAudioQuality.Validate(audioBitRate);
        if (delayMilliseconds is < 0 or > 2000) throw new ArgumentOutOfRangeException(nameof(delayMilliseconds));
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (run is { IsCompleted: false }) throw new InvalidOperationException("请先停止当前投放。");
            lifetime?.Dispose();
            lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
            desiredDelayMilliseconds = delayMilliseconds;
            run = RunAsync(speaker, ready, lifetime.Token, audioBitRate);
        }
        finally { gate.Release(); }
        await ready.Task.ConfigureAwait(false);
    }

    public async Task StopAsync(string message = "已停止投放")
    {
        Task? pending;
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            lifetime?.Cancel();
            pending = run;
            if (pending is { IsCompleted: false }) Publish(new(DesktopCastState.Stopping, "正在停止…"));
        }
        finally { gate.Release(); }
        if (pending is not null) await pending.ConfigureAwait(false);
        Publish(new(DesktopCastState.Idle, message));
    }

    public async Task SetVolumeAsync(int volume, CancellationToken token = default)
    {
        desiredVolume = Math.Clamp(volume, 0, 100);
        var session = active;
        if (session is null || Status.State != DesktopCastState.Casting) return;
        await ApplyVolumeAsync(session, Math.Clamp(volume, 0, 100), token).ConfigureAwait(false);
    }

    public async Task SetDelayAsync(int milliseconds, CancellationToken token = default)
    {
        if (milliseconds is < 0 or > 2000) throw new ArgumentOutOfRangeException(nameof(milliseconds));
        Volatile.Write(ref desiredDelayMilliseconds, milliseconds);
        var session = active;
        if (session is not null && Status.State == DesktopCastState.Casting)
            await session.SetDelayAsync(milliseconds, token).ConfigureAwait(false);
    }

    private async Task ApplyVolumeAsync(ICastSession session, int volume, CancellationToken token)
    {
        Interlocked.Increment(ref volumeWrites);
        try
        {
            await session.SetVolumeAsync(volume, token).ConfigureAwait(false);
            volumeWarning = null;
        }
        catch (MiPlayVolumeAcknowledgementTimeoutException)
        {
            volumeWarning = "正在投放，音量调整未获确认，可稍后重试";
        }
        finally { Interlocked.Decrement(ref volumeWrites); }
        if (Status.State == DesktopCastState.Casting)
            Publish(Status with { Message = volumeWarning ?? "正在投放",
                Failure = volumeWarning is null ? DesktopFailure.None : DesktopFailure.VolumeControl });
    }

    private async Task RunAsync(Speaker selected, TaskCompletionSource ready, CancellationToken token, int audioBitRate)
    {
        var everStreamed = false;
        volumeWarning = null;
        try
        {
            var path = encoderPath();
            for (var attemptNumber = 0; ; attemptNumber++)
            {
                token.ThrowIfCancellationRequested();
                Exception? failure = null;
                MiPlayCastDiagnostics? terminalDiagnostics = null;
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(token);
                try
                {
                    if (attemptNumber > 0)
                    {
                        Publish(new(DesktopCastState.Reconnecting, $"正在重新连接 {selected.Name}（{attemptNumber}/3）"));
                        await Task.Delay(retryDelay, token).ConfigureAwait(false);
                        var found = await discovery.SearchAsync(token).ConfigureAwait(false);
                        selected = found.FirstOrDefault(s => string.Equals(s.Id, selected.Id, StringComparison.OrdinalIgnoreCase))
                            ?? throw new SocketException((int)SocketError.HostUnreachable);
                    }
                    else Publish(new(DesktopCastState.Connecting, $"正在连接 {selected.Name}…"));

                    await using var session = sessionFactory();
                    var ended = new TaskCompletionSource<MiPlayCastDiagnostics>(TaskCreationOptions.RunContinuationsAsynchronously);
                    void OnDiagnostics(object? sender, MiPlayCastDiagnostics diagnostic)
                    {
                        if (diagnostic.State is MiPlayCastState.Error or MiPlayCastState.Idle)
                            ended.TrySetResult(diagnostic);
                        else if (diagnostic.State == MiPlayCastState.Streaming && !token.IsCancellationRequested)
                            Publish(new(DesktopCastState.Casting,
                                volumeWarning ?? (diagnostic.ContainsAudiblePcm == false ? "正在投放，当前没有声音输入" : $"正在投放到 {selected.Name}"),
                                volumeWarning is not null ? DesktopFailure.VolumeControl :
                                    diagnostic.ContainsAudiblePcm == false ? DesktopFailure.NoInput : DesktopFailure.None, diagnostic));
                    }
                    session.DiagnosticsChanged += OnDiagnostics;
                    Task? monitor = null;
                    try
                    {
                        active = session;
                        var output = platform.GetDefaultOutputDevice();
                        attempt.CancelAfter(TimeSpan.FromSeconds(30));
                        await session.StartAsync(new(selected.Renderer,
                            new CaptureSelection.SystemMix(output.Id, output.DisplayName), path, PlaybackDelayMicroseconds: Volatile.Read(ref desiredDelayMilliseconds) * 1000, AudioBitRate: audioBitRate), attempt.Token).ConfigureAwait(false);
                        attempt.CancelAfter(Timeout.InfiniteTimeSpan);
                        token.ThrowIfCancellationRequested();
                        everStreamed = true;
                        if (desiredVolume is { } savedVolume) await ApplyVolumeAsync(session, savedVolume, token).ConfigureAwait(false);
                        Publish(new(DesktopCastState.Casting, volumeWarning ?? $"正在投放到 {selected.Name}",
                            volumeWarning is null ? DesktopFailure.None : DesktopFailure.VolumeControl, session.Diagnostics));
                        ready.TrySetResult();
                        monitor = MonitorDefaultOutputAsync(session, output.Id, attempt.Token);
                        var winner = await Task.WhenAny(ended.Task, monitor).ConfigureAwait(false);
                        if (winner == monitor) await monitor.ConfigureAwait(false);
                        terminalDiagnostics = await ended.Task.WaitAsync(attempt.Token).ConfigureAwait(false);
                        throw new IOException(terminalDiagnostics.LastError ?? "音箱连接已结束。");
                    }
                    finally
                    {
                        if (ended.Task.IsCompletedSuccessfully) terminalDiagnostics = ended.Task.Result;
                        active = null;
                        attempt.Cancel();
                        session.DiagnosticsChanged -= OnDiagnostics;
                        await session.StopAsync().ConfigureAwait(false);
                        if (monitor is not null)
                            try { await monitor.ConfigureAwait(false); } catch (OperationCanceledException) { }
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (OperationCanceledException exception) { failure = new TimeoutException("连接音箱超时。", exception); }
                catch (Exception exception) { failure = exception; }

                var kind = ClassifyFailure(failure!, terminalDiagnostics);
                if (!everStreamed || attemptNumber >= 3 || kind is not DesktopFailure.Offline)
                {
                    Publish(new(DesktopCastState.Error, ExplainFailure(kind, failure!), kind, terminalDiagnostics));
                    ready.TrySetException(failure!);
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { ready.TrySetCanceled(token); }
        catch (Exception exception)
        {
            var kind = ClassifyFailure(exception);
            Publish(new(DesktopCastState.Error, ExplainFailure(kind, exception), kind));
            ready.TrySetException(exception);
        }
    }

    private async Task MonitorDefaultOutputAsync(ICastSession session, string previous, CancellationToken token)
    {
        int? lastVolume = null;
        SpeakerIdentity? lastIdentity = null;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
        {
            if (Volatile.Read(ref volumeWrites) == 0 && session.ReceiverVolume is >= 0 and <= 100 && session.ReceiverVolume != lastVolume)
            {
                var resolvedWarning = volumeWarning is not null && (session.ReceiverVolume == desiredVolume || lastVolume is not null);
                lastVolume = session.ReceiverVolume;
                desiredVolume = lastVolume;
                if (resolvedWarning)
                {
                    volumeWarning = null;
                    Publish(Status with { Message = "正在投放", Failure = DesktopFailure.None });
                }
                ReceiverVolumeObserved?.Invoke(this, lastVolume!.Value);
            }
            if (session.ReceiverIdentity is { } identity && identity != lastIdentity)
            {
                lastIdentity = identity;
                ReceiverIdentityObserved?.Invoke(this, identity);
            }
            AudioSourceItem output;
            try { output = platform.GetDefaultOutputDevice(); }
            catch (Exception ex) { throw new AudioCaptureException("无法读取系统输出设备。", ex); }
            if (output.Id == previous) continue;
            try { await session.SetCaptureAsync(new CaptureSelection.SystemMix(output.Id, output.DisplayName), token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex) { throw new AudioCaptureException("切换系统输出设备失败。", ex); }
            previous = output.Id;
        }
    }

    public static DesktopFailure ClassifyFailure(Exception exception, MiPlayCastDiagnostics? diagnostic = null)
    {
        if (exception is MiPlayBootstrapClosedException) return DesktopFailure.Compatibility;
        if (diagnostic?.FailureKind == MiPlayCastFailureKind.ReceiverBusy) return DesktopFailure.Busy;
        if (diagnostic?.FailureKind == MiPlayCastFailureKind.CaptureFailed) return DesktopFailure.Capture;
        if (exception is UnauthorizedAccessException) return DesktopFailure.Permission;
        if (exception is AudioCaptureException) return DesktopFailure.Capture;
        var text = exception.ToString();
        if (text.Contains("SafetyInfo", StringComparison.OrdinalIgnoreCase) || text.Contains("SafetyAuth", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("authentication", StringComparison.OrdinalIgnoreCase)) return DesktopFailure.Authentication;
        if (exception is FileNotFoundException || text.Contains("FFmpeg", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("encoder", StringComparison.OrdinalIgnoreCase)) return DesktopFailure.Encoder;
        if (diagnostic?.FailureKind == MiPlayCastFailureKind.TransportStall ||
            exception is SocketException or TimeoutException or IOException) return DesktopFailure.Offline;
        return DesktopFailure.Unknown;
    }

    private static string ExplainFailure(DesktopFailure kind, Exception exception) => kind switch
    {
        DesktopFailure.Permission => "未获得系统音频录制权限，请在系统设置中允许后重试。",
        DesktopFailure.Offline => "音箱连接中断或超时，请检查电脑和音箱的网络后重新开始。",
        DesktopFailure.Busy => "音箱正被其他设备使用，请结束其他投放后重试。",
        DesktopFailure.Compatibility => "接收端在初始化握手时关闭了连接，尚未开始发送音频。当前连接方式可能不兼容。",
        DesktopFailure.Authentication => "音箱使用了当前版本尚未支持的认证流程，请导出诊断记录。",
        DesktopFailure.Encoder => "音频编码器缺失或运行失败，请检查测试包是否完整。",
        DesktopFailure.Capture => exception.Message,
        _ => $"投放失败：{exception.Message}"
    };

    private void Publish(CastStatus status) { Status = status; StatusChanged?.Invoke(this, status); }
    private async void OnSuspending(object? sender, EventArgs args)
    {
        try { await StopAsync("电脑已休眠；唤醒后可重新开始投放。").ConfigureAwait(false); }
        catch (Exception ex) { Publish(new(DesktopCastState.Error, ex.Message)); }
    }
    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        platform.Suspending -= OnSuspending;
        await StopAsync().ConfigureAwait(false);
        lifetime?.Dispose();
    }
}

public sealed class AudioCaptureException(string message, Exception? inner = null) : Exception(message, inner);
