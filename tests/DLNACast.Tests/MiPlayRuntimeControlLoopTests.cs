using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using DLNACast.Core.Abstractions;
using DLNACast.Core.MiPlay;
using DLNACast.Core.Models;

namespace DLNACast.Tests;

public sealed class MiPlayRuntimeControlLoopTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MediaUpdatesShareConnectionWithoutBlockingVolumeAndClearOnPlayerExit(bool shared)
    {
        var state = new MiPlayMediaState();
        // Exercise large cover writes followed by volume, lyrics and track changes
        // on both persistent shared and compatibility command paths.
        var cover = new byte[256 * 1024];
        cover[0] = 0xff; cover[1] = 0xd8; cover[2] = 0xff;
        var media = new MiPlayMediaSnapshot("歌曲", "歌手", "专辑", "QQ 音乐", 200000, 42000, false,
            Convert.ToBase64String(cover));
        state.Update(media);
        await using var harness = await Harness.StartAsync(shared, media: state);
        async Task<MiPlayCommandFrame> Until(ushort command)
        {
            for (var i = 0; i < 12; i++)
            {
                var frame = await harness.Read();
                if (frame.Command == MiPlayProtocolConstants.HeartbeatCommand)
                    await harness.Send(MiPlayProtocolConstants.HeartbeatAcknowledgementCommand, frame.Sequence);
                if (frame.Command == command) return frame;
            }
            throw new InvalidOperationException("Expected media command was not sent");
        }
        var first = await Until(0x12);
        Assert.True(MiPlaySetMediaInfoPayloadCodec.TryDecode(first.Payload, out var info));
        Assert.Equal("歌曲", info!.Title); Assert.Equal(media.ArtworkBase64, info.Art);
        // Real receivers can return empty ACKs; no media ACK may block audio or volume.
        await harness.Send(0x13, first.Sequence);
        var playback = await Until(0x5e);
        Assert.Equal(3u, BinaryPrimitives.ReadUInt32BigEndian(playback.Payload));
        var position = await Until(0x56);
        Assert.Equal(42000UL, BinaryPrimitives.ReadUInt64BigEndian(position.Payload));
        var volumeTask = harness.ChangeVolume(39);
        var volume = await Until(0x0c);
        await harness.Send(0x0d, volume.Sequence, MiPlayLegacyStatusScalarCodec.Encode(39));
        await volumeTask;
        state.Update(media with { Lyrics = "[00:01.00]Synthetic test line" });
        var lyricUpdate = await Until(0x12);
        Assert.True(MiPlaySetMediaInfoPayloadCodec.TryDecode(lyricUpdate.Payload, out info));
        Assert.Equal("[00:01.00]Synthetic test line", info!.Lyrics);
        Assert.Equal(media.TrackId, info.Id);
        state.Update(media with { Title = "下一首", ArtworkBase64 = "", PositionMilliseconds = 90000, Playing = true });
        var next = await Until(0x12);
        Assert.True(MiPlaySetMediaInfoPayloadCodec.TryDecode(next.Payload, out info));
        Assert.Equal("下一首", info!.Title); Assert.Empty(info.Art); Assert.Equal(39, info.Volume);
        Assert.Equal("", info.Lyrics);
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32BigEndian((await Until(0x5e)).Payload));
        Assert.InRange(BinaryPrimitives.ReadUInt64BigEndian((await Until(0x56)).Payload), 90000UL, 93000UL);
        state.Update(null);
        var cleared = await Until(0x12);
        Assert.True(MiPlaySetMediaInfoPayloadCodec.TryDecode(cleared.Payload, out info));
        Assert.Equal("System Audio", info!.Title); Assert.Empty(info.Album); Assert.Empty(info.Art);
        Assert.Equal("", info.Lyrics);
        Assert.Equal(0UL, BinaryPrimitives.ReadUInt64BigEndian((await Until(0x56)).Payload));
        Assert.False(harness.Loop.IsCompleted);
    }

    [Fact]
    public async Task VolumeTimeoutPreservesLoopConfirmedVolumeAndPartiallyReadLateReply()
    {
        await using var harness = await Harness.StartAsync(tabletHeartbeat: true);
        var heartbeat = await harness.Read();
        await harness.Send(0x1b, heartbeat.Sequence);
        var first = harness.ChangeVolume(26);
        var initial = await harness.Read();
        await harness.Send(0x0d, initial.Sequence, MiPlayLegacyStatusScalarCodec.Encode(26));
        await first;
        var change = harness.ChangeVolume(57);
        var volume = await harness.Read();
        var lateReply = MiPlayCommandFrameCodec.Encode(0x0d, volume.Sequence, MiPlayLegacyStatusScalarCodec.Encode(57));
        await harness.PeerStream.WriteAsync(lateReply.AsMemory(0, 4), harness.Token);
        await Assert.ThrowsAsync<MiPlayVolumeAcknowledgementTimeoutException>(() => change);
        Assert.False(harness.Loop.IsCompleted);
        Assert.Equal(26, harness.Runner.ReceiverVolume);
        var retry = harness.ChangeVolume(28);
        var next = await harness.Read();
        Assert.Equal((ushort)0x0c, next.Command);
        await harness.PeerStream.WriteAsync(lateReply.AsMemory(4), harness.Token);
        await harness.Send(0x0d, next.Sequence, MiPlayLegacyStatusScalarCodec.Encode(28));
        await retry;
        Assert.Equal(28, harness.Runner.ReceiverVolume);
        Assert.False(harness.Loop.IsCompleted);
    }

    [Fact]
    public async Task SharedLoopKeepsSendingAfterMissingAckAndPreservesFragmentedLateReplies()
    {
        await using var harness = await Harness.StartAsync(tabletHeartbeat: true);
        var firstHeartbeat = await harness.Read();
        Assert.Equal(MiPlayProtocolConstants.HeartbeatCommand, firstHeartbeat.Command);
        Assert.Equal((ushort)16, firstHeartbeat.Sequence);

        // No heartbeat ACK: volume must still be sent and acknowledged now.
        var change = harness.ChangeVolume(27);
        var volume = await harness.Read();
        Assert.Equal(MiPlayProtocolConstants.SetVolumeCommand, volume.Command);
        Assert.Equal((ushort)17, volume.Sequence);
        for (ushort index = 0; index < 32; index++)
        {
            await harness.Send(MiPlayProtocolConstants.NotifyCommand, index, [1]);
        }
        await harness.Send(MiPlayProtocolConstants.SetVolumeAcknowledgementCommand, volume.Sequence, volume.Payload);
        await change.WaitAsync(harness.Token);
        Assert.Equal(27, harness.Runner.ReceiverVolume);

        // Leave an old ACK half-read across the previous three-second limit.
        // The same persistent reader must finish it, without losing framing.
        var oldAck = MiPlayCommandFrameCodec.Encode(
            MiPlayProtocolConstants.HeartbeatAcknowledgementCommand, firstHeartbeat.Sequence, []);
        await harness.PeerStream.WriteAsync(oldAck.AsMemory(0, 4), harness.Token);
        var secondHeartbeat = await harness.Read();
        Assert.Equal(MiPlayProtocolConstants.HeartbeatCommand, secondHeartbeat.Command);
        Assert.Equal((ushort)18, secondHeartbeat.Sequence);
        Assert.False(harness.Loop.IsCompleted);
        await harness.PeerStream.WriteAsync(oldAck.AsMemory(4), harness.Token);
        await harness.Send(MiPlayProtocolConstants.HeartbeatCommand, 0xabcd);
        var reply = await harness.Read();
        Assert.Equal(MiPlayProtocolConstants.HeartbeatAcknowledgementCommand, reply.Command);
        Assert.Equal((ushort)0xabcd, reply.Sequence);
        Assert.Empty(reply.Payload);

        var nextChange = harness.ChangeVolume(28);
        var nextVolume = await harness.Read();
        Assert.Equal((ushort)19, nextVolume.Sequence);
        await harness.Send(MiPlayProtocolConstants.HeartbeatAcknowledgementCommand, secondHeartbeat.Sequence);
        await harness.Send(MiPlayProtocolConstants.SetVolumeAcknowledgementCommand, nextVolume.Sequence, nextVolume.Payload);
        await nextChange.WaitAsync(harness.Token);
        Assert.Equal(28, harness.Runner.ReceiverVolume);
    }

    [Fact]
    public async Task LegacySingleSpeakerStillRequiresTheIndividualHeartbeatAck()
    {
        await using var harness = await Harness.StartAsync(tabletHeartbeat: false);
        var heartbeat = await harness.Read();
        Assert.Equal(MiPlayProtocolConstants.HeartbeatCommand, heartbeat.Command);
        var exception = await Assert.ThrowsAsync<TimeoutException>(() => harness.Loop.WaitAsync(harness.Token));
        Assert.Contains("0x001A sequence 0x0010 was not acknowledged", exception.Message);
    }

    [Fact]
    public async Task SocketClosureWhileTheWriterIsIdleIsObservedByThePersistentReader()
    {
        await using var harness = await Harness.StartAsync(tabletHeartbeat: true);
        await harness.Read();
        harness.Peer.Dispose();
        await Assert.ThrowsAsync<EndOfStreamException>(() => harness.Loop.WaitAsync(harness.Token));
    }

    [Fact]
    public async Task RealFifteenSecondInactivityWindowStillEndsAnUnresponsiveConnection()
    {
        await using var harness = await Harness.StartAsync(tabletHeartbeat: true);
        var started = Stopwatch.GetTimestamp();
        await harness.Read();
        var exception = await Assert.ThrowsAsync<TimeoutException>(() => harness.Loop.WaitAsync(harness.Token));
        Assert.Contains("fifteen seconds", exception.Message);
        Assert.True(Stopwatch.GetElapsedTime(started) >= TimeSpan.FromSeconds(14));
    }

    [Fact]
    public async Task ReadOnlyQueriesObserveVolumeAndGroupWithoutRestartingOrWaitingForMissingReplies()
    {
        await using var harness = await Harness.StartAsync(tabletHeartbeat: true, observe: true);
        var heartbeat = await harness.Read();
        await harness.Send(MiPlayProtocolConstants.HeartbeatAcknowledgementCommand, heartbeat.Sequence);
        var volume = await harness.Read();
        Assert.Equal((ushort)0x000e, volume.Command);
        await harness.Send(0x000f, volume.Sequence, MiPlayLegacyStatusScalarCodec.Encode(47));
        var identity = await harness.Read();
        Assert.Equal((ushort)0x001e, identity.Command);
        await harness.Send(0x001f, identity.Sequence, MiPlayLegacyDeviceInfoPayloadCodec.Encode(new Dictionary<string, string> {
            ["miName"] = "Art L", ["groupId"] = "pair", ["groupName"] = "Stereo", ["channel"] = "left"
        }));
        for (var i = 0; i < 50 && harness.Runner.ReceiverIdentity is null; i++) await Task.Delay(10);
        Assert.Equal(47, harness.Runner.ReceiverVolume);
        Assert.Equal("pair", harness.Runner.ReceiverIdentity?.GroupId);
        var missing = await harness.Read();
        Assert.Equal((ushort)0x000e, missing.Command); // Deliberately do not acknowledge this read.
        var next = await harness.Read();
        Assert.Contains(next.Command, new ushort[] { MiPlayProtocolConstants.GetVolumeCommand, MiPlayProtocolConstants.HeartbeatCommand });
        Assert.False(harness.Loop.IsCompleted);
    }

    private sealed class Harness : IAsyncDisposable
    {
        public CancellationTokenSource Lifetime { get; } = new(TimeSpan.FromSeconds(25));
        public CancellationToken Token => Lifetime.Token;
        public TcpClient Source { get; }
        public TcpClient Peer { get; }
        public NetworkStream PeerStream => Peer.GetStream();
        public MiPlayLegacySystemAudioSessionRunner Runner { get; }
        public Task Loop { get; }
        private readonly object volume;

        private Harness(TcpClient source, TcpClient peer, bool tabletHeartbeat, bool observe, MiPlayMediaState? media)
        {
            Runner = new(new UnusedAudioCatalog(), observeReceiverState: observe, mediaState: media);
            Source = source;
            Peer = peer;
            var runnerType = typeof(MiPlayLegacySystemAudioSessionRunner);
            object CreateNested(string name) => Activator.CreateInstance(
                runnerType.GetNestedType(name, BindingFlags.NonPublic)!, nonPublic: true)!;
            volume = CreateNested("ActiveVolumeControl");
            var playback = CreateNested("ActiveSourcePlaybackControl");
            var timeline = CreateNested("ActiveMediaTimeline");
            var run = runnerType.GetMethod("RunRuntimeControlsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            Loop = (Task)run.Invoke(Runner,
                [source.GetStream(), Stopwatch.GetTimestamp() - 5 * Stopwatch.Frequency, volume, playback,
                    new MiPlayRuntimeTimeOffsetControl(), timeline, Token, tabletHeartbeat,
                    (Action<string>)(_ => { }), null, null, null, null, null])!;
        }

        public static async Task<Harness> StartAsync(bool tabletHeartbeat, bool observe = false, MiPlayMediaState? media = null)
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var peer = new TcpClient { NoDelay = true };
            await peer.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            var source = await listener.AcceptTcpClientAsync();
            source.NoDelay = true;
            return new(source, peer, tabletHeartbeat, observe, media);
        }

        public Task ChangeVolume(int value) => (Task)volume.GetType().GetMethod("EnqueueAsync")!
            .Invoke(volume, [value, Token])!;

        public async Task<MiPlayCommandFrame> Read()
        {
            var header = new byte[9];
            await PeerStream.ReadExactlyAsync(header, Token);
            var length = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(5, 4));
            Assert.InRange(length, 0u, (uint)MiPlayCommandFrameCodec.MaximumPayloadLength);
            var frameBytes = new byte[header.Length + length];
            header.CopyTo(frameBytes, 0);
            await PeerStream.ReadExactlyAsync(frameBytes.AsMemory(9), Token);
            Assert.True(MiPlayCommandFrameCodec.TryDecode(frameBytes, out var frame, out var consumed));
            Assert.Equal(frameBytes.Length, consumed);
            return Assert.IsType<MiPlayCommandFrame>(frame);
        }

        public Task Send(ushort command, ushort sequence, byte[]? payload = null) =>
            PeerStream.WriteAsync(MiPlayCommandFrameCodec.Encode(command, sequence, payload ?? []), Token).AsTask();

        public async ValueTask DisposeAsync()
        {
            Lifetime.Cancel();
            try { await Loop; }
            catch { /* Assertions above own the expected failure. Always drain the loop before closing sockets. */ }
            Source.Dispose();
            Peer.Dispose();
            Lifetime.Dispose();
        }
    }

    private sealed class UnusedAudioCatalog : IAudioSourceCatalog
    {
        public IReadOnlyList<AudioSourceItem> GetOutputDevices() => [];
        public IReadOnlyList<AudioSourceItem> GetCandidateProcesses() => [];
        public IAudioCaptureSource CreateCapture(CaptureSelection selection) => throw new NotSupportedException();
    }
}
