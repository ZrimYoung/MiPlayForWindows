using System.Net;
using DLNACast.Core.Abstractions;
using DLNACast.Core.MiPlay;
using DLNACast.Core.Models;
using MiPlay.Desktop.Services;

namespace MiPlay.Tests;

public sealed class DesktopSessionTests
{
    [Fact]
    public async Task TwentyStartsAndStopsDisposeEverySessionAndDoNotAutostart()
    {
        using var platform = new FakePlatform();
        var sessions = new List<FakeSession>();
        await using var service = Create(platform, new FakeDiscovery(), () => { var s = new FakeSession(); sessions.Add(s); return s; });
        Assert.Equal(DesktopCastState.Idle, service.Status.State);
        for (var i = 0; i < 20; i++)
        {
            await service.StartAsync(SpeakerAt("same", "192.168.1.3"));
            await service.StopAsync();
            Assert.Equal(DesktopCastState.Idle, service.Status.State);
        }
        Assert.Equal(20, sessions.Count);
        Assert.All(sessions, s => { Assert.True(s.Stopped); Assert.True(s.Disposed); });
    }

    [Fact]
    public async Task RetryUsesStableIdentityAndItsNewAddress()
    {
        using var platform = new FakePlatform();
        var discovery = new FakeDiscovery { Speakers = [SpeakerAt("other", "192.168.1.4"), SpeakerAt("same", "192.168.1.9")] };
        var sessions = new List<FakeSession>();
        await using var service = Create(platform, discovery, () => { var s = new FakeSession(); sessions.Add(s); return s; });
        await service.StartAsync(SpeakerAt("same", "192.168.1.3"));
        sessions[0].Fail(MiPlayCastFailureKind.TransportStall);
        await Eventually(() => sessions.Count == 2 && sessions[1].Request is not null);
        Assert.Equal("same", sessions[1].Request!.Renderer.Udn);
        Assert.Equal(IPAddress.Parse("192.168.1.9"), sessions[1].Request!.Renderer.Address);
        Assert.True(sessions[0].Disposed);
    }

    [Fact]
    public async Task MissingOriginalSpeakerRetriesThreeTimesWithoutCastingToAnother()
    {
        using var platform = new FakePlatform();
        var discovery = new FakeDiscovery { Speakers = [SpeakerAt("other", "192.168.1.4")] };
        var session = new FakeSession();
        var created = 0;
        await using var service = Create(platform, discovery, () => { created++; return session; });
        await service.StartAsync(SpeakerAt("same", "192.168.1.3"));
        session.Fail(MiPlayCastFailureKind.TransportStall);
        await Eventually(() => service.Status.State == DesktopCastState.Error);
        Assert.Equal(3, discovery.Calls);
        Assert.Equal(1, created);
    }

    [Theory]
    [InlineData(MiPlayCastFailureKind.ReceiverBusy, "busy", DesktopFailure.Busy)]
    [InlineData(MiPlayCastFailureKind.None, "SafetyInfo/SafetyAuth unsupported", DesktopFailure.Authentication)]
    public async Task BusyAndUnsupportedAuthenticationDoNotRetry(MiPlayCastFailureKind kind, string error, DesktopFailure expected)
    {
        using var platform = new FakePlatform();
        var discovery = new FakeDiscovery();
        var session = new FakeSession();
        await using var service = Create(platform, discovery, () => session);
        await service.StartAsync(SpeakerAt("same", "192.168.1.3"));
        session.Fail(kind, error);
        await Eventually(() => service.Status.State == DesktopCastState.Error);
        Assert.Equal(expected, service.Status.Failure);
        Assert.Equal(0, discovery.Calls);
    }

    [Fact]
    public async Task StopCancelsDiscoveryDuringReconnect()
    {
        using var platform = new FakePlatform();
        var discovery = new FakeDiscovery { WaitForCancellation = true };
        var session = new FakeSession();
        await using var service = Create(platform, discovery, () => session);
        await service.StartAsync(SpeakerAt("same", "192.168.1.3"));
        session.Fail(MiPlayCastFailureKind.TransportStall);
        await Eventually(() => discovery.Calls == 1);
        await service.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(DesktopCastState.Idle, service.Status.State);
        Assert.Equal(1, discovery.Calls);
    }

    [Fact]
    public async Task DefaultOutputChangeRebindsCaptureAndSleepStopsIt()
    {
        using var platform = new FakePlatform();
        var session = new FakeSession();
        await using var service = Create(platform, new FakeDiscovery(), () => session);
        await service.StartAsync(SpeakerAt("same", "192.168.1.3"));
        platform.OutputId = "headphones";
        await Eventually(() => session.Selection is CaptureSelection.SystemMix { EndpointId: "headphones" });
        platform.Sleep();
        await Eventually(() => service.Status.State == DesktopCastState.Idle);
        Assert.True(session.Disposed);
    }

    [Fact]
    public async Task StopDuringStartupCancelsAndDisposes()
    {
        using var platform = new FakePlatform();
        var session = new FakeSession { BlockStartup = true };
        await using var service = Create(platform, new FakeDiscovery(), () => session);
        var starting = service.StartAsync(SpeakerAt("same", "192.168.1.3"));
        await Eventually(() => session.Request is not null);
        await service.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => starting);
        Assert.True(session.Disposed);
    }

    [Fact]
    public async Task StartupPhasesReachTheDesktopAndStopCanCancelThem()
    {
        using var platform = new FakePlatform();
        var session = new FakeSession { BlockStartup = true };
        await using var service = Create(platform, new FakeDiscovery(), () => session);
        var starting = service.StartAsync(SpeakerAt("same", "192.168.1.3"));
        await Eventually(() => session.Request is not null);
        session.Report(new(MiPlayCastState.AwaitingReceiver, "Waiting for RTSP", StartupPhase: "ReverseRtsp"));
        Assert.Equal(DesktopCastState.Connecting, service.Status.State);
        Assert.Equal("ReverseRtsp", service.Status.Diagnostics!.StartupPhase);
        Assert.True(service.Status.Diagnostics.StartupElapsedMilliseconds >= 0);
        await service.StopAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => starting);
        Assert.Equal(DesktopCastState.Idle, service.Status.State);
    }

    [Fact]
    public async Task DelayedReadinessReportCannotResetAnAlreadyStartedSessionToConnecting()
    {
        using var platform = new FakePlatform();
        var session = new FakeSession { ReadyBeforeFirstMedia = true };
        await using var service = Create(platform, new FakeDiscovery(), () => session);
        await service.StartAsync(SpeakerAt("same", "192.168.1.3"));
        Assert.Equal(DesktopCastState.Casting, service.Status.State);
        Assert.True(service.Status.Diagnostics!.StartupElapsedMilliseconds >= 0);
        session.Report(new(MiPlayCastState.AwaitingReceiver, "Late readiness report"));
        Assert.Equal(DesktopCastState.Casting, service.Status.State);
    }

    [Fact]
    public void DiscoveryIsCapabilityBasedAndIdentityDoesNotContainTheIpAddress()
    {
        var records = new Dictionary<string, string> { ["apps"] = "[5]", ["idHash"] = Convert.ToBase64String([1, 2, 3]), ["model"] = "unknown-future-model" };
        var first = SpeakerDiscovery.FromAdvertisement(new("test._mi-connect._udp.local", "Art", null, IPAddress.Parse("192.168.1.3"), 1234, records));
        var second = SpeakerDiscovery.FromAdvertisement(new("test._mi-connect._udp.local", "Pro", null, IPAddress.Parse("192.168.1.9"), 1234, records));
        Assert.NotNull(first); Assert.NotNull(second); Assert.Equal(first.Id, second.Id);
        Assert.Equal(8899, first.Renderer.DescriptionUrl.Port);
        records["apps"] = "[2]";
        Assert.Null(SpeakerDiscovery.FromAdvertisement(new("test", "unrelated", null, IPAddress.Parse("192.168.1.3"), null, records)));
    }

    private static CastService Create(FakePlatform p, FakeDiscovery d, Func<ICastSession> f) => new(p, d, f, () => "test-encoder", TimeSpan.FromMilliseconds(5));
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
    private sealed class FakeDiscovery : ISpeakerDiscovery
    {
        public int Calls;
        public bool WaitForCancellation;
        public IReadOnlyList<Speaker> Speakers = [];
        public async Task<IReadOnlyList<Speaker>> SearchAsync(CancellationToken token)
        {
            Interlocked.Increment(ref Calls);
            if (WaitForCancellation) await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Speakers;
        }
    }
    private sealed class FakePlatform : IDesktopAudioPlatform
    {
        public string OutputId = "default";
        public event EventHandler? Suspending;
        public void Sleep() => Suspending?.Invoke(this, EventArgs.Empty);
        public AudioSourceItem GetDefaultOutputDevice() => new(OutputId, OutputId);
        public IReadOnlyList<AudioSourceItem> GetOutputDevices() => [GetDefaultOutputDevice()];
        public IReadOnlyList<AudioSourceItem> GetCandidateProcesses() => [];
        public IAudioCaptureSource CreateCapture(CaptureSelection selection) => throw new NotSupportedException();
        public IMediaPacingClock CreateMediaClock() => new MonotonicMediaPacingClock();
        public void Dispose() { }
    }
    private sealed class FakeSession : ICastSession
    {
        public MiPlaySystemAudioRequest? Request;
        public CaptureSelection? Selection;
        public bool Stopped, Disposed, BlockStartup, ReadyBeforeFirstMedia;
        public MiPlayCastDiagnostics Diagnostics { get; private set; } = new(MiPlayCastState.Idle, "");
        public event EventHandler<MiPlayCastDiagnostics>? DiagnosticsChanged;
        public async Task StartAsync(MiPlaySystemAudioRequest request, CancellationToken token)
        {
            Request = request;
            if (BlockStartup) await Task.Delay(Timeout.InfiniteTimeSpan, token);
            Diagnostics = new(ReadyBeforeFirstMedia ? MiPlayCastState.AwaitingReceiver : MiPlayCastState.Streaming, "");
            DiagnosticsChanged?.Invoke(this, Diagnostics);
        }
        public void Fail(MiPlayCastFailureKind kind, string text = "connection lost")
        {
            Diagnostics = new(MiPlayCastState.Error, "", FailureKind: kind, LastError: text);
            DiagnosticsChanged?.Invoke(this, Diagnostics);
        }
        public void Report(MiPlayCastDiagnostics diagnostic)
        { Diagnostics = diagnostic; DiagnosticsChanged?.Invoke(this, diagnostic); }
        public Task StopAsync() { Stopped = true; return Task.CompletedTask; }
        public Task SetVolumeAsync(int volume, CancellationToken token) => Task.CompletedTask;
        public Task SetCaptureAsync(CaptureSelection selection, CancellationToken token) { Selection = selection; return Task.CompletedTask; }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
