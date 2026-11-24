using System.Net;
using DLNACast.Core.Abstractions;
using DLNACast.Core.Audio;
using DLNACast.Core.MiPlay;
using DLNACast.Core.Models;
using MiPlay.Desktop;
using MiPlay.Desktop.Services;

namespace MiPlay.Tests;

public sealed class AudioRoutingDesktopTests
{
    [Fact]
    public async Task SourceChangesStopAllAndFixedDeviceAndApplicationReachSession()
    {
        using var f = new Fixture(); await using var c = f.Create();
        await c.RefreshAsync(); await c.SetSelectedAsync("a", true);
        await c.SetAudioSourceAsync(new(OutputDeviceId: "usb"));
        Assert.False(c.Snapshot.Speakers.Single().Selected);
        Assert.True(f.Sessions[0].Stopped);
        await c.SetSelectedAsync("a", true);
        Assert.Equal("usb", Assert.IsType<CaptureSelection.SystemMix>(f.Sessions[^1].Request!.Selection).EndpointId);
        Assert.Equal("usb", f.Store.Load().AudioSource!.OutputDeviceId);
        await c.SetAudioSourceAsync(new("process", ProcessId: Environment.ProcessId));
        await c.SetSelectedAsync("a", true);
        Assert.Equal(Environment.ProcessId, Assert.IsType<CaptureSelection.Process>(f.Sessions[^1].Request!.Selection).ProcessId);
        Assert.Equal("system", f.Store.Load().AudioSource!.Mode); // Never restore a stale PID.
        await c.SetMutedAsync(true); // A later preference write must not leak the PID either.
        Assert.Null(f.Store.Load().AudioSource!.ProcessId);
    }
    [Fact]
    public async Task PausedSelectedApplicationCanJoinWithoutBeingInActiveCandidateList()
    {
        using var f = new Fixture(); f.Discovery.Items = [Fixture.Speaker, Fixture.Speaker with { Id = "b" }];
        await using var c = f.Create(); await c.RefreshAsync();
        await c.SetAudioSourceAsync(new("process", ProcessId: Environment.ProcessId));
        await c.SetSelectedAsync("a", true);
        f.Platform.ActiveProcesses = false;
        await c.SetSelectedAsync("b", true);
        Assert.Equal(2, f.Sessions.Count);
        Assert.Equal(f.Sessions[0].Request!.Selection, f.Sessions[1].Request!.Selection);
        Assert.All(c.Snapshot.Speakers, s => Assert.True(s.Selected));
    }
    [Fact]
    public async Task InvalidSourceDoesNotInterruptCurrentPlaybackOrFallBackToMix()
    {
        using var f = new Fixture(); await using var c = f.Create();
        await c.RefreshAsync(); await c.SetSelectedAsync("a", true);
        await Assert.ThrowsAsync<AudioCaptureException>(() => c.SetAudioSourceAsync(new("process", ProcessId: int.MaxValue)));
        await Assert.ThrowsAsync<AudioCaptureException>(() => c.SetAudioSourceAsync(new(OutputDeviceId: "gone")));
        await Assert.ThrowsAsync<ArgumentException>(() => c.SetAudioSourceAsync(new("invalid")));
        Assert.True(c.Snapshot.Speakers.Single().Selected);
        Assert.False(f.Sessions[0].Stopped);
        Assert.Single(f.Sessions);
    }
    [Fact]
    public async Task ManualRoutePersistsAndOfficialPairCannotBeOverridden()
    {
        using var f = new Fixture(); await using var c = f.Create();
        await c.RefreshAsync(); await c.SetSelectedAsync("a", true);
        await c.SetChannelRouteAsync("a", "LeftAsMono");
        Assert.True(f.Sessions[0].Stopped);
        Assert.Equal("LeftAsMono", f.Store.Load().Speakers!["a"].ChannelRoute);
        await c.SetSelectedAsync("a", true);
        Assert.Equal(AudioChannelRoute.LeftAsMono, f.Sessions[^1].Request!.ChannelRoute);
        f.Discovery.Items = [Fixture.Speaker with { Identity = new("Pair", "", "group", "Stereo", "left", true) }];
        await c.RefreshAsync();
        Assert.Equal("Stereo", c.Snapshot.Speakers.Single().ChannelRoute);
        await Assert.ThrowsAsync<InvalidOperationException>(() => c.SetChannelRouteAsync("a", "RightAsMono"));
    }
    [Fact]
    public async Task DiagnosticsAreExposedAndDlnaNeverAcceptsMiPlayDelay()
    {
        using var f = new Fixture(); f.Discovery.Items = [Fixture.Speaker with { Transport = "DLNA" }];
        await using var c = f.Create(); await c.RefreshAsync();
        await Assert.ThrowsAsync<KeyNotFoundException>(() => c.SetSelectedAsync("a", true));
        await c.SetDlnaEnabledAsync(true); await c.SetSelectedAsync("a", true);
        await Assert.ThrowsAsync<NotSupportedException>(() => c.SetDelayAsync("a", 500));
        Assert.Equal("DLNA", c.Snapshot.Speakers.Single().Transport);
        Assert.Equal(12, c.Snapshot.Speakers.Single().Diagnostics!.BufferedMilliseconds);
        await c.SetDlnaEnabledAsync(false);
        Assert.True(f.Sessions.Single().Stopped);
        Assert.False(c.Snapshot.Speakers.Single().Online);
    }
    [Fact]
    public async Task SelectedApplicationMuteLeaseReleasesWithLastSubscriber()
    {
        using var p = new Platform(); await using var shared = new SharedDesktopAudioPlatform(p);
        await using var b = new PcmFrameBuffer(AudioChannelRoute.Stereo, PcmFrameFormat.MiPlayAac);
        await shared.SetMutedAsync(true);
        await using var tap = shared.CreateCapture(new CaptureSelection.Process(Environment.ProcessId, "test"));
        await tap.StartAsync(b, default);
        Assert.True(Assert.IsType<CaptureSelection.Process>(p.LastCapture!.Selection).MuteLocalOutput);
        await shared.SetMutedAsync(false);
        Assert.False(Assert.IsType<CaptureSelection.Process>(p.LastCapture!.Selection).MuteLocalOutput);
        await tap.StopAsync(); Assert.True(p.LastCapture.Stopped);
    }

    [Fact]
    public async Task SharedTapTransfersRealCaptureWithoutInventingSilenceBeforeItArrives()
    {
        using var p = new Platform(); await using var shared = new SharedDesktopAudioPlatform(p);
        await using var destination = new PcmFrameBuffer(AudioChannelRoute.Stereo, PcmFrameFormat.MiPlayAac);
        await using var tap = shared.CreateCapture(new CaptureSelection.SystemMix("default", "Default"));
        await tap.StartAsync(destination, default);
        Assert.Equal(0, destination.BufferedMilliseconds);
        var bytes = new byte[destination.FrameByteCount];
        for (var i = 1; i <= 3; i++) { bytes[0] = (byte)i; p.LastCapture!.Destination!.Write(bytes, (ulong)i); }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        for (var i = 1; i <= 3; i++)
        {
            var frame = await destination.ReadCapturedFrameAsync(timeout.Token);
            Assert.Equal((byte)i, frame.Bytes[0]);
            Assert.Equal((ulong)i, frame.PresentationTimeMicroseconds);
        }
        await tap.StopAsync().WaitAsync(TimeSpan.FromSeconds(1));
        Assert.True(p.LastCapture!.Stopped);
    }
    private sealed class Fixture : IDisposable
    {
        private readonly DirectoryInfo directory = Directory.CreateTempSubdirectory("miplay-routing-");
        public readonly Platform Platform = new(); public readonly Discovery Discovery = new();
        public readonly List<Session> Sessions = [];
        public DesktopStore Store => new(directory.FullName);
        public static Speaker Speaker { get; } = MakeSpeaker();
        static Speaker MakeSpeaker()
        {
            var u = new Uri("http://192.0.2.1/"); var e = new UpnpServiceEndpoint("", u, u, u);
            return new("a", "Speaker", "", new("a", "Speaker", "", "", IPAddress.Parse("192.0.2.1"), u, e, e, e, ""));
        }
        public DesktopController Create() => new(Platform, Discovery, Store,
            () => { var s = new Session(); Sessions.Add(s); return s; }, () => "fake");
        public void Dispose() => directory.Delete(true);
    }
    private sealed class Discovery : ISpeakerDiscovery
    {
        public IReadOnlyList<Speaker> Items = [Fixture.Speaker];
        public Task<IReadOnlyList<Speaker>> SearchAsync(CancellationToken t) => Task.FromResult(Items);
    }
    private sealed class Platform : IDesktopAudioPlatform
    {
        public bool SupportsLocalMute => true;
        public event EventHandler? Suspending { add {} remove {} }
        public AudioSourceItem GetDefaultOutputDevice() => new("default", "Default");
        public IReadOnlyList<AudioSourceItem> GetOutputDevices() => [GetDefaultOutputDevice(), new("usb", "USB")];
        public bool ActiveProcesses = true;
        public IReadOnlyList<AudioSourceItem> GetCandidateProcesses() => ActiveProcesses ? [new("process", "Test", Environment.ProcessId)] : [];
        public Capture? LastCapture;
        public IAudioCaptureSource CreateCapture(CaptureSelection selection) => LastCapture = new(selection);
        public IMediaPacingClock CreateMediaClock() => new MonotonicMediaPacingClock();
        public void Dispose() {}
    }
    private sealed class Capture(CaptureSelection selection) : IAudioCaptureSource
    {
        public PcmFrameBuffer? Destination;
        public CaptureSelection Selection => selection; public bool IsRunning { get; private set; }
        public bool Stopped; public CaptureHealth Health => new(DateTimeOffset.UtcNow, null, null, 0);
        public event EventHandler<Exception>? CaptureFailed { add {} remove {} }
        public Task StartAsync(PcmFrameBuffer b, CancellationToken t) { Destination = b; IsRunning = true; return Task.CompletedTask; }
        public Task StopAsync() { IsRunning = false; Stopped = true; return Task.CompletedTask; }
        public ValueTask DisposeAsync() { Stopped = true; return ValueTask.CompletedTask; }
    }
    private sealed class Session : ICastSession
    {
        public MiPlaySystemAudioRequest? Request; public bool Stopped;
        public MiPlayCastDiagnostics Diagnostics { get; private set; } = new(MiPlayCastState.Streaming, "test", BufferedMilliseconds: 12);
        public event EventHandler<MiPlayCastDiagnostics>? DiagnosticsChanged { add {} remove {} }
        public Task StartAsync(MiPlaySystemAudioRequest r, CancellationToken t) { Request = r; return Task.CompletedTask; }
        public Task StopAsync() { Stopped = true; return Task.CompletedTask; }
        public Task SetVolumeAsync(int v, CancellationToken t) => Task.CompletedTask;
        public Task SetCaptureAsync(CaptureSelection s, CancellationToken t) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
