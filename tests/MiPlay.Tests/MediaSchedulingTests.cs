using System.Diagnostics;
using DLNACast.Core.Audio;

namespace MiPlay.Tests;

public sealed class MediaSchedulingTests
{
    [Fact]
    public async Task NativeWaitDoesNotBlockItsCallerOrRunOnTheThreadPool()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        Thread? timingThread = null;
        using var clock = new DedicatedMediaPacingClock(_ =>
        {
            timingThread = Thread.CurrentThread;
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(2))) throw new TimeoutException();
        });
        try
        {
            var pending = clock.WaitUntilAsync(Stopwatch.GetTimestamp() + 5 * Stopwatch.Frequency, cancellation.Token).AsTask();
            Assert.True(entered.Wait(TimeSpan.FromSeconds(1)));
            Assert.False(pending.IsCompleted);
            Assert.NotNull(timingThread);
            Assert.False(timingThread.IsThreadPoolThread);
            cancellation.Cancel();
            release.Set();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(1)));
        }
        finally { release.Set(); }
    }

    [Fact]
    public async Task DeadlinesAreMonotonicAndCancellationDoesNotPoisonTheNextWait()
    {
        using var clock = new DedicatedMediaPacingClock(Sleep);
        for (var i = 0; i < 3; i++)
        {
            var target = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 100;
            await clock.WaitUntilAsync(target, default).AsTask().WaitAsync(TimeSpan.FromSeconds(1));
            Assert.True(Stopwatch.GetTimestamp() >= target);
        }
        using var cancellation = new CancellationTokenSource();
        var canceled = clock.WaitUntilAsync(Stopwatch.GetTimestamp() + 5 * Stopwatch.Frequency, cancellation.Token).AsTask();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled.WaitAsync(TimeSpan.FromSeconds(1)));
        await clock.WaitUntilAsync(Stopwatch.GetTimestamp(), default);
    }

    [Fact]
    public async Task DisposeStopsTheTimingThreadAndCompletesAPendingDeadline()
    {
        using var entered = new ManualResetEventSlim();
        Thread? worker = null;
        var clock = new DedicatedMediaPacingClock(nanos =>
        {
            worker = Thread.CurrentThread;
            entered.Set();
            Sleep(nanos);
        });
        var pending = clock.WaitUntilAsync(Stopwatch.GetTimestamp() + 10 * Stopwatch.Frequency, default).AsTask();
        Assert.True(entered.Wait(TimeSpan.FromSeconds(1)));
        clock.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => pending);
        Assert.False(worker!.IsAlive);
        Assert.Throws<ObjectDisposedException>(() => clock.WaitUntilAsync(0, default));
        clock.Dispose();
    }

    private static void Sleep(ulong nanos) => Thread.Sleep(TimeSpan.FromMilliseconds(Math.Ceiling(nanos / 1_000_000d)));

    [Fact]
    public async Task CaptureTransferDrainsBurstsWithoutASecondClockAndRetainsTiming()
    {
        await using var buffer = new PcmFrameBuffer(AudioChannelRoute.Stereo, PcmFrameFormat.MiPlayAac);
        var waiting = buffer.ReadCapturedFrameAsync(default).AsTask();
        Assert.False(waiting.IsCompleted);
        var bytes = new byte[buffer.FrameByteCount];
        bytes[0] = 7;
        buffer.Write(bytes, 123, captureDiscontinuity: true);
        var first = await waiting.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(123UL, first.PresentationTimeMicroseconds);
        Assert.True(first.CaptureDiscontinuity);
        for (var i = 0; i < 3; i++) { bytes[0] = (byte)i; buffer.Write(bytes, (ulong)i + 200); }
        for (var i = 0; i < 3; i++)
        {
            var read = buffer.ReadCapturedFrameAsync(default);
            Assert.True(read.IsCompletedSuccessfully);
            Assert.Equal((byte)i, (await read).Bytes[0]);
        }
        Assert.Equal(0, buffer.Underruns);
    }

    [Fact]
    public async Task CaptureTransferRemainsBoundedAndCompletionWakesItsReader()
    {
        await using var buffer = new PcmFrameBuffer(AudioChannelRoute.Stereo, PcmFrameFormat.MiPlayAac, maximumMilliseconds: 44);
        var bytes = new byte[buffer.FrameByteCount];
        for (var i = 0; i < 4; i++) { bytes[0] = (byte)i; buffer.Write(bytes); }
        Assert.Equal(2, buffer.Overruns);
        Assert.Equal(2, (await buffer.ReadCapturedFrameAsync(default)).Bytes[0]);
        Assert.Equal(3, (await buffer.ReadCapturedFrameAsync(default)).Bytes[0]);
        var waiting = buffer.ReadCapturedFrameAsync(default).AsTask();
        buffer.Complete();
        await Assert.ThrowsAsync<EndOfStreamException>(() => waiting.WaitAsync(TimeSpan.FromSeconds(1)));
    }
}
