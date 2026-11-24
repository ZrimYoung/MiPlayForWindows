using DLNACast.Core.Abstractions;
using DLNACast.Core.MiPlay;
using DLNACast.Core.Models;
using DLNACast.Core.Audio;

namespace MiPlay.Desktop.Services;

/// <summary>Owns the encoder across receiver retries, joins and departures.</summary>
internal sealed class DesktopCastGroup(IDesktopAudioPlatform platform,
    IAacEncoderFactory? encoderFactory = null, Func<bool>? prepareChannelRoutes = null) : IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly HashSet<Entry> entries = [];
    private Entry? current;
    private bool disposed;

    internal async Task<Lease> AcquireAsync(MiPlaySystemAudioRequest request, CancellationToken token)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (current is null || !current.Session.IsAvailable)
            {
                var routes = prepareChannelRoutes?.Invoke() == true ? Enum.GetValues<AudioChannelRoute>() : new[] { request.ChannelRoute };
                var group = await MiPlaySharedAudioSession.StartAsync(platform, request.Selection, routes,
                    request.FfmpegPath, cancellationToken: token,
                    encoderFactory: encoderFactory ?? new FfmpegAacEncoderFactory(
                        OperatingSystem.IsWindows() ? "aac_mf" : "aac"),
                    drainBeforeMediaReady: true, disposeWhenEmpty: false, audioBitRate: request.AudioBitRate, startupParticipantCount: 1).ConfigureAwait(false);
                current = new(group, request.AudioBitRate);
                entries.Add(current);
            }
            if (current.AudioBitRate != request.AudioBitRate)
                throw new InvalidOperationException("请停止当前投放后切换音质。");
            current.Leases++;
            return new Lease(this, current);
        }
        finally { gate.Release(); }
    }

    private async ValueTask ReleaseAsync(Entry entry)
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (--entry.Leases != 0) return;
            if (ReferenceEquals(current, entry)) current = null;
            if (entries.Remove(entry)) await entry.Session.DisposeAsync().ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed) return;
            disposed = true;
            current = null;
            foreach (var entry in entries) await entry.Session.DisposeAsync().ConfigureAwait(false);
            entries.Clear();
        }
        finally { gate.Release(); }
    }

    internal sealed class Entry(MiPlaySharedAudioSession session, int audioBitRate)
    {
        internal MiPlaySharedAudioSession Session { get; } = session;
        internal int Leases;
        internal int AudioBitRate { get; } = audioBitRate;
    }

    internal sealed class Lease(DesktopCastGroup owner, Entry entry) : IAsyncDisposable
    {
        private int disposed;
        internal MiPlaySharedAudioSession Session => entry.Session;
        public ValueTask DisposeAsync() => Interlocked.Exchange(ref disposed, 1) == 0
            ? owner.ReleaseAsync(entry) : ValueTask.CompletedTask;
    }
}

/// <summary>The transport ends before its lease on the group is released.</summary>
internal sealed class GroupCastSession(ICastSession transport, DesktopCastGroup group) : ICastSession
{
    private DesktopCastGroup.Lease? lease;
    public int? ReceiverVolume => transport.ReceiverVolume;
    public SpeakerIdentity? ReceiverIdentity => transport.ReceiverIdentity;
    public MiPlayCastDiagnostics Diagnostics => transport.Diagnostics;
    public event EventHandler<MiPlayCastDiagnostics>? DiagnosticsChanged
    {
        add => transport.DiagnosticsChanged += value;
        remove => transport.DiagnosticsChanged -= value;
    }
    public async Task StartAsync(MiPlaySystemAudioRequest request, CancellationToken token)
    {
        lease = await group.AcquireAsync(request, token).ConfigureAwait(false);
        try { await transport.StartAsync(request with { SharedAudioSession = lease.Session }, token).ConfigureAwait(false); }
        catch { await StopAsync().ConfigureAwait(false); throw; }
    }
    public async Task StopAsync()
    {
        try { await transport.StopAsync().ConfigureAwait(false); }
        finally
        {
            var previous = Interlocked.Exchange(ref lease, null);
            if (previous is not null) await previous.DisposeAsync().ConfigureAwait(false);
        }
    }
    public Task SetVolumeAsync(int volume, CancellationToken token) => transport.SetVolumeAsync(volume, token);
    public Task SetDelayAsync(int milliseconds, CancellationToken token) => transport.SetDelayAsync(milliseconds, token);
    public Task SetCaptureAsync(CaptureSelection selection, CancellationToken token) => transport.SetCaptureAsync(selection, token);
    public async ValueTask DisposeAsync()
    {
        try { await StopAsync().ConfigureAwait(false); }
        finally { await transport.DisposeAsync().ConfigureAwait(false); }
    }
}
