using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Threading.Channels;
using DLNACast.Core.Abstractions;
using DLNACast.Core.Audio;
using DLNACast.Core.MiPlay;
using DLNACast.Core.Models;
using MiPlay.Desktop.Services;

namespace MiPlay.Tests;

public sealed class SharedDesktopPipelineTests
{
    [Theory]
    [InlineData(256000)]
    [InlineData(320000)]
    [InlineData(512000)]
    public async Task SharedGroupUsesSelectedEncoderQuality(int bitrate)
    {
        using var source = new Source();
        var factory = new EncoderFactory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var group = await MiPlaySharedAudioSession.StartAsync(source,
            new CaptureSelection.SystemMix("default", "default"), "fake", participantCount: 1,
            cancellationToken: timeout.Token, encoderFactory: factory, drainBeforeMediaReady: true, audioBitRate: bitrate);
        await using var first = group.Subscribe(AudioChannelRoute.Stereo);
        await using var second = group.Subscribe(AudioChannelRoute.Stereo);
        Assert.Equal(bitrate, Assert.Single(factory.Bitrates));
    }

    [Theory]
    [InlineData(1000000, 200000, -800000)]
    [InlineData(400000, 1500000, 1100000)]
    [InlineData(1000000, 1000000, 0)]
    public void ReceiverDelayKeepsTheGroupOriginAcrossStartupAndRecovery(int group, int receiver, long delta)
    {
        var timing = new MiPlayReceiverTiming(group, receiver);
        const ulong origin = 100_000_000;
        Assert.Equal((ulong)((long)origin + delta), timing.ToReceiver(origin));
        Assert.Equal(origin, timing.ToGroup(timing.ToReceiver(origin)));
        Assert.Equal(origin + 123456, timing.ToGroup(timing.ToReceiver(origin + 123456)));
        Assert.Throws<OverflowException>(() => new MiPlayReceiverTiming(2_000_000, 0).ToReceiver(1));
    }

    [Fact]
    public async Task PrePlayDrainsUntilReadinessAndDoesNotPublishHandshakeAudio()
    {
        using var platform = new Source();
        var factory = new EncoderFactory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var group = await Start(platform, factory, timeout.Token);
        await using var member = group.Subscribe(AudioChannelRoute.Stereo);
        await Eventually(() => group.PrePlayDiscardedAccessUnits >= 12, timeout.Token);
        var threshold = Now();
        await member.PrepareMediaStartupAsync(timeout.Token);
        var first = await member.ReadIndexedAccessUnitAsync(timeout.Token);
        Assert.Equal(0, first.AccessUnitIndex);
        Assert.True(first.AccessUnit.PresentationTimeMicroseconds >= threshold);
        Assert.Single(factory.Encoders);
        Assert.InRange(member.EncoderPendingInputFrames, 0, MiPlayEncoderInputWindow.DefaultCapacity);
    }

    [Fact]
    public async Task CancelingAJoinDuringFounderStartupDoesNotFailTheFounder()
    {
        using var platform = new Source();
        var factory = new EncoderFactory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var group = await Start(platform, factory, timeout.Token);
        await using var founder = group.Subscribe(AudioChannelRoute.Stereo);
        var joining = group.Subscribe(AudioChannelRoute.Stereo);
        Assert.True(joining.IsLateJoin);
        using var joinLifetime = new CancellationTokenSource();
        var waiting = joining.PrepareMediaStartupAsync(joinLifetime.Token);
        joinLifetime.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        await joining.DisposeAsync();
        Assert.True(group.IsAvailable);
        await Ready(founder, timeout.Token);
        Assert.False(factory.Encoders.Single().Disposed);
    }

    [Fact]
    public async Task SlowJoinUsesFreshHistoryAndFounderCanLeaveWithoutRestartingEncoder()
    {
        using var platform = new Source();
        var factory = new EncoderFactory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var founderStartup = new CancellationTokenSource();
        await using var group = await Start(platform, factory, founderStartup.Token);
        var founder = group.Subscribe(AudioChannelRoute.Stereo, 800_000);
        var baseOffset = await Ready(founder, timeout.Token);
        var frames = new ConcurrentDictionary<long, MiPlaySharedIndexedAccessUnit>();
        using var pumping = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var pump = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    var frame = await founder.ReadIndexedAccessUnitAsync(pumping.Token);
                    frames[frame.AccessUnitIndex] = frame;
                    await SendFrame(founder, frame.AccessUnitIndex, pumping.Token);
                }
            }
            catch (OperationCanceledException) when (pumping.IsCancellationRequested) { }
        });
        await using var newcomer = group.Subscribe(AudioChannelRoute.Stereo, 1_100_000);
        try
        {
            await newcomer.PrepareMediaStartupAsync(timeout.Token);
            // Longer than the 64-frame timestamp retention, while its diagnostic
            // PCM queue is deliberately never read. Live output must keep moving.
            await Eventually(() => frames.Count >= 85, timeout.Token);
            Assert.Equal(baseOffset + 300_000, await newcomer.SynchronizeTimeOffsetAsync(0, timeout.Token));
            await newcomer.PrepareLateJoinReplayAsync(timeout.Token);
            var firstIndex = -1L;
            for (var i = 0; i < MiPlaySharedStartupAccessUnitBuffer.LateJoinRequiredAccessUnitCount; i++)
            {
                var replay = await newcomer.ReadIndexedAccessUnitAsync(timeout.Token);
                if (i == 0) firstIndex = replay.AccessUnitIndex;
                Assert.False(replay.ParticipatesInSendBarrier);
                await Eventually(() => frames.ContainsKey(replay.AccessUnitIndex), timeout.Token);
                Assert.Equal(frames[replay.AccessUnitIndex].AccessUnit.Bytes, replay.AccessUnit.Bytes);
                Assert.Equal(founder.GetOutputTimestamp(replay.AccessUnitIndex), newcomer.GetOutputTimestamp(replay.AccessUnitIndex));
            }
            Assert.True(firstIndex > 30);
            var live = await newcomer.ReadIndexedAccessUnitAsync(timeout.Token);
            Assert.True(live.ParticipatesInSendBarrier);
            await Eventually(() => frames.ContainsKey(live.AccessUnitIndex), timeout.Token);
            await SendFrame(newcomer, live.AccessUnitIndex, timeout.Token);
            pumping.Cancel();
            await pump;
            founderStartup.Cancel(); // Must not own the group encoder lifetime.
            await founder.DisposeAsync();
            for (var i = 0; i < 5; i++)
            {
                var next = await newcomer.ReadIndexedAccessUnitAsync(timeout.Token);
                Assert.True(next.AccessUnitIndex > live.AccessUnitIndex);
                await SendFrame(newcomer, next.AccessUnitIndex, timeout.Token);
            }
            Assert.Single(factory.Encoders);
            Assert.Single(platform.Captures);
            Assert.False(factory.Encoders[0].Disposed);
            Assert.False(platform.Captures[0].Stopped);
        }
        finally
        {
            pumping.Cancel();
            await pump;
            await founder.DisposeAsync();
        }
    }

    [Fact]
    public async Task LateLiveReadCanArriveBeforeFoundersPreviousSendCompletes()
    {
        using var platform = new Source();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var group = await Start(platform, new EncoderFactory(), timeout.Token);
        await using var founder = group.Subscribe(AudioChannelRoute.Stereo);
        await Ready(founder, timeout.Token);
        const long origin = 100_000;
        async Task Send(MiPlaySharedAudioSession.MiPlaySharedAudioSubscription member, long index)
        {
            await member.SynchronizeSendCompletionAsync(index, Stopwatch.GetTimestamp(), 0,
                origin, Stopwatch.Frequency, false, timeout.Token);
        }
        for (var i = 1; i < 70; i++)
        {
            var frame = await founder.ReadIndexedAccessUnitAsync(timeout.Token);
            founder.GetOutputTimestamp(frame.AccessUnitIndex);
            await Send(founder, frame.AccessUnitIndex);
        }
        await using var newcomer = group.Subscribe(AudioChannelRoute.Stereo);
        await newcomer.PrepareMediaStartupAsync(timeout.Token);
        await newcomer.PrepareLateJoinReplayAsync(timeout.Token);
        for (var i = 0; i < MiPlaySharedStartupAccessUnitBuffer.LateJoinRequiredAccessUnitCount; i++)
        {
            var replay = await newcomer.ReadIndexedAccessUnitAsync(timeout.Token);
            Assert.InRange(replay.AccessUnitIndex, 23, 69);
            // Published frame 70 has no final timestamp yet and must not be replayed.
        }

        // Frame 70 is published to the founder before the new live subscription
        // registers. Taking it releases the broadcast, NOT its socket send.
        var prior = await founder.ReadIndexedAccessUnitAsync(timeout.Token);
        founder.GetOutputTimestamp(prior.AccessUnitIndex);
        // Ensure frame 71 has been published before registration (it is then
        // excluded too). This reproduces the production prefetch race.
        await Task.Delay(60, timeout.Token);
        var lateRead = newcomer.ReadIndexedAccessUnitAsync(timeout.Token).AsTask();
        var intervening = await founder.ReadIndexedAccessUnitAsync(timeout.Token);
        var live = await lateRead;
        Assert.True(live.AccessUnitIndex > prior.AccessUnitIndex);

        var arrival = Send(newcomer, live.AccessUnitIndex);
        Assert.False(arrival.IsCompleted);
        await Send(founder, prior.AccessUnitIndex);
        founder.GetOutputTimestamp(intervening.AccessUnitIndex);
        await Send(founder, intervening.AccessUnitIndex);
        var same = await founder.ReadIndexedAccessUnitAsync(timeout.Token);
        Assert.Equal(live.AccessUnitIndex, same.AccessUnitIndex);
        founder.GetOutputTimestamp(same.AccessUnitIndex);
        await Send(founder, same.AccessUnitIndex);
        await arrival;
    }

    [Fact]
    public async Task SharedOffsetsRetainEachReceiversCompensationDuringRecoveryAndLateJoin()
    {
        using var platform = new Source();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var group = await Start(platform, new EncoderFactory(), timeout.Token);
        await using var first = group.Subscribe(AudioChannelRoute.Stereo, 800_000);
        await Ready(first, timeout.Token);
        await using var second = group.Subscribe(AudioChannelRoute.Stereo, 200_000);
        var key = new MiPlaySharedTimeOffsetUpdateKey(MiPlaySharedTimeOffsetUpdateKind.TransportRecovery, 42);
        var groupValue = first.PrepareRuntimeTimeOffsetUpdate(key, 100_000_000);
        var secondValue = second.PrepareRuntimeTimeOffsetUpdate(key, 99_400_000);
        Assert.Equal(600_000UL, groupValue - secondValue);
        second.CommitRuntimeTimeOffsetUpdate(key, secondValue);
        first.CommitRuntimeTimeOffsetUpdate(key, groupValue);
        await using var third = group.Subscribe(AudioChannelRoute.Stereo, 1_200_000);
        Assert.Equal(100_400_000UL, await third.SynchronizeTimeOffsetAsync(0, timeout.Token));
    }

    [Fact]
    public async Task TwentyGroupLifetimesReleaseAllCaptureAndEncoderResources()
    {
        using var platform = new Source();
        var factory = new EncoderFactory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        for (var i = 0; i < 20; i++)
        {
            await using var group = await Start(platform, factory, timeout.Token);
            await using var member = group.Subscribe(AudioChannelRoute.Stereo);
            await member.DisposeAsync();
        }
        Assert.Equal(20, factory.Encoders.Count);
        Assert.All(factory.Encoders, encoder => Assert.True(encoder.Disposed));
        Assert.All(platform.Captures, capture => Assert.True(capture.Stopped));
    }

    [Fact]
    public async Task DesktopSessionLeasesShareOneEncoderAndReleaseOnlyAfterLastTransport()
    {
        using var platform = new Source();
        var factory = new EncoderFactory();
        await using var pool = new DesktopCastGroup(platform, factory);
        var firstTransport = new Transport(); var secondTransport = new Transport();
        await using var first = new GroupCastSession(firstTransport, pool);
        await using var second = new GroupCastSession(secondTransport, pool);
        using var firstLifetime = new CancellationTokenSource();
        await first.StartAsync(Request(), firstLifetime.Token);
        await second.StartAsync(Request() with { PlaybackDelayMicroseconds = 200_000 }, default);
        Assert.Same(firstTransport.Request!.SharedAudioSession, secondTransport.Request!.SharedAudioSession);
        Assert.Equal(200_000, secondTransport.Request.PlaybackDelayMicroseconds);
        Assert.Single(factory.Encoders);
        firstLifetime.Cancel();
        await first.StopAsync();
        Assert.False(factory.Encoders.Single().Disposed);
        Assert.False(platform.Captures.Single().Stopped);
        await second.StopAsync();
        Assert.True(factory.Encoders.Single().Disposed);
        Assert.True(platform.Captures.Single().Stopped);
        await using var third = new GroupCastSession(new Transport(), pool);
        await third.StartAsync(Request(), default);
        Assert.Equal(2, factory.Encoders.Count);
        await third.StopAsync();
        Assert.All(factory.Encoders, encoder => Assert.True(encoder.Disposed));
    }

    [Fact]
    public async Task FailedTransportStartupReleasesItsGroupLease()
    {
        using var platform = new Source();
        var factory = new EncoderFactory();
        await using var pool = new DesktopCastGroup(platform, factory);
        await using var session = new GroupCastSession(new Transport { FailStart = true }, pool);
        await Assert.ThrowsAsync<IOException>(() => session.StartAsync(Request(), default));
        Assert.True(factory.Encoders.Single().Disposed);
        Assert.True(platform.Captures.Single().Stopped);
    }

    [FfmpegFact]
    public async Task RealSharedAacEncoderDrainsPrePlayAndProducesFreshOrderedOutput()
    {
        using var platform = new Source();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var factory = new RealEncoderFactory();
        await using (var group = await MiPlaySharedAudioSession.StartAsync(platform,
            new CaptureSelection.SystemMix("default", "default"), Environment.GetEnvironmentVariable("MIPLAY_TEST_FFMPEG")!,
            participantCount: 1, cancellationToken: timeout.Token, encoderFactory: factory, drainBeforeMediaReady: true))
        {
            await using var member = group.Subscribe(AudioChannelRoute.Stereo);
            await Eventually(() => group.PrePlayDiscardedAccessUnits >= 15, timeout.Token);
            var readyAt = Now();
            await member.PrepareMediaStartupAsync(timeout.Token);
            for (var i = 0; i < 30; i++)
            {
                var frame = await member.ReadIndexedAccessUnitAsync(timeout.Token);
                Assert.Equal(i, frame.AccessUnitIndex);
                Assert.True(frame.AccessUnit.PresentationTimeMicroseconds >= readyAt);
                Assert.InRange(Now() - frame.AccessUnit.PresentationTimeMicroseconds, 0UL, 1_000_000UL);
                var parser = new MiPlayAdtsStreamParser();
                Assert.Single(parser.Push(frame.AccessUnit.Bytes));
                Assert.Equal(0, parser.PendingByteCount);
            }
        }
        Assert.Single(factory.Pids);
        foreach (var pid in factory.Pids)
        {
            try { using var process = Process.GetProcessById(pid); Assert.True(process.HasExited); }
            catch (ArgumentException) { }
        }
        Assert.All(platform.Captures, capture => Assert.True(capture.Stopped));
    }

    private static MiPlaySystemAudioRequest Request()
    {
        var uri = new Uri("http://192.0.2.10:8899/");
        var endpoint = new UpnpServiceEndpoint("", uri, uri, uri);
        return new(new("speaker", "speaker", "Xiaomi", "", IPAddress.Parse("192.0.2.10"), uri,
            endpoint, endpoint, endpoint, ""), new CaptureSelection.SystemMix("default", "default"), "fake");
    }

    private sealed class Transport : ICastSession
    {
        public bool FailStart;
        public MiPlaySystemAudioRequest? Request;
        public MiPlayCastDiagnostics Diagnostics => new(MiPlayCastState.Streaming, "test");
        public event EventHandler<MiPlayCastDiagnostics>? DiagnosticsChanged { add { } remove { } }
        public Task StartAsync(MiPlaySystemAudioRequest request, CancellationToken token)
        { Request = request; if (FailStart) throw new IOException("connection failed"); return Task.CompletedTask; }
        public Task StopAsync() => Task.CompletedTask;
        public Task SetVolumeAsync(int volume, CancellationToken token) => Task.CompletedTask;
        public Task SetCaptureAsync(CaptureSelection selection, CancellationToken token) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class RealEncoderFactory : IAacEncoderFactory
    {
        public List<int> Pids = [];
        public IAacEncoder Start(string path, int bitrate)
        {
            var encoder = MiPlayFfmpegAacEncoder.Start(path, bitrate,
                OperatingSystem.IsWindows() ? "aac_mf" : "aac");
            Pids.Add(encoder.ProcessId); return encoder;
        }
    }

    [Fact]
    public async Task PreparedManualRoutesDoNotWaitForThreeStartupReceivers()
    {
        using var platform = new Source();
        var factory = new EncoderFactory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var group = await MiPlaySharedAudioSession.StartAsync(platform,
            new CaptureSelection.SystemMix("default", "default"), Enum.GetValues<AudioChannelRoute>(),
            "fake", timeout.Token, factory, drainBeforeMediaReady: true, startupParticipantCount: 1);
        await using var left = group.Subscribe(AudioChannelRoute.LeftAsMono);
        await Ready(left, timeout.Token);
        await using var right = group.Subscribe(AudioChannelRoute.RightAsMono);
        await right.SynchronizeOpenAsync(timeout.Token);
        await right.PrepareMediaStartupAsync(timeout.Token);
        Assert.Equal(3, factory.Encoders.Count);
        Assert.Single(platform.Captures);
    }

    private static Task<MiPlaySharedAudioSession> Start(Source source, IAacEncoderFactory factory, CancellationToken token) =>
        MiPlaySharedAudioSession.StartAsync(source, new CaptureSelection.SystemMix("default", "default"),
            "fake", participantCount: 1, cancellationToken: token, encoderFactory: factory,
            drainBeforeMediaReady: true);

    private static async Task<ulong> Ready(MiPlaySharedAudioSession.MiPlaySharedAudioSubscription member, CancellationToken token)
    {
        await member.SynchronizeOpenAsync(token);
        await member.PrepareMediaStartupAsync(token);
        var first = await member.ReadIndexedAccessUnitAsync(token);
        var offset = await member.SynchronizeTimeOffsetAsync(Now() + 800_000, token);
        await member.SynchronizeMediaAsync(offset, token);
        var origin = await member.SynchronizeAccessUnitRunwayAsync(token);
        await SendFrame(member, first.AccessUnitIndex, token, origin);
        return offset;
    }

    private static async Task SendFrame(MiPlaySharedAudioSession.MiPlaySharedAudioSubscription member,
        long index, CancellationToken token, long? initialOrigin = null)
    {
        var origin = await member.WaitForSendTurnAsync(index, token) ?? initialOrigin ?? Stopwatch.GetTimestamp();
        member.GetOutputTimestamp(index, joiningLive: true);
        await member.SynchronizeSendCompletionAsync(index, Stopwatch.GetTimestamp(), 0,
            origin, Stopwatch.Frequency, false, token);
    }

    private static ulong Now() => MiPlayWfdMediaClock.ConvertStopwatchTicksToMicroseconds(Stopwatch.GetTimestamp(), Stopwatch.Frequency);
    private static async Task Eventually(Func<bool> condition, CancellationToken token)
    {
        while (!condition()) await Task.Delay(5, token);
    }

    private sealed class EncoderFactory : IAacEncoderFactory
    {
        public List<Encoder> Encoders = [];
        public List<int> Bitrates = [];
        public IAacEncoder Start(string path, int bitrate)
        {
            Bitrates.Add(bitrate);
            var encoder = new Encoder(); Encoders.Add(encoder); return encoder;
        }
    }
    private sealed class Encoder : IAacEncoder
    {
        private readonly Channel<byte[]> output = Channel.CreateUnbounded<byte[]>();
        public bool Disposed;
        public ValueTask WritePcmAsync(ReadOnlyMemory<byte> pcm, CancellationToken token) => output.Writer.WriteAsync(pcm[..8].ToArray(), token);
        public ValueTask CompleteInputAsync(CancellationToken token) { output.Writer.TryComplete(); return ValueTask.CompletedTask; }
        public async ValueTask<byte[]?> ReadAccessUnitAsync(CancellationToken token) => await output.Reader.ReadAsync(token);
        public ValueTask DisposeAsync() { Disposed = true; output.Writer.TryComplete(); return ValueTask.CompletedTask; }
    }
    private sealed class Source : IDesktopAudioPlatform
    {
        public List<Capture> Captures = [];
        public event EventHandler? Suspending { add { } remove { } }
        public AudioSourceItem GetDefaultOutputDevice() => new("default", "default");
        public IReadOnlyList<AudioSourceItem> GetOutputDevices() => [];
        public IReadOnlyList<AudioSourceItem> GetCandidateProcesses() => [];
        public IMediaPacingClock CreateMediaClock() => new MonotonicMediaPacingClock();
        public IAudioCaptureSource CreateCapture(CaptureSelection selection)
        {
            var capture = new Capture(selection); Captures.Add(capture); return capture;
        }
        public void Dispose() { }
    }
    private sealed class Capture(CaptureSelection selection) : IAudioCaptureSource
    {
        private readonly CancellationTokenSource lifetime = new();
        private Task pump = Task.CompletedTask;
        public bool Stopped;
        public CaptureSelection Selection => selection;
        public bool IsRunning => !Stopped;
        public CaptureHealth Health => new(DateTimeOffset.UtcNow, null, null, 0);
        public event EventHandler<Exception>? CaptureFailed { add { } remove { } }
        public Task StartAsync(PcmFrameBuffer destination, CancellationToken token)
        {
            pump = Task.Run(async () =>
            {
                try
                {
                    for (long index = 0; ; index++)
                    {
                        var bytes = new byte[destination.FrameByteCount];
                        BitConverter.TryWriteBytes(bytes.AsSpan(), index);
                        destination.Write(bytes, Now());
                        await Task.Delay(20, lifetime.Token);
                    }
                }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            });
            return Task.CompletedTask;
        }
        public async Task StopAsync() { if (Stopped) return; Stopped = true; lifetime.Cancel(); await pump; }
        public async ValueTask DisposeAsync() { await StopAsync(); lifetime.Dispose(); }
    }
}
