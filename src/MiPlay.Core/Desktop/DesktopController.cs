using DLNACast.Core.MiPlay;
using DLNACast.Core.Models;
using DLNACast.Core.Audio;
using MiPlay.Desktop.Services.Dlna;

namespace MiPlay.Desktop.Services;

public sealed record SpeakerCard(string Id, string Name, string Model, bool Online, bool Selected,
    string State, string Message, int? Volume, int DelayMilliseconds,
    string GroupId = "", string GroupName = "", string Channel = "", string Failure = "None",
    string Transport = "MiPlay", string ChannelRoute = "Stereo", DesktopDiagnostic? Diagnostics = null);
public sealed record DesktopSnapshot(IReadOnlyList<SpeakerCard> Speakers, bool Scanning,
    string DiscoveryMessage, bool MuteLocalOutput, bool SupportsLocalMute, long Revision = 0,
    bool SynchronizedPlayback = true, int AudioBitRate = MiPlayProtocolConstants.AacBitRate,
    DesktopAudioSource? AudioSource = null, IReadOnlyList<AudioSourceItem>? OutputDevices = null,
    IReadOnlyList<AudioSourceItem>? CandidateProcesses = null, bool DlnaEnabled = false);

/// <summary>UI-independent multi-speaker commands, persistence and continuous discovery.</summary>
public sealed class DesktopController : IAsyncDisposable
{
    private readonly object sync = new();
    private readonly SharedDesktopAudioPlatform platform;
    private readonly ISpeakerDiscovery discovery;
    private readonly DesktopStore store;
    private readonly DesktopCastGroup group;
    private readonly MiPlayMediaState mediaState = new();
    public void UpdateMedia(MiPlayMediaSnapshot? media) => mediaState.Update(media);
    private readonly Func<ICastSession>? sessionFactory;
    private readonly Func<string>? encoderPath;
    private readonly Dictionary<string, Entry> entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Task> topologyStops = [];
    private readonly SemaphoreSlim configurationGate = new(1, 1);
    private readonly SemaphoreSlim scanGate = new(1, 1);
    private readonly SemaphoreSlim audioSourcesGate = new(1, 1);
    private IReadOnlyList<AudioSourceItem> outputDevices = [], candidateProcesses = [];
    private readonly SemaphoreSlim groupVolumeGate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly TimeSpan scanInterval, offlineAfter;
    private readonly Func<DateTimeOffset> now;
    private DesktopSettings settings;
    private Task? discoveryTask;
    private bool scanning, disposed, modeChanging;
    private long revision;
    private string discoveryMessage = "正在查找附近音箱…";
    public event EventHandler<DesktopSnapshot>? Changed;

    public DesktopController(IDesktopAudioPlatform platform, ISpeakerDiscovery discovery, DesktopStore store,
        Func<ICastSession>? sessionFactory = null, Func<string>? encoderPath = null,
        TimeSpan? scanInterval = null, TimeSpan? offlineAfter = null, Func<DateTimeOffset>? now = null)
    {
        this.platform = new(platform); this.discovery = discovery; this.store = store;
        if (discovery is SpeakerDiscovery networkDiscovery)
            networkDiscovery.CanReadIdentity = id => { lock (sync) return !entries.TryGetValue(id, out var e) || !e.Wanted; };
        group = new(this.platform, prepareChannelRoutes: () => { lock (sync) return settings?.Speakers?.Values.Any(p => p.ChannelRoute != "Stereo" && string.IsNullOrEmpty(p.GroupId)) == true; });
        this.sessionFactory = sessionFactory;
        this.encoderPath = encoderPath;
        this.scanInterval = scanInterval ?? TimeSpan.FromSeconds(2);
        this.offlineAfter = offlineAfter ?? TimeSpan.FromSeconds(18);
        this.now = now ?? (() => DateTimeOffset.UtcNow);
        settings = store.Load();
        if (!MiPlayAudioQuality.IsSupported(settings.AudioBitRate)) settings = settings with { AudioBitRate = 256_000 };
        settings = settings with { Speakers = settings.Speakers ?? new(), MuteLocalOutput = settings.MuteLocalOutput && platform.SupportsLocalMute,
            AudioSource = settings.AudioSource is { Mode: "system" } saved ? saved : new() };
        if (discovery is SpeakerDiscovery allDiscovery) allDiscovery.DlnaEnabled = settings.DlnaEnabled;
        foreach (var (id, value) in settings.Speakers)
        {
            var preferences = value with { Volume = value.Volume is { } v ? Math.Clamp(v, 0, 100) : null,
                DelayMilliseconds = Math.Clamp(value.DelayMilliseconds, 0, 2000),
                ChannelRoute = Enum.TryParse<AudioChannelRoute>(value.ChannelRoute, out var route) && Enum.IsDefined(route)
                    && string.IsNullOrEmpty(value.GroupId) ? route.ToString() : "Stereo" };
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
                    e.Status.State == DesktopCastState.Casting ? e.Service.ReceiverVolume ?? e.Preferences.Volume : e.Preferences.Volume,
                    e.Preferences.DelayMilliseconds,
                    e.Preferences.GroupId, e.Preferences.GroupName, e.Preferences.Channel, e.Status.Failure.ToString(),
                    e.Speaker?.Transport ?? e.Preferences.Transport, e.Preferences.ChannelRoute, DesktopDiagnostic.From(e.Status.Diagnostics))).ToArray(),
                    scanning, discoveryMessage, settings.MuteLocalOutput, platform.SupportsLocalMute, ++revision,
                    settings.SynchronizedPlayback, settings.AudioBitRate, settings.AudioSource, outputDevices, candidateProcesses, settings.DlnaEnabled);
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
                    try { await RefreshAudioSourcesAsync().ConfigureAwait(false); }
                    catch (Exception ex) when (!lifetime.IsCancellationRequested) { store.Log("audio-sources", ex.Message); }
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
                    if (speaker.Transport == "DLNA" && !settings.DlnaEnabled) continue;
                    if (!entries.TryGetValue(speaker.Id, out var entry))
                        entries[speaker.Id] = entry = CreateEntry(speaker.Id, new(speaker.Name, speaker.Model, Transport: speaker.Transport));
                    entry.Speaker = speaker; entry.LastSeen = now(); entry.Online = true;
                    entry.Preferences = entry.Preferences with { Name = speaker.Name, Model = speaker.Model, Transport = speaker.Transport };
                    if (speaker.Identity is { } identity) ApplyIdentity(entry, identity);
                }
                foreach (var entry in entries.Values)
                    if (now() - entry.LastSeen > offlineAfter) entry.Online = entry.Status.State == DesktopCastState.Casting;
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
            if (selected && (entry.Speaker?.Transport ?? entry.Preferences.Transport) == "DLNA" && !settings.DlnaEnabled)
                throw new InvalidOperationException("请先在设置中开启 DLNA 设备发现。");
            if (selected && modeChanging) throw new InvalidOperationException("正在切换投放模式，请稍后选择音箱。");
            if (selected && entry.TopologyChanging) throw new InvalidOperationException("组合信息正在更新，请稍后重新选择。");
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

    public async Task SetGroupSelectedAsync(string groupId, bool selected)
    {
        Entry[] members;
        lock (sync)
        {
            if (string.IsNullOrWhiteSpace(groupId)) throw new ArgumentException("未识别到立体声组合。");
            members = entries.Values.Where(e => e.Preferences.GroupId == groupId).ToArray();
            if (members.Length == 0) throw new InvalidOperationException("立体声组合已不在列表中。");
            if (selected && (members.Length != 2 || members.Any(e => !e.Online) ||
                !members.Any(e => e.Preferences.Channel == "left") || !members.Any(e => e.Preferences.Channel == "right")))
                throw new InvalidOperationException("等待立体声组合的左右音箱都上线后再投放。");
            if (selected && members.All(e => !e.Wanted))
            {
                var saved = members.Select(e => e.Preferences.Volume).Where(v => v.HasValue).Select(v => v!.Value).ToArray();
                if (saved.Length > 0 && members.Any(e => e.Preferences.Volume != saved.Min()))
                {
                    // Older versions saved independent levels. Use the lower level on a fresh
                    // group start; never raise volume or change it merely during discovery.
                    foreach (var entry in members)
                    {
                        entry.Preferences = entry.Preferences with { Volume = saved.Min() };
                        settings.Speakers![entry.Id] = entry.Preferences;
                    }
                    SaveSettings();
                }
            }
        }
        await Task.WhenAll(members.Select(e => SetSelectedAsync(e.Id, selected))).ConfigureAwait(false);
    }

    public async Task SetVolumeAsync(string id, int volume)
    {
        if (volume is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(volume));
        var entry = Get(id);
        string groupId; lock (sync) groupId = entry.Preferences.GroupId;
        // A stereo member is never adjusted independently, including through an older UI.
        if (!string.IsNullOrWhiteSpace(groupId))
        {
            await SetGroupVolumeAsync(groupId, volume).ConfigureAwait(false);
            return;
        }
        lock (sync) { entry.Preferences = entry.Preferences with { Volume = volume }; Save(entry); }
        Publish();
        await ApplyVolumeAsync(entry, volume).ConfigureAwait(false);
    }

    public async Task SetGroupVolumeAsync(string groupId, int volume)
    {
        if (volume is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(volume));
        if (string.IsNullOrWhiteSpace(groupId)) throw new ArgumentException("未识别到立体声组合。");
        await groupVolumeGate.WaitAsync(lifetime.Token).ConfigureAwait(false);
        try
        {
            Entry[] members;
            lock (sync)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                members = entries.Values.Where(e => e.Preferences.GroupId == groupId).ToArray();
                if (members.Length == 0) throw new InvalidOperationException("立体声组合已不在列表中。");
                foreach (var entry in members)
                {
                    entry.Preferences = entry.Preferences with { Volume = volume };
                    settings.Speakers![entry.Id] = entry.Preferences;
                }
                SaveSettings();
            }
            Publish();
            // The same absolute volume is applied to every member. A delayed acknowledgement
            // is handled by each service without stopping either audio stream.
            await Task.WhenAll(members.Select(e => ApplyVolumeAsync(e, volume))).ConfigureAwait(false);
        }
        finally { groupVolumeGate.Release(); Publish(); }
    }

    private async Task ApplyVolumeAsync(Entry entry, int volume)
    {
        await entry.Gate.WaitAsync(lifetime.Token).ConfigureAwait(false);
        try { await entry.Service.SetVolumeAsync(volume, lifetime.Token).ConfigureAwait(false); }
        finally { entry.Gate.Release(); }
    }

    public async Task SetDelayAsync(string id, int milliseconds)
    {
        if (milliseconds is < 0 or > 2000) throw new ArgumentOutOfRangeException(nameof(milliseconds));
        var entry = Get(id);
        if ((entry.Speaker?.Transport ?? entry.Preferences.Transport) == "DLNA")
            throw new NotSupportedException("DLNA 不支持 MiPlay 延迟微调。");
        long revision;
        lock (sync)
        {
            if (entry.Preferences.DelayMilliseconds == milliseconds) return;
            entry.Preferences = entry.Preferences with { DelayMilliseconds = milliseconds };
            revision = ++entry.DelayRevision;
            Save(entry);
        }
        Publish();
        await entry.Gate.WaitAsync(lifetime.Token).ConfigureAwait(false);
        try
        {
            // Drop superseded slider requests before sending. A slow receiver must
            // not build a queue of obsolete offsets or repeat the final target.
            lock (sync) { if (revision != entry.DelayRevision) return; }
            await entry.Service.SetDelayAsync(milliseconds, lifetime.Token).ConfigureAwait(false);
        }
        finally { entry.Gate.Release(); Publish(); }
    }
    public async Task SetGroupDelayAsync(string groupId, int milliseconds)
    {
        if (milliseconds is < 0 or > 2000) throw new ArgumentOutOfRangeException(nameof(milliseconds));
        string[] members;
        lock (sync) { members = entries.Values.Where(e => e.Preferences.GroupId == groupId).Select(e => e.Id).ToArray(); }
        if (members.Length == 0) throw new InvalidOperationException("立体声组合已不在列表中。");
        await Task.WhenAll(members.Select(id => SetDelayAsync(id, milliseconds))).ConfigureAwait(false);
    }
    public async Task SetMutedAsync(bool muted)
    {
        await configurationGate.WaitAsync(lifetime.Token).ConfigureAwait(false);
        try
        {
            await platform.SetMutedAsync(muted, lifetime.Token).ConfigureAwait(false);
            lock (sync) { settings = settings with { MuteLocalOutput = muted }; SaveSettings(); }
        }
        finally { configurationGate.Release(); Publish(); }
    }
    public async Task SetSynchronizedPlaybackAsync(bool enabled)
    {
        await configurationGate.WaitAsync(lifetime.Token).ConfigureAwait(false);
        try
        {
            lock (sync)
            {
                if (settings.SynchronizedPlayback == enabled) return;
                modeChanging = true;
            }
            await StopAllAsync().ConfigureAwait(false);
            lock (sync)
            {
                settings = settings with { SynchronizedPlayback = enabled };
                SaveSettings();
            }
            store.Log("playback-mode", enabled ? "shared-timeline" : "legacy-independent");
        }
        finally { lock (sync) modeChanging = false; configurationGate.Release(); Publish(); }
    }
    public async Task SetAudioBitRateAsync(int value)
    {
        MiPlayAudioQuality.Validate(value);
        await configurationGate.WaitAsync(lifetime.Token).ConfigureAwait(false);
        try
        {
            lock (sync) { if (settings.AudioBitRate == value) return; modeChanging = true; }
            await StopAllAsync().ConfigureAwait(false);
            lock (sync) { settings = settings with { AudioBitRate = value }; SaveSettings(); }
        }
        finally { lock (sync) modeChanging = false; configurationGate.Release(); Publish(); }
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
    public async Task RefreshAudioSourcesAsync()
    {
        if (!await audioSourcesGate.WaitAsync(0, lifetime.Token).ConfigureAwait(false)) return;
        try
        {
            var devices = await Task.Run(platform.GetOutputDevices, lifetime.Token).ConfigureAwait(false);
            var processes = await Task.Run(platform.GetCandidateProcesses, lifetime.Token).ConfigureAwait(false);
            lock (sync) { outputDevices = devices; candidateProcesses = processes; }
        }
        finally { audioSourcesGate.Release(); Publish(); }
    }
    public async Task SetAudioSourceAsync(DesktopAudioSource source)
    {
        source.Validate();
        await configurationGate.WaitAsync(lifetime.Token).ConfigureAwait(false);
        try
        {
            // Resolve before stopping: an invalid source must not interrupt healthy playback.
            var selection = await Task.Run(() => source.Resolve(platform), lifetime.Token).ConfigureAwait(false);
            if (selection is CaptureSelection.Process process)
            {
                long? birth = null;
                try { using var running = System.Diagnostics.Process.GetProcessById(process.ProcessId); birth = running.StartTime.ToUniversalTime().Ticks; }
                catch (Exception) { /* Platform capture still verifies liveness before starting. */ }
                source = source with { ProcessName = process.DisplayName, ProcessStartedAtUtcTicks = birth };
            }
            lock (sync) { if (settings.AudioSource == source) return; modeChanging = true; }
            await StopAllAsync().ConfigureAwait(false);
            lock (sync) { settings = settings with { AudioSource = source }; SaveSettings(); }
        }
        finally { lock (sync) modeChanging = false; configurationGate.Release(); Publish(); }
    }
    public async Task SetChannelRouteAsync(string id, string route)
    {
        if (route is not ("Stereo" or "LeftAsMono" or "RightAsMono"))
            throw new ArgumentException("请选择立体声、左声道或右声道。");
        var entry = Get(id);
        await configurationGate.WaitAsync(lifetime.Token).ConfigureAwait(false);
        try
        {
            lock (sync)
            {
                if (!string.IsNullOrEmpty(entry.Preferences.GroupId)) throw new InvalidOperationException("官方立体声组合由音箱管理左右声道。");
                if (entry.Preferences.ChannelRoute == route) return;
                modeChanging = true;
            }
            await StopAllAsync().ConfigureAwait(false);
            lock (sync) { entry.Preferences = entry.Preferences with { ChannelRoute = route }; Save(entry); }
        }
        finally { lock (sync) modeChanging = false; configurationGate.Release(); Publish(); }
    }
    public async Task SetDlnaEnabledAsync(bool enabled)
    {
        await configurationGate.WaitAsync(lifetime.Token).ConfigureAwait(false);
        try
        {
            Entry[] stop;
            lock (sync)
            {
                modeChanging = true;
                stop = enabled ? [] : entries.Values.Where(e => (e.Speaker?.Transport ?? e.Preferences.Transport) == "DLNA").ToArray();
            }
            foreach (var entry in stop) await SetSelectedAsync(entry.Id, false).ConfigureAwait(false);
            lock (sync)
            {
                settings = settings with { DlnaEnabled = enabled }; SaveSettings();
                foreach (var entry in stop) entry.Online = false;
                if (discovery is SpeakerDiscovery network) network.DlnaEnabled = enabled;
            }
        }
        finally { lock (sync) modeChanging = false; configurationGate.Release(); Publish(); }
        await RefreshAsync().ConfigureAwait(false);
    }
    private void SaveSettings() => store.Save(settings with {
        // PIDs are session-local identities, never restore one after an app restart.
        AudioSource = settings.AudioSource is { Mode: "process" } ? new DesktopAudioSource() : settings.AudioSource });

    public string Export() => store.Export(null);

    private ICastSession CreateSession(Speaker speaker)
    {
        lock (sync)
        {
            if (speaker.Transport == "DLNA") return new DlnaCastSession(platform);
            var transport = new NativeCastSession(platform, mediaState);
            return settings.SynchronizedPlayback ? new GroupCastSession(transport, group) : transport;
        }
    }

    private Entry CreateEntry(string id, SpeakerPreferences preferences)
    {
        var service = new CastService(platform, discovery, sessionFactory, encoderPath,
            source: () => { lock (sync) return settings.AudioSource ?? new(); },
            speakerSessionFactory: sessionFactory is null ? CreateSession : null);
        var entry = new Entry(id, preferences, service);
        service.ReceiverVolumeObserved += (_, volume) =>
        {
            lock (sync)
            {
                if (disposed || !entry.Wanted) return;
                if (entry.Preferences.Volume != volume)
                {
                    entry.Preferences = entry.Preferences with { Volume = volume };
                    Save(entry);
                }
            }
            Publish();
        };
        service.ReceiverIdentityObserved += (_, identity) =>
        {
            lock (sync)
            {
                if (disposed || !entry.Wanted) return;
                if (discovery is SpeakerDiscovery network) network.ObserveIdentity(id, identity);
                ApplyIdentity(entry, identity);
            }
            Publish();
        };
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

    // Called under sync. Identity changes never silently move a running route into a new group.
    private void ApplyIdentity(Entry entry, SpeakerIdentity identity)
    {
        var before = entry.Preferences;
        if (!identity.HasGroupInfo) identity = identity with {
            GroupId = before.GroupId, GroupName = before.GroupName, Channel = before.Channel };
        if (!string.IsNullOrEmpty(before.GroupId) && identity.GroupId == before.GroupId &&
            identity.Channel is not ("left" or "right")) identity = identity with { Channel = before.Channel };
        var topologyChanged = before.GroupId != identity.GroupId ||
            (!string.IsNullOrEmpty(before.GroupId) && before.Channel != identity.Channel);
        var affected = topologyChanged ? entries.Values.Where(e => ReferenceEquals(e, entry) ||
            (!string.IsNullOrEmpty(before.GroupId) && e.Preferences.GroupId == before.GroupId)).ToArray() : [];
        entry.Preferences = before with {
            Name = string.IsNullOrWhiteSpace(identity.Name) ? before.Name : identity.Name,
            Model = string.IsNullOrWhiteSpace(identity.Model) ? before.Model : identity.Model,
            GroupId = identity.GroupId, GroupName = identity.GroupName, Channel = identity.Channel,
            ChannelRoute = string.IsNullOrEmpty(identity.GroupId) ? before.ChannelRoute : "Stereo" };
        if (entry.Speaker is { } speaker) entry.Speaker = speaker with { Identity = identity, Name = entry.Preferences.Name, Model = entry.Preferences.Model };
        if (entry.Preferences != before) Save(entry);
        if (topologyChanged && discovery is SpeakerDiscovery network)
            foreach (var member in affected) network.InvalidateIdentity(member.Id);
        var stopping = affected.Where(e => e.Wanted && !e.TopologyChanging).ToArray();
        if (stopping.Length == 0) return;
        foreach (var member in stopping)
        {
            member.Wanted = false; member.TopologyChanging = true; member.StartCancellation?.Cancel();
        }
        topologyStops.RemoveAll(t => t.IsCompleted);
        topologyStops.Add(Task.Run(async () =>
        {
            foreach (var member in stopping)
            {
                await member.Gate.WaitAsync().ConfigureAwait(false);
                try { await member.Service.StopAsync("组合信息已变化，请重新选择音箱。").ConfigureAwait(false); }
                catch (Exception ex) { store.Log("group-change", ex.Message); }
                finally
                {
                    lock (sync) member.TopologyChanging = false;
                    member.Gate.Release();
                }
            }
            Publish();
        }));
    }
    private async Task StartEntryAsync(Entry entry)
    {
        CancellationToken token;
        Speaker speaker;
        int delay, audioBitRate;
        AudioChannelRoute route;
        lock (sync)
        {
            entry.StartCancellation?.Dispose();
            entry.StartCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            if (!entry.Wanted) return;
            token = entry.StartCancellation.Token;
            speaker = entry.Speaker ?? throw new InvalidOperationException("等待音箱重新上线。");
            delay = entry.Preferences.DelayMilliseconds;
            audioBitRate = settings.AudioBitRate;
            route = Enum.Parse<AudioChannelRoute>(entry.Preferences.ChannelRoute);
            Save(entry);
        }
        try
        {
            int? volume; lock (sync) volume = entry.Preferences.Volume;
            if (volume is { } value) await entry.Service.SetVolumeAsync(value, token).ConfigureAwait(false);
            await entry.Service.StartAsync(speaker, token, delay, audioBitRate, route).ConfigureAwait(false);
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
    private void Save(Entry entry) { settings.Speakers![entry.Id] = entry.Preferences; SaveSettings(); }
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
        await audioSourcesGate.WaitAsync().ConfigureAwait(false); audioSourcesGate.Release();
        Task[] topology; lock (sync) topology = topologyStops.ToArray();
        await Task.WhenAll(topology).ConfigureAwait(false);
        foreach (var entry in entries.Values) { await entry.Service.DisposeAsync().ConfigureAwait(false); entry.StartCancellation?.Dispose(); }
        await group.DisposeAsync().ConfigureAwait(false);
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
        public bool Online, Wanted, TopologyChanging;
        public DateTimeOffset LastSeen;
        public long DelayRevision;
        public CastStatus Status = new(DesktopCastState.Idle, "点选加入投放");
        public SemaphoreSlim Gate = new(1, 1);
        public CancellationTokenSource? StartCancellation;
    }
}
