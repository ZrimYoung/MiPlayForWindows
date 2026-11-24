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
    public async Task SlowDelayWriteDropsIntermediateSliderTargets()
    {
        using var fixture = new Fixture();
        await using var controller = fixture.Create();
        await controller.RefreshAsync(); await controller.SetSelectedAsync("a", true);
        var session = Assert.Single(fixture.Sessions);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var values = new List<int>();
        session.DelayHandler = async value => {
            values.Add(value);
            if (value == 100) { entered.TrySetResult(); await release.Task; }
        };
        var first = controller.SetDelayAsync("a", 100);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var middle = controller.SetDelayAsync("a", 200);
        var last = controller.SetDelayAsync("a", 300);
        release.TrySetResult();
        await Task.WhenAll(first, middle, last).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(new[] { 100, 300 }, values);
        Assert.False(session.Disposed); Assert.Single(fixture.Sessions);
    }

    [Fact]
    public void PeerClosingDuringHandshakeIsCompatibilityNotAnOfflineSpeaker()
    {
        var failure = new MiPlayBootstrapClosedException("AwaitingNativeVersionAcknowledgement", new EndOfStreamException());
        Assert.Equal(DesktopFailure.Compatibility, CastService.ClassifyFailure(failure));
        Assert.Equal(DesktopFailure.Offline, CastService.ClassifyFailure(new EndOfStreamException()));
    }

    [Fact]
    public async Task QualityChangeStopsSessionsPersistsAndAppliesToNewSessions()
    {
        using var fixture = new Fixture();
        await using (var controller = fixture.Create())
        {
            await controller.RefreshAsync();
            foreach (var bitrate in new[] { 320000, 512000, 256000 })
            {
                await controller.SetSelectedAsync("a", true);
                await controller.SetAudioBitRateAsync(bitrate);
                Assert.All(controller.Snapshot.Speakers, card => Assert.False(card.Selected));
                Assert.Equal(bitrate, fixture.Store.Load().AudioBitRate);
                await controller.SetSelectedAsync("a", true);
                Assert.Equal(bitrate, fixture.Sessions.Last().Request!.AudioBitRate);
            }
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => controller.SetAudioBitRateAsync(999999));
            Assert.True(controller.Snapshot.Speakers.Single(s => s.Id == "a").Selected);
        }
        await using var reopened = fixture.Create();
        Assert.Equal(256000, reopened.Snapshot.AudioBitRate);
        Assert.All(reopened.Snapshot.Speakers, card => Assert.False(card.Selected));
    }

    [Fact]
    public async Task LiveGroupDelayKeepsBothSessionsAndEncoderSelection()
    {
        using var fixture = StereoFixture();
        await using var controller = fixture.Create();
        await controller.RefreshAsync();
        await controller.SetGroupSelectedAsync("pair", true);
        foreach (var target in new[] { 1200, 300, 1000 })
        {
            await controller.SetGroupDelayAsync("pair", target);
            Assert.Equal(2, fixture.Sessions.Count);
            Assert.All(fixture.Sessions, session => { Assert.False(session.Disposed); Assert.Equal(target, session.Delay); });
            Assert.All(controller.Snapshot.Speakers.Where(card => card.GroupId == "pair"), card => { Assert.True(card.Selected); Assert.Equal(target, card.DelayMilliseconds); });
        }
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => controller.SetGroupDelayAsync("pair", -10));
        Assert.All(fixture.Sessions, session => Assert.False(session.Disposed));
    }

    [Fact]
    public async Task SavedVolumeTimeoutDoesNotStopCastingOrDisplayUnconfirmedValue()
    {
        using var fixture = new Fixture { MissingVolumeAck = true };
        await using var controller = fixture.Create();
        await controller.RefreshAsync();
        await controller.SetVolumeAsync("a", 57);
        await controller.SetSelectedAsync("a", true);
        var session = Assert.Single(fixture.Sessions);
        var card = controller.Snapshot.Speakers.Single(s => s.Id == "a");
        Assert.True(card.Selected);
        Assert.Equal("Casting", card.State);
        Assert.Contains("音量调整未获确认", card.Message);
        Assert.Equal(30, card.Volume);
        Assert.False(session.Disposed);
        session.ReportStreaming();
        Assert.Contains("音量调整未获确认", controller.Snapshot.Speakers.Single(s => s.Id == "a").Message);
        session.MissingVolumeAck = false;
        await controller.SetVolumeAsync("a", 58);
        card = controller.Snapshot.Speakers.Single(s => s.Id == "a");
        Assert.True(card.Selected);
        Assert.Equal(58, card.Volume);
        Assert.DoesNotContain("未获确认", card.Message);
        Assert.False(session.Disposed);
    }

    [Fact]
    public async Task ConfirmedStereoGroupLinksVolumeAndRetainsIndividualDelays()
    {
        using var fixture = new Fixture();
        fixture.Discovery.Speakers = [
            SpeakerAt("a", "192.0.2.2") with { Identity = new("Art L", "L09A", "pair", "客厅", "left", true) },
            SpeakerAt("b", "192.0.2.3") with { Identity = new("Art R", "L09A", "pair", "客厅", "right", false) }
        ];
        await using var controller = fixture.Create();
        await controller.RefreshAsync();
        Assert.All(controller.Snapshot.Speakers, s => { Assert.Equal("pair", s.GroupId); Assert.False(s.Selected); });
        await controller.SetGroupSelectedAsync("pair", true);
        Assert.All(controller.Snapshot.Speakers, s => Assert.True(s.Selected));
        await controller.SetVolumeAsync("a", 20);
        Assert.All(controller.Snapshot.Speakers, s => Assert.Equal(20, s.Volume));
        await controller.SetVolumeAsync("b", 35);
        Assert.All(controller.Snapshot.Speakers, s => Assert.Equal(35, s.Volume));
        Assert.All(fixture.Sessions, s => Assert.Equal(35, s.Volume));
        Assert.All(fixture.Store.Load().Speakers!.Values, s => Assert.Equal(35, s.Volume));
        await controller.SetDelayAsync("a", 200);
        Assert.Equal(200, controller.Snapshot.Speakers.Single(s => s.Id == "a").DelayMilliseconds);
        Assert.Equal(1000, controller.Snapshot.Speakers.Single(s => s.Id == "b").DelayMilliseconds);
        await controller.SetGroupSelectedAsync("pair", false);
        Assert.All(controller.Snapshot.Speakers, s => Assert.False(s.Selected));
        Assert.All(fixture.Sessions, s => Assert.True(s.Disposed));
        Assert.Equal("left", fixture.Store.Load().Speakers!["a"].Channel);
    }

    [Fact]
    public async Task GroupVolumeKeepsBothPlayingWhenOneAcknowledgementIsMissing()
    {
        using var fixture = StereoFixture();
        await using var controller = fixture.Create();
        await controller.RefreshAsync();
        await controller.SetGroupSelectedAsync("pair", true);
        fixture.Sessions.Single(s => s.Request!.Renderer.Udn == "a").MissingVolumeAck = true;
        await controller.SetGroupVolumeAsync("pair", 45);
        Assert.All(controller.Snapshot.Speakers.Where(s => s.GroupId == "pair"), s => {
            Assert.True(s.Selected); Assert.Equal("Casting", s.State);
        });
        var left = controller.Snapshot.Speakers.Single(s => s.Id == "a");
        Assert.Equal(30, left.Volume);
        Assert.Contains("未获确认", left.Message);
        Assert.Equal(45, controller.Snapshot.Speakers.Single(s => s.Id == "b").Volume);
        Assert.Null(controller.Snapshot.Speakers.Single(s => s.Id == "other").Volume);
        Assert.All(fixture.Sessions, s => Assert.False(s.Disposed));
    }

    [Fact]
    public async Task ConcurrentGroupVolumeChangesAreOrderedAndPersistAcrossRestart()
    {
        using var fixture = StereoFixture();
        await using (var controller = fixture.Create())
        {
            await controller.RefreshAsync();
            await controller.SetGroupSelectedAsync("pair", true);
            await Task.WhenAll(controller.SetGroupVolumeAsync("pair", 20), controller.SetGroupVolumeAsync("pair", 60));
            Assert.All(fixture.Sessions, s => Assert.Equal(60, s.Volume));
        }
        await using var reopened = fixture.Create();
        await reopened.RefreshAsync();
        Assert.All(reopened.Snapshot.Speakers.Where(s => s.GroupId == "pair"), s => {
            Assert.Equal(60, s.Volume); Assert.False(s.Selected);
        });
        await reopened.SetGroupSelectedAsync("pair", true);
        Assert.All(fixture.Sessions.Where(s => !s.Disposed), s => Assert.Equal(60, s.Volume));
    }

    [Fact]
    public async Task InvalidGroupVolumeDoesNotChangeAnySpeaker()
    {
        using var fixture = StereoFixture();
        await using var controller = fixture.Create();
        await controller.RefreshAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.SetGroupVolumeAsync("unknown", 40));
        await Assert.ThrowsAsync<ArgumentException>(() => controller.SetGroupVolumeAsync("", 40));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => controller.SetGroupVolumeAsync("pair", 101));
        Assert.All(controller.Snapshot.Speakers, s => Assert.Null(s.Volume));
        Assert.Empty(fixture.Sessions);
    }

    [Fact]
    public async Task JoiningStereoGroupUnifiesLegacySavedLevelsWithoutRaisingVolume()
    {
        using var fixture = StereoFixture();
        fixture.Store.Save(new(Speakers: new() {
            ["a"] = new(Volume: 50, GroupId: "pair", Channel: "left"),
            ["b"] = new(Volume: 32, GroupId: "pair", Channel: "right")
        }));
        await using var controller = fixture.Create();
        await controller.RefreshAsync();
        // Discovery and opening the UI do not change or apply saved levels.
        Assert.Equal(50, controller.Snapshot.Speakers.Single(s => s.Id == "a").Volume);
        Assert.Empty(fixture.Sessions);
        await controller.SetGroupSelectedAsync("pair", true);
        Assert.All(fixture.Sessions, s => Assert.Equal(32, s.Volume));
        Assert.All(fixture.Store.Load().Speakers!.Values.Where(s => s.GroupId == "pair"), s => Assert.Equal(32, s.Volume));
    }

    [Fact]
    public async Task HardwareVolumeUpdatesSnapshotAndSavedValueWithoutEchoingACommand()
    {
        using var fixture = new Fixture();
        await using var controller = fixture.Create();
        await controller.RefreshAsync();
        await controller.SetVolumeAsync("a", 40);
        await controller.SetSelectedAsync("a", true);
        var session = Assert.Single(fixture.Sessions);
        var writes = session.VolumeWrites;
        session.Volume = 27; // Physical button / read-only receiver response.
        await Eventually(() => fixture.Store.Load().Speakers!["a"].Volume == 27);
        Assert.Equal(27, controller.Snapshot.Speakers.Single(s => s.Id == "a").Volume);
        Assert.Equal(writes, session.VolumeWrites);
        Assert.False(session.Disposed);
    }

    [Fact]
    public async Task RuntimeGroupDissolutionStopsFormerPairAndDoesNotAutoCastAfterRediscovery()
    {
        using var fixture = StereoFixture();
        await using var controller = fixture.Create();
        await controller.RefreshAsync();
        await controller.SetGroupSelectedAsync("pair", true);
        await controller.SetSelectedAsync("other", true);
        var left = fixture.Sessions.Single(s => s.Request!.Renderer.Udn == "a");
        var independent = fixture.Sessions.Single(s => s.Request!.Renderer.Udn == "other");
        left.ReceiverIdentity = new("Art L", "L09A", "", "", "center", false);
        await Eventually(() => fixture.Sessions.Where(s => s != independent).All(s => s.Disposed));
        Assert.All(controller.Snapshot.Speakers.Where(s => s.Id != "other"), s => Assert.False(s.Selected));
        Assert.False(independent.Disposed);
        Assert.Empty(controller.Snapshot.Speakers.Single(s => s.Id == "a").GroupId);
        fixture.Discovery.Speakers = [SpeakerAt("a", "192.0.2.22") with { Identity = left.ReceiverIdentity },
            SpeakerAt("b", "192.0.2.3") with { Identity = new("Art R", "L09A", "", "", "center", false) }];
        await controller.RefreshAsync();
        Assert.All(controller.Snapshot.Speakers, s => Assert.Empty(s.GroupId));
        Assert.Equal(3, fixture.Sessions.Count);
        Assert.All(controller.Snapshot.Speakers.Where(s => s.Id != "other"), s => Assert.False(s.Selected));
        Assert.True(controller.Snapshot.Speakers.Single(s => s.Id == "other").Selected);
    }

    [Fact]
    public async Task LaterReceiverReadResolvesUnconfirmedVolumeWithoutStoppingPlayback()
    {
        using var fixture = new Fixture { MissingVolumeAck = true };
        await using var controller = fixture.Create();
        await controller.RefreshAsync();
        await controller.SetVolumeAsync("a", 57);
        await controller.SetSelectedAsync("a", true);
        var session = Assert.Single(fixture.Sessions);
        Assert.Contains("未获确认", controller.Snapshot.Speakers.Single(s => s.Id == "a").Message);
        session.Volume = 57;
        await Eventually(() => controller.Snapshot.Speakers.Single(s => s.Id == "a").Failure == "None");
        Assert.Equal(57, controller.Snapshot.Speakers.Single(s => s.Id == "a").Volume);
        Assert.True(controller.Snapshot.Speakers.Single(s => s.Id == "a").Selected);
        Assert.False(session.Disposed);
    }

    [Fact]
    public async Task RediscoveryAndGroupRejoinDoNotRestartTheHealthyMember()
    {
        var now = DateTimeOffset.UtcNow;
        using var fixture = StereoFixture();
        await using var controller = fixture.Create(() => now);
        await controller.RefreshAsync();
        await controller.SetGroupSelectedAsync("pair", true);
        var left = fixture.Sessions.Single(s => s.Request!.Renderer.Udn == "a");
        await controller.SetSelectedAsync("b", false);
        fixture.Discovery.Speakers = fixture.Discovery.Speakers.Where(s => s.Id != "b").ToArray();
        now = now.AddSeconds(30);
        await controller.RefreshAsync();
        Assert.False(controller.Snapshot.Speakers.Single(s => s.Id == "b").Online);
        Assert.True(controller.Snapshot.Speakers.Single(s => s.Id == "a").Selected);
        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.SetGroupSelectedAsync("pair", true));
        fixture.Discovery.Speakers = [..fixture.Discovery.Speakers, SpeakerAt("b", "192.0.2.99") with {
            Identity = new("Art R", "L09A", "pair", "客厅", "right", false) }];
        await controller.RefreshAsync();
        Assert.False(controller.Snapshot.Speakers.Single(s => s.Id == "b").Selected);
        await controller.SetGroupSelectedAsync("pair", true);
        Assert.False(left.Disposed);
        Assert.Equal(3, fixture.Sessions.Count);
        Assert.Equal(IPAddress.Parse("192.0.2.99"), fixture.Sessions.Last().Request!.Renderer.Address);
        Assert.All(controller.Snapshot.Speakers.Where(s => s.GroupId == "pair"), s => Assert.True(s.Selected));
    }

    [Fact]
    public async Task PartialDeviceInfoDoesNotDissolveAKnownPlayingGroup()
    {
        using var fixture = StereoFixture();
        await using var controller = fixture.Create();
        await controller.RefreshAsync();
        await controller.SetGroupSelectedAsync("pair", true);
        fixture.Discovery.Speakers = fixture.Discovery.Speakers.Select(s => s.Id == "a" ? s with {
            Identity = new("Renamed Art", "L09A", "", "", "", false, HasGroupInfo: false) } : s).ToArray();
        await controller.RefreshAsync();
        var left = controller.Snapshot.Speakers.Single(s => s.Id == "a");
        Assert.Equal("pair", left.GroupId);
        Assert.Equal("Renamed Art", left.Name);
        fixture.Discovery.Speakers = fixture.Discovery.Speakers.Select(s => s.Id == "a" ? s with {
            Identity = new("Renamed Art", "L09A", "pair", "客厅", "", false) } : s).ToArray();
        await controller.RefreshAsync();
        Assert.Equal("left", controller.Snapshot.Speakers.Single(s => s.Id == "a").Channel);
        Assert.All(controller.Snapshot.Speakers.Where(s => s.GroupId == "pair"), s => Assert.True(s.Selected));
        Assert.All(fixture.Sessions, s => Assert.False(s.Disposed));
    }

    private static Fixture StereoFixture()
    {
        var fixture = new Fixture();
        fixture.Discovery.Speakers = [
            SpeakerAt("a", "192.0.2.2") with { Identity = new("Art L", "L09A", "pair", "客厅", "left", true) },
            SpeakerAt("b", "192.0.2.3") with { Identity = new("Art R", "L09A", "pair", "客厅", "right", false) },
            SpeakerAt("other", "192.0.2.4")
        ];
        return fixture;
    }

    [Fact]
    public async Task UnknownOrIncompleteStereoGroupCannotStartAnotherSpeaker()
    {
        using var fixture = new Fixture();
        fixture.Discovery.Speakers = [SpeakerAt("a", "192.0.2.2") with {
            Identity = new("Art L", "L09A", "pair", "客厅", "left", true) }];
        await using var controller = fixture.Create();
        await controller.RefreshAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.SetGroupSelectedAsync("pair", true));
        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.SetGroupSelectedAsync("unknown", true));
        Assert.Empty(fixture.Sessions);
    }

    [Fact]
    public async Task SwitchingToCompatibilityStopsAllAndPersistsWithoutAutostart()
    {
        using var fixture = new Fixture();
        await using (var controller = fixture.Create())
        {
            Assert.True(controller.Snapshot.SynchronizedPlayback);
            await controller.RefreshAsync();
            await controller.SetSelectedAsync("a", true);
            await controller.SetSelectedAsync("b", true);
            await controller.SetSynchronizedPlaybackAsync(false);
            Assert.False(controller.Snapshot.SynchronizedPlayback);
            Assert.All(controller.Snapshot.Speakers, speaker => Assert.False(speaker.Selected));
            Assert.All(fixture.Sessions, session => Assert.True(session.Disposed));
            Assert.False(fixture.Store.Load().SynchronizedPlayback);
        }
        await using var reopened = fixture.Create();
        Assert.False(reopened.Snapshot.SynchronizedPlayback);
        Assert.Equal(2, fixture.Sessions.Count);
    }

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
            Assert.False(firstA.Disposed);
            Assert.Equal(200, firstA.Delay);
            Assert.Equal(2, fixture.Sessions.Count);
            Assert.False(firstB.Disposed);
            Assert.Equal(61, firstB.Volume);
            Assert.Equal(25, firstA.Volume);
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
        public bool MissingVolumeAck;
        public DesktopStore Store => new(directory.FullName);
        public DesktopController Create(Func<DateTimeOffset>? now = null) => new(Platform, Discovery, Store,
            () => { var session = new FakeSession { BlockStartup = BlockStarts, MissingVolumeAck = MissingVolumeAck }; Sessions.Enqueue(session); return session; },
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
        public int? Delay;
        public Func<int, Task>? DelayHandler;
        public int VolumeWrites;
        public SpeakerIdentity? ReceiverIdentity { get; set; }
        public bool MissingVolumeAck;
        public int? ReceiverVolume => Volume ?? 30;
        public void ReportStreaming() => DiagnosticsChanged?.Invoke(this, new(MiPlayCastState.Streaming, ""));
        public MiPlayCastDiagnostics Diagnostics { get; private set; } = new(MiPlayCastState.Idle, "");
        public event EventHandler<MiPlayCastDiagnostics>? DiagnosticsChanged;
        public async Task StartAsync(MiPlaySystemAudioRequest request, CancellationToken token)
        {
            Request = request;
            if (BlockStartup) await Task.Delay(Timeout.InfiniteTimeSpan, token);
            Diagnostics = new(MiPlayCastState.Streaming, ""); DiagnosticsChanged?.Invoke(this, Diagnostics);
        }
        public Task StopAsync() => Task.CompletedTask;
        public Task SetVolumeAsync(int volume, CancellationToken token)
        {
            VolumeWrites++;
            if (MissingVolumeAck) throw new MiPlayVolumeAcknowledgementTimeoutException(16);
            Volume = volume; return Task.CompletedTask;
        }
        public Task SetCaptureAsync(CaptureSelection selection, CancellationToken token) => Task.CompletedTask;
        public async Task SetDelayAsync(int milliseconds, CancellationToken token) { if (DelayHandler is not null) await DelayHandler(milliseconds); Delay = milliseconds; }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
