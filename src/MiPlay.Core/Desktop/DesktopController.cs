namespace MiPlay.Desktop.Services;

public sealed record SpeakerCard(string Id, string Name, string Model, bool Online, bool Selected,
    string State, string Message, int? Volume, int DelayMilliseconds);
public sealed record DesktopSnapshot(IReadOnlyList<SpeakerCard> Speakers, bool Scanning,
    string DiscoveryMessage, bool MuteLocalOutput, bool SupportsLocalMute, long Revision = 0);

/// <summary>UI-independent multi-speaker commands, persistence and continuous discovery.</summary>
public sealed class DesktopController : IAsyncDisposable
{
    private readonly object sync = new();
    private readonly SharedDesktopAudioPlatform platform;
    private readonly ISpeakerDiscovery discovery;
    private readonly DesktopStore store;
    private readonly Func<ICastSession>? sessionFactory;
    private readonly Func<string>? encoderPath;
    private readonly Dictionary<string, Entry> entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim configurationGate = new(1, 1);
    private readonly SemaphoreSlim scanGate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly TimeSpan scanInterval, offlineAfter;
    private readonly Func<DateTimeOffset> now;
    private DesktopSettings settings;
    private Task? discoveryTask;
    private bool scanning, disposed;
    private long revision;
    private string discoveryMessage = "正在查找附近音箱…";
    public event EventHandler<DesktopSnapshot>? Changed;

    public DesktopController(IDesktopAudioPlatform platform, ISpeakerDiscovery discovery, DesktopStore store,
        Func<ICastSession>? sessionFactory = null, Func<string>? encoderPath = null,
        TimeSpan? scanInterval = null, TimeSpan? offlineAfter = null, Func<DateTimeOffset>? now = null)
    {
        this.platform = new(platform); this.discovery = discovery; this.store = store;
        this.sessionFactory = sessionFactory; this.encoderPath = encoderPath;
        this.scanInterval = scanInterval ?? TimeSpan.FromSeconds(2);
        this.offlineAfter = offlineAfter ?? TimeSpan.FromSeconds(18);
        this.now = now ?? (() => DateTimeOffset.UtcNow);
        settings = store.Load();
        settings = settings with { Speakers = settings.Speakers ?? new(), MuteLocalOutput = settings.MuteLocalOutput && platform.SupportsLocalMute };
        foreach (var (id, value) in settings.Speakers)
        {
            var preferences = value with { Volume = value.Volume is { } v ? Math.Clamp(v, 0, 100) : null,
                DelayMilliseconds = Math.Clamp(value.DelayMilliseconds, 0, 2000) };
            entries[id] = CreateEntry(id, preferences);
        }
        platform.Suspending += OnSuspending;
        this.platform.SetMutedAsync(settings.MuteLocalOutput).GetAwaiter().GetResult();
    }

    public DesktopSnapshot Snapshot
    {
        get
        {
            lock (sync)
                return new(entries.Values.Select(e => new SpeakerCard(e.Id, e.Preferences.Name, e.Preferences.Model,
                    e.Online, e.Wanted, e.Status.State.ToString(), e.Status.Message,
                    e.Preferences.Volume ?? e.Service.ReceiverVolume, e.Preferences.DelayMilliseconds)).ToArray(),
                    scanning, discoveryMessage, settings.MuteLocalOutput, platform.SupportsLocalMute, ++revision);
        }
    }
    public void StartDiscovery()
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            discoveryTask ??= Task.Run(async () =>
            {
                try
                {
                    while (!lifetime.IsCancellationRequested)
                    {
                        await RefreshAsync().ConfigureAwait(false);
                        await Task.Delay(scanInterval, lifetime.Token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            });
        }
    }
    public async Task RefreshAsync()
    {
        if (!await scanGate.WaitAsync(0, lifetime.Token).ConfigureAwait(false)) return;
        try
        {
            lock (sync) scanning = true;
            Publish();
            var found = await discovery.SearchAsync(lifetime.Token).ConfigureAwait(false);
            lock (sync)
            {
                foreach (var speaker in found)
                {
                    if (!entries.TryGetValue(speaker.Id, out var entry))
                        entries[speaker.Id] = entry = CreateEntry(speaker.Id, new(speaker.Name, speaker.Model));
                    entry.Speaker = speaker; entry.LastSeen = now(); entry.Online = true;
                    entry.Preferences = entry.Preferences with { Name = speaker.Name, Model = speaker.Model };
                }
                foreach (var entry in entries.Values)
                    if (now() - entry.LastSeen > offlineAfter) entry.Online = false;
                var count = entries.Values.Count(e => e.Online);
                discoveryMessage = count > 0 ? $"{count} 台音箱在线 · 自动发现中" : "正在持续查找 · 请将音箱与电脑连接到同一网络";
                // Discovery metadata alone need not write the settings on every scan.
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            lock (sync) discoveryMessage = "暂时无法扫描，稍后自动重试";
            store.Log("discovery", ex.Message);
        }
        finally { lock (sync) scanning = false; scanGate.Release(); Publish(); }
    }

    public async Task SetSelectedAsync(string id, bool selected)
    {
        var entry = Get(id);
        lock (sync)
        {
            if (selected && !entry.Online) throw new InvalidOperationException("音箱离线，等待重新发现后再试。");
            entry.Wanted = selected;
            if (!selected) entry.StartCancellation?.Cancel();
        }
        Publish();
        await entry.Gate.WaitAsync(lifetime.Token).ConfigureAwait(false);
        try
        {
            if (!entry.Wanted) await entry.Service.StopAsync().ConfigureAwait(false);
            else if (entry.Service.Status.State is DesktopCastState.Idle or DesktopCastState.Error)
                await StartEntryAsync(entry).ConfigureAwait(false);
        }
        finally { entry.Gate.Release(); Publish(); }
    }

    public async Task SetVolumeAsync(string id, int volume)
    {
        if (volume is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(volume));
        var entry = Get(id);
        lock (sync) { entry.Preferences = entry.Preferences with { Volume = volume }; Save(entry); }
        Publish();
        await entry.Gate.WaitAsync(lifetime.Token).ConfigureAwait(false);
        try { await entry.Service.SetVolumeAsync(volume, lifetime.Token).ConfigureAwait(false); }
        finally { entry.Gate.Release(); }
    }

    public async Task SetDelayAsync(string id, int milliseconds)
    {
        if (milliseconds is < 0 or > 2000) throw new ArgumentOutOfRangeException(nameof(milliseconds));
        var entry = Get(id);
        lock (sync)
        {
            if (entry.Preferences.DelayMilliseconds == milliseconds) return;
            entry.Preferences = entry.Preferences with { DelayMilliseconds = milliseconds };
            Save(entry);
            // Cancel a pending handshake immediately, rather than waiting for its timeout.
            entry.StartCancellation?.Cancel();
        }
        Publish();
        await entry.Gate.WaitAsync(lifetime.Token).ConfigureAwait(false);
        try
        {
            if (!entry.Wanted) return;
            await entry.Service.StopAsync("正在应用新的延迟…").ConfigureAwait(false);
            if (entry.Wanted) await StartEntryAsync(entry).ConfigureAwait(false);
        }
        finally { entry.Gate.Release(); Publish(); }
    }
    public async Task SetMutedAsync(bool muted)
    {
        await configurationGate.WaitAsync(lifetime.Token).ConfigureAwait(false);
        try
        {
            await platform.SetMutedAsync(muted, lifetime.Token).ConfigureAwait(false);
            lock (sync) { settings = settings with { MuteLocalOutput = muted }; store.Save(settings); }
        }
        finally { configurationGate.Release(); Publish(); }
    }
    public async Task StopAllAsync()
    {
        Entry[] all;
        lock (sync)
        {
            all = entries.Values.ToArray();
            foreach (var entry in all) { entry.Wanted = false; entry.StartCancellation?.Cancel(); }
        }
        Publish();
        await Task.WhenAll(all.Select(async entry =>
        {
            await entry.Gate.WaitAsync().ConfigureAwait(false);
            try { await entry.Service.StopAsync().ConfigureAwait(false); }
            finally { entry.Gate.Release(); }
        })).ConfigureAwait(false);
    }
    public string Export() => store.Export(null);

    private Entry CreateEntry(string id, SpeakerPreferences preferences)
    {
        var service = new CastService(platform, discovery, sessionFactory, encoderPath);
        var entry = new Entry(id, preferences, service);
        service.StatusChanged += (_, status) =>
        {
            lock (sync)
            {
                entry.Status = status;
                if (status.State == DesktopCastState.Error) entry.Wanted = false;
            }
            store.Log("speaker", $"{id}: {status.Message}", status);
            Publish();
        };
        return entry;
    }
    private async Task StartEntryAsync(Entry entry)
    {
        CancellationToken token;
        Speaker speaker;
        int delay;
        lock (sync)
        {
            entry.StartCancellation?.Dispose();
            entry.StartCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            if (!entry.Wanted) return;
            token = entry.StartCancellation.Token;
            speaker = entry.Speaker ?? throw new InvalidOperationException("等待音箱重新上线。");
            delay = entry.Preferences.DelayMilliseconds;
            Save(entry);
        }
        try
        {
            int? volume; lock (sync) volume = entry.Preferences.Volume;
            if (volume is { } value) await entry.Service.SetVolumeAsync(value, token).ConfigureAwait(false);
            await entry.Service.StartAsync(speaker, token, delay).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { store.Log("start", ex.Message); }
    }
    private Entry Get(string id)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return entries.TryGetValue(id, out var entry) ? entry : throw new KeyNotFoundException("未发现这台音箱。");
        }
    }
    private void Save(Entry entry) { settings.Speakers![entry.Id] = entry.Preferences; store.Save(settings); }
    private void Publish() => Changed?.Invoke(this, Snapshot);
    private async void OnSuspending(object? sender, EventArgs args)
    {
        try { await StopAllAsync().ConfigureAwait(false); }
        catch (Exception ex) { store.Log("sleep", ex.Message); }
    }
    public async ValueTask DisposeAsync()
    {
        lock (sync) { if (disposed) return; disposed = true; }
        platform.Suspending -= OnSuspending;
        lifetime.Cancel();
        await StopAllAsync().ConfigureAwait(false);
        if (discoveryTask is not null) await discoveryTask.ConfigureAwait(false);
        await scanGate.WaitAsync().ConfigureAwait(false); scanGate.Release();
        foreach (var entry in entries.Values) { await entry.Service.DisposeAsync().ConfigureAwait(false); entry.StartCancellation?.Dispose(); }
        await configurationGate.WaitAsync().ConfigureAwait(false);
        try { await platform.DisposeAsync().ConfigureAwait(false); }
        finally { configurationGate.Release(); }
        lifetime.Dispose();
    }
    private sealed class Entry(string id, SpeakerPreferences preferences, CastService service)
    {
        public string Id = id;
        public SpeakerPreferences Preferences = preferences;
        public CastService Service = service;
        public Speaker? Speaker;
        public bool Online, Wanted;
        public DateTimeOffset LastSeen;
        public CastStatus Status = new(DesktopCastState.Idle, "点选加入投放");
        public SemaphoreSlim Gate = new(1, 1);
        public CancellationTokenSource? StartCancellation;
    }
}
