using DLNACast.Core.Abstractions;
using DLNACast.Core.Audio;
using DLNACast.Core.Models;

namespace MiPlay.Desktop.Services;

/// <summary>One physical tap, bounded independent queues. A slow receiver cannot block another.</summary>
public sealed class SharedDesktopAudioPlatform(IDesktopAudioPlatform inner) : IDesktopAudioPlatform, IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly object sync = new();
    private readonly Dictionary<Subscription, PcmFrameBuffer> subscribers = [];
    private SwitchableAudioCaptureSource? capture;
    private CaptureHealth? lastCaptureHealth;
    private PcmFrameBuffer? buffer;
    private CancellationTokenSource? lifetime;
    private Task? pump;
    private bool muted, disposed;
    public bool SupportsLocalMute => inner.SupportsLocalMute;
    public event EventHandler? Suspending { add => inner.Suspending += value; remove => inner.Suspending -= value; }
    public AudioSourceItem GetDefaultOutputDevice() => inner.GetDefaultOutputDevice();
    public IReadOnlyList<AudioSourceItem> GetOutputDevices() => inner.GetOutputDevices();
    public IReadOnlyList<AudioSourceItem> GetCandidateProcesses() => inner.GetCandidateProcesses();
    public IMediaPacingClock CreateMediaClock() => inner.CreateMediaClock();
    public IAudioCaptureSource CreateCapture(CaptureSelection selection) => new Subscription(this, selection);

    public async Task SetMutedAsync(bool value, CancellationToken token = default)
    {
        if (value && !SupportsLocalMute) throw new NotSupportedException("当前平台暂不支持捕获并静音本机。");
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (capture?.Selection is CaptureSelection.SystemMix selection)
                await capture.SwitchAsync(selection with { MuteLocalOutput = value }, token).ConfigureAwait(false);
            else if (capture?.Selection is CaptureSelection.Process process)
                await capture.SwitchAsync(process with { MuteLocalOutput = value }, token).ConfigureAwait(false);
            muted = value;
        }
        finally { gate.Release(); }
    }

    private async Task SubscribeAsync(Subscription subscriber, PcmFrameBuffer destination, CancellationToken token)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var selection = subscriber.Selection is CaptureSelection.SystemMix system
                ? system with { MuteLocalOutput = muted } : subscriber.Selection is CaptureSelection.Process app
                    ? app with { MuteLocalOutput = muted } : subscriber.Selection;
            if (capture is null)
            {
                // Capacity covers short capture-worker scheduling jitter; reads
                // below drain immediately rather than adding this to playback delay.
                buffer = new(AudioChannelRoute.Stereo, PcmFrameFormat.MiPlayAac, maximumMilliseconds: 128);
                lifetime = new();
                capture = new(inner, selection);
                capture.CaptureFailed += OnFailure;
                try
                {
                    // The shared tap lifetime must not belong to the first speaker.
                    await capture.StartAsync(buffer, lifetime.Token).ConfigureAwait(false);
                }
                catch { await ReleaseCaptureAsync().ConfigureAwait(false); throw; }
            }
            else await capture.SwitchAsync(selection, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            lock (sync) subscribers.Add(subscriber, destination);
            pump ??= PumpAsync(buffer!, lifetime!.Token);
        }
        catch
        {
            bool empty; lock (sync) empty = subscribers.Count == 0;
            if (empty) await ReleaseCaptureAsync().ConfigureAwait(false);
            throw;
        }
        finally { gate.Release(); }
    }

    private async Task PumpAsync(PcmFrameBuffer source, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                // The group encoder owns pacing and recovery silence. The tap
                // hub only transfers real capture, including short arrival bursts.
                var frame = await source.ReadCapturedFrameAsync(token).ConfigureAwait(false);
                lock (sync)
                    foreach (var destination in subscribers.Values.Distinct())
                        destination.Write(frame.Bytes, frame.PresentationTimeMicroseconds,
                            frame.ReanchorPresentationTimeline, frame.CaptureDiscontinuity);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { OnFailure(this, ex); }
    }

    private void OnFailure(object? sender, Exception failure)
    {
        Subscription[] active; lock (sync) active = subscribers.Keys.ToArray();
        foreach (var subscription in active) subscription.Fail(failure);
    }
    private async Task UnsubscribeAsync(Subscription subscriber)
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            bool empty;
            lock (sync) { subscribers.Remove(subscriber); empty = subscribers.Count == 0; }
            if (empty) await ReleaseCaptureAsync().ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }
    private async Task ReleaseCaptureAsync()
    {
        lifetime?.Cancel();
        if (capture is not null)
        {
            capture.CaptureFailed -= OnFailure;
            await capture.DisposeAsync().ConfigureAwait(false);
            Volatile.Write(ref lastCaptureHealth, capture.Health);
            capture = null;
        }
        if (pump is not null) await pump.ConfigureAwait(false);
        if (buffer is not null) await buffer.DisposeAsync().ConfigureAwait(false);
        lifetime?.Dispose(); lifetime = null; pump = null; buffer = null;
    }
    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed) return;
            disposed = true;
            lock (sync) subscribers.Clear();
            await ReleaseCaptureAsync().ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }
    // The owning controller awaits disposal; the underlying platform is owned by the application.
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    private sealed class Subscription(SharedDesktopAudioPlatform owner, CaptureSelection selection) : IAudioCaptureSource
    {
        public CaptureSelection Selection => selection;
        public bool IsRunning { get; private set; }
        public CaptureHealth Health => owner.capture?.Health ?? Volatile.Read(ref owner.lastCaptureHealth) ?? new(DateTimeOffset.UtcNow, null, null, 0);
        public event EventHandler<Exception>? CaptureFailed;
        public void Fail(Exception failure) => CaptureFailed?.Invoke(this, failure);
        public async Task StartAsync(PcmFrameBuffer destination, CancellationToken token)
        {
            if (IsRunning) throw new InvalidOperationException("采集订阅已经启动。");
            await owner.SubscribeAsync(this, destination, token).ConfigureAwait(false);
            IsRunning = true;
        }
        public async Task StopAsync() { await owner.UnsubscribeAsync(this).ConfigureAwait(false); IsRunning = false; }
        public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
    }
}
