using System.Collections.Concurrent;
using System.Net;
using DLNACast.Core.Abstractions;
using DLNACast.Core.Audio;
using DLNACast.Core.MiPlay;
using DLNACast.Core.Models;
using MiPlay.Desktop;
using MiPlay.Desktop.Services;

namespace MiPlay.Tests;

public sealed class MultiSpeakerTests
{
    [Fact]
    public async Task IndependentVolumeAndDelaySurviveRestartAndAddressChanges()
    {
        using var fixture = new Fixture();
        await using (var controller = fixture.Create())
        {
            await controller.RefreshAsync();
            await controller.SetSelectedAsync("a", true);
            await controller.SetSelectedAsync("b", true);
            var firstA = fixture.Sessions.Single(s => s.Request!.Renderer.Udn == "a");
            var firstB = fixture.Sessions.Single(s => s.Request!.Renderer.Udn == "b");
            await controller.SetVolumeAsync("a", 25);
            await controller.SetVolumeAsync("b", 61);
            await controller.SetDelayAsync("a", 200);
            Assert.True(firstA.Disposed);
            Assert.False(firstB.Disposed);
            Assert.Equal(61, firstB.Volume);
            var latest = fixture.Sessions.Last();
            Assert.Equal(200_000, latest.Request!.PlaybackDelayMicroseconds);
            Assert.Equal(25, latest.Volume);
            Assert.All(controller.Snapshot.Speakers, s => Assert.True(s.Selected));
            await controller.SetSelectedAsync("a", false);
            Assert.False(firstB.Disposed);
        }
        var count = fixture.Sessions.Count;
        fixture.Discovery.Speakers = [SpeakerAt("a", "192.0.2.99"), SpeakerAt("b", "192.0.2.3")];
        await using var reopened = fixture.Create();
        await reopened.RefreshAsync();
        Assert.All(reopened.Snapshot.Speakers, s => Assert.False(s.Selected));
        Assert.Equal(count, fixture.Sessions.Count);
        Assert.Equal(200, reopened.Snapshot.Speakers.Single(s => s.Id == "a").DelayMilliseconds);
        await reopened.SetSelectedAsync("a", true);
        Assert.Equal(IPAddress.Parse("192.0.2.99"), fixture.Sessions.Last().Request!.Renderer.Address);
        Assert.Equal(25, fixture.Sessions.Last().Volume);
    }

    [Fact]
    public async Task StopCancelsPendingJoinAndDoesNotRestartAfterDelayUpdate()
    {
        using var fixture = new Fixture { BlockStarts = true };
        await using var controller = fixture.Create();
        await controller.RefreshAsync();
        var joining = controller.SetSelectedAsync("a", true);
        await Eventually(() => fixture.Sessions.Count == 1);
        var updating = controller.SetDelayAsync("a", 300);
        var stopping = controller.SetSelectedAsync("a", false);
        await Task.WhenAll(joining, updating, stopping).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.All(fixture.Sessions, s => Assert.True(s.Disposed));
        Assert.False(controller.Snapshot.Speakers.Single(s => s.Id == "a").Selected);
        Assert.Equal(300, controller.Snapshot.Speakers.Single(s => s.Id == "a").DelayMilliseconds);
    }

    [Fact]
    public async Task SleepClearsSelectionWithoutAutoconnecting()
    {
        using var fixture = new Fixture();
        await using var controller = fixture.Create();
        await controller.RefreshAsync();
        await controller.SetSelectedAsync("a", true);
        await controller.SetSelectedAsync("b", true);
        fixture.Platform.Sleep();
        await Eventually(() => fixture.Sessions.All(s => s.Disposed));
        Assert.All(controller.Snapshot.Speakers, s => Assert.False(s.Selected));
        await controller.RefreshAsync();
        Assert.Equal(2, fixture.Sessions.Count);
    }

    [Fact]
    public async Task DiscoveryRetainsOrderAndSettingsAcrossMissedScansAndExpiresOffline()
    {
        using var fixture = new Fixture();
        var now = DateTimeOffset.UtcNow;
        await using var controller = fixture.Create(() => now);
        await controller.RefreshAsync();
        await controller.SetDelayAsync("a", 400);
        fixture.Discovery.Speakers = [];
        now += TimeSpan.FromSeconds(5);
        await controller.RefreshAsync();
        Assert.All(controller.Snapshot.Speakers, s => Assert.True(s.Online));
        now += TimeSpan.FromSeconds(20);
        await controller.RefreshAsync();
        Assert.All(controller.Snapshot.Speakers, s => Assert.False(s.Online));
        fixture.Discovery.Speakers = [SpeakerAt("b", "192.0.2.3"), SpeakerAt("a", "192.0.2.99")];
        await controller.RefreshAsync();
        Assert.Equal(new[] { "a", "b" }, controller.Snapshot.Speakers.Select(s => s.Id));
        Assert.Equal(400, controller.Snapshot.Speakers[0].DelayMilliseconds);
        Assert.All(controller.Snapshot.Speakers, s => Assert.True(s.Online));
        Assert.Empty(fixture.Sessions);
    }

    [Fact]
    public async Task ContinuousDiscoveryCanBeCanceledWithoutStartingAudio()
    {
        using var fixture = new Fixture();
        var controller = fixture.Create();
        controller.StartDiscovery();
        await Eventually(() => fixture.Discovery.Calls >= 3);
        await controller.DisposeAsync();
        var calls = fixture.Discovery.Calls;
        await Task.Delay(40);
        Assert.Equal(calls, fixture.Discovery.Calls);
        Assert.Empty(fixture.Platform.Captures);
    }

    [Fact]
    public async Task TwoSubscribersOwnOneTapAndMuteLeaseEndsWithLastSubscriber()
    {
        using var physical = new FakePlatform();
        await using var hub = new SharedDesktopAudioPlatform(physical);
        await hub.SetMutedAsync(true);
        await using var a = hub.CreateCapture(new CaptureSelection.SystemMix("default", "default"));
        await using var b = hub.CreateCapture(new CaptureSelection.SystemMix("default", "default"));
        await using var aBuffer = new PcmFrameBuffer(AudioChannelRoute.Stereo, PcmFrameFormat.MiPlayAac);
        await using var bBuffer = new PcmFrameBuffer(AudioChannelRoute.Stereo, PcmFrameFormat.MiPlayAac);
        using var firstLifetime = new CancellationTokenSource();
        await a.StartAsync(aBuffer, firstLifetime.Token);
        await b.StartAsync(bBuffer, default);
        Assert.Single(physical.Captures);
        Assert.True(((CaptureSelection.SystemMix)physical.Captures[0].Selection).MuteLocalOutput);
        var input = physical.Captures[0].Destination!;
        var bytes = new byte[input.FrameByteCount]; bytes[0] = 42;
        input.Write(bytes, 123456);
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
        {
            async Task<PcmFrameRead> ReadSignal(PcmFrameBuffer destination)
            {
                while (true)
                {
                    var frame = await destination.ReadFrameWithTimingOrSilenceAsync(timeout.Token);
                    if (frame.Bytes[0] == 42) return frame;
                }
            }
            var frames = await Task.WhenAll(ReadSignal(aBuffer), ReadSignal(bBuffer));
            Assert.All(frames, frame => Assert.Equal(123456UL, frame.PresentationTimeMicroseconds));
        }
        firstLifetime.Cancel();
        await a.StopAsync();
        Assert.False(physical.Captures[0].Stopped);
        Assert.True(b.IsRunning);
        await b.StopAsync();
        Assert.True(physical.Captures[0].Stopped);
    }

    [Fact]
    public async Task MutingAndChangingOutputRebindOneTapAndCaptureFailuresReachAllReceivers()
    {
        using var physical = new FakePlatform();
        await using var hub = new SharedDesktopAudioPlatform(physical);
        await using var a = hub.CreateCapture(new CaptureSelection.SystemMix("default", "default"));
        await using var b = hub.CreateCapture(new CaptureSelection.SystemMix("default", "default"));
        await using var aBuffer = new PcmFrameBuffer(AudioChannelRoute.Stereo, PcmFrameFormat.MiPlayAac);
        await using var bBuffer = new PcmFrameBuffer(AudioChannelRoute.Stereo, PcmFrameFormat.MiPlayAac);
        await a.StartAsync(aBuffer, default); await b.StartAsync(bBuffer, default);
        await hub.SetMutedAsync(true);
        Assert.Equal(2, physical.Captures.Count);
        Assert.True(physical.Captures[0].Stopped);
        await using var replacement = hub.CreateCapture(new CaptureSelection.SystemMix("headphones", "headphones"));
        await replacement.StartAsync(aBuffer, default);
        Assert.Equal(3, physical.Captures.Count);
        Assert.True(physical.Captures[1].Stopped);
        Assert.True(((CaptureSelection.SystemMix)physical.Captures[2].Selection).MuteLocalOutput);
        var failures = 0;
        a.CaptureFailed += (_, _) => failures++;
        b.CaptureFailed += (_, _) => failures++;
        physical.Captures[2].Fail();
        Assert.Equal(2, failures);
    }

    [Fact]
    public async Task MutingFailureDoesNotPersistAFalseSuccess()
    {
        using var fixture = new Fixture();
        fixture.Platform.SupportsLocalMute = false;
        await using var controller = fixture.Create();
        await Assert.ThrowsAsync<NotSupportedException>(() => controller.SetMutedAsync(true));
        Assert.False(controller.Snapshot.MuteLocalOutput);
        Assert.False(fixture.Store.Load().MuteLocalOutput);
    }

    private static async Task Eventually(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(5, timeout.Token);
    }
    private static Speaker SpeakerAt(string id, string ip)
    {
        var uri = new Uri($"http://{ip}:8899/");
        var endpoint = new UpnpServiceEndpoint("", uri, uri, uri);
        return new(id, id, "", new(id, id, "Xiaomi", "", IPAddress.Parse(ip), uri, endpoint, endpoint, endpoint, ""));
    }
    private sealed class Fixture : IDisposable
    {
        private readonly DirectoryInfo directory = Directory.CreateTempSubdirectory("miplay-multi-");
        public readonly FakePlatform Platform = new();
        public readonly FakeDiscovery Discovery = new() { Speakers = [SpeakerAt("a", "192.0.2.2"), SpeakerAt("b", "192.0.2.3")] };
        public readonly ConcurrentQueue<FakeSession> Sessions = new();
        public bool BlockStarts;
        public DesktopStore Store => new(directory.FullName);
        public DesktopController Create(Func<DateTimeOffset>? now = null) => new(Platform, Discovery, Store,
            () => { var session = new FakeSession { BlockStartup = BlockStarts }; Sessions.Enqueue(session); return session; },
            () => "test", TimeSpan.FromMilliseconds(10), now: now);
        public void Dispose() { Platform.Dispose(); directory.Delete(true); }
    }
    private sealed class FakeDiscovery : ISpeakerDiscovery
    {
        public int Calls;
        public IReadOnlyList<Speaker> Speakers = [];
        public Task<IReadOnlyList<Speaker>> SearchAsync(CancellationToken token) { Interlocked.Increment(ref Calls); return Task.FromResult(Speakers); }
    }
    private sealed class FakePlatform : IDesktopAudioPlatform
    {
        public bool SupportsLocalMute { get; set; } = true;
        public List<FakeCapture> Captures = [];
        public event EventHandler? Suspending;
        public void Sleep() => Suspending?.Invoke(this, EventArgs.Empty);
        public AudioSourceItem GetDefaultOutputDevice() => new("default", "default");
        public IReadOnlyList<AudioSourceItem> GetOutputDevices() => [];
        public IReadOnlyList<AudioSourceItem> GetCandidateProcesses() => [];
        public IAudioCaptureSource CreateCapture(CaptureSelection selection) { var source = new FakeCapture(selection); Captures.Add(source); return source; }
        public IMediaPacingClock CreateMediaClock() => new MonotonicMediaPacingClock();
        public void Dispose() { }
    }
    private sealed class FakeCapture(CaptureSelection selection) : IAudioCaptureSource
    {
        public CaptureSelection Selection => selection;
        public bool Stopped;
        public PcmFrameBuffer? Destination;
        public bool IsRunning => !Stopped;
        public CaptureHealth Health => new(DateTimeOffset.UtcNow, null, null, 0);
        public event EventHandler<Exception>? CaptureFailed;
        public void Fail() => CaptureFailed?.Invoke(this, new AudioCaptureException("lost device"));
        public Task StartAsync(PcmFrameBuffer destination, CancellationToken token) { Destination = destination; return Task.CompletedTask; }
        public Task StopAsync() { Stopped = true; return Task.CompletedTask; }
        public ValueTask DisposeAsync() { Stopped = true; return ValueTask.CompletedTask; }
    }
    private sealed class FakeSession : ICastSession
    {
        public MiPlaySystemAudioRequest? Request;
        public bool Disposed, BlockStartup;
        public int? Volume;
        public MiPlayCastDiagnostics Diagnostics { get; private set; } = new(MiPlayCastState.Idle, "");
        public event EventHandler<MiPlayCastDiagnostics>? DiagnosticsChanged;
        public async Task StartAsync(MiPlaySystemAudioRequest request, CancellationToken token)
        {
            Request = request;
            if (BlockStartup) await Task.Delay(Timeout.InfiniteTimeSpan, token);
            Diagnostics = new(MiPlayCastState.Streaming, ""); DiagnosticsChanged?.Invoke(this, Diagnostics);
        }
        public Task StopAsync() => Task.CompletedTask;
        public Task SetVolumeAsync(int volume, CancellationToken token) { Volume = volume; return Task.CompletedTask; }
        public Task SetCaptureAsync(CaptureSelection selection, CancellationToken token) => Task.CompletedTask;
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
