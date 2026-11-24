using System.Diagnostics;
using DLNACast.Core.Audio;
using DLNACast.Core.MiPlay;

namespace MiPlay.Tests;

public sealed class SlowReceiverIsolationTests
{
    [Fact]
    public async Task BlockedReceiverReleasesTheLiveGroupBeforeItsTransportIsCleanedUp()
    {
        var broadcast = new MiPlaySharedAccessUnitBroadcast(2);
        var sends = new MiPlaySharedSendCompletionCoordinator(2);
        var healthyAudio = broadcast.Register(AudioChannelRoute.Stereo);
        var slowAudio = broadcast.Register(AudioChannelRoute.Stereo);
        var healthySend = sends.Register(); var slowSend = sends.Register();
        var origin = Stopwatch.GetTimestamp();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var first = broadcast.PublishAsync(Stereo(0), timeout.Token);
        await broadcast.ReadIndexedAsync(healthyAudio, timeout.Token);
        await broadcast.ReadIndexedAsync(slowAudio, timeout.Token);
        await first;
        var healthyCompletion = sends.SynchronizeAsync(healthySend, 0, origin, 0, origin,
            Stopwatch.Frequency, false, timeout.Token);
        Assert.False(healthyCompletion.IsCompleted);

        // The stalled member has prefetched no next AU. Both the publisher and
        // the healthy sender are blocked until its write failure detaches it.
        var next = broadcast.PublishAsync(Stereo(1), timeout.Token);
        var healthyNext = await broadcast.ReadIndexedAsync(healthyAudio, timeout.Token);
        Assert.False(next.IsCompleted);
        using var socket = new BlockedStream();
        var detached = false;
        var failure = await Assert.ThrowsAsync<TimeoutException>(() =>
            MiPlayLegacySystemAudioSessionRunner.WriteSharedMediaAsync(socket, new byte[256],
                TimeSpan.FromMilliseconds(30), timeout.Token, () =>
                {
                    broadcast.Unregister(slowAudio);
                    sends.Unregister(slowSend);
                    detached = true;
                    return ValueTask.CompletedTask;
                }));
        Assert.True(detached);
        Assert.Contains("30 milliseconds", failure.Message);
        await next.WaitAsync(timeout.Token);
        await healthyCompletion.WaitAsync(timeout.Token);
        await sends.SynchronizeAsync(healthySend, healthyNext.AccessUnitIndex, Stopwatch.GetTimestamp(),
            0, origin, Stopwatch.Frequency, false, timeout.Token);
        Assert.Equal(1, sends.LastCompletedIndex);
    }

    [Fact]
    public async Task UserStopRemainsCancellationAndAlsoDetachesTheReceiver()
    {
        using var stop = new CancellationTokenSource();
        using var socket = new BlockedStream();
        var detached = false;
        var pending = MiPlayLegacySystemAudioSessionRunner.WriteSharedMediaAsync(socket, new byte[1],
            TimeSpan.FromSeconds(3), stop.Token, () => { detached = true; return ValueTask.CompletedTask; });
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.True(detached);
    }

    private static Dictionary<AudioChannelRoute, MiPlayEncodedAccessUnit> Stereo(long index) =>
        new() { [AudioChannelRoute.Stereo] = new([(byte)index], (ulong)index * 21_333) };

    private sealed class BlockedStream : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            new(Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken));
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
