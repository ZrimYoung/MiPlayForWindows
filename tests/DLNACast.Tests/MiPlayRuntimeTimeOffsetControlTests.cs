using DLNACast.Core.MiPlay;

namespace DLNACast.Tests;

public sealed class MiPlayRuntimeTimeOffsetControlTests
{
    [Fact]
    public async Task LiveDelayIsAbsoluteAndSurvivesRecoveryWithoutChangingGroupClock()
    {
        var control = new MiPlayRuntimeTimeOffsetControl(false, initialPlaybackDelayMicroseconds: 1_000_000);
        ulong offset = 100_000_000;
        async Task Apply(Task pending, ulong expected)
        {
            Assert.True(control.TryRead(out var request)); Assert.True(request.TryStart());
            offset = control.Resolve(request, offset);
            Assert.Equal(expected, offset);
            request.Succeed(); await pending;
        }
        await Apply(control.SetPlaybackDelayAsync(1_200_000, CancellationToken.None), 100_200_000);
        await Apply(control.SetPlaybackDelayAsync(300_000, CancellationToken.None), 99_300_000);
        // Transport recovery replaces the base clock, preserving the -700 ms target.
        await Apply(control.UpdateAsync(110_000_000, CancellationToken.None), 109_300_000);
        await Apply(control.SetPlaybackDelayAsync(1_000_000, CancellationToken.None), 110_000_000);
        await Apply(control.SetPlaybackDelayAsync(0, CancellationToken.None), 109_000_000);
        await Apply(control.SetPlaybackDelayAsync(2_000_000, CancellationToken.None), 111_000_000);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => control.SetPlaybackDelayAsync(2_000_001, CancellationToken.None));
    }

    [Fact]
    public async Task CompletesOnlyAfterTheRtspOwnerAcknowledgesTheRequest()
    {
        var control = new MiPlayRuntimeTimeOffsetControl();
        var update = control.UpdateAsync(9_700_000_000, CancellationToken.None);

        Assert.True(await control.WaitToReadAsync(CancellationToken.None));
        Assert.True(control.TryRead(out var request));
        Assert.Equal(9_700_000_000UL, request.TimeOffsetMicroseconds);
        Assert.True(request.TryStart());
        Assert.False(update.IsCompleted);

        request.Succeed();

        await update;
    }

    [Fact]
    public async Task SharedRequestStillWaitsForTheRtspOwnerToFinishWriting()
    {
        var control = new MiPlayRuntimeTimeOffsetControl(waitForAcknowledgement: false);
        Assert.False(control.WaitForAcknowledgement);
        var first = control.UpdateAsync(1_000_000, CancellationToken.None);
        var second = control.UpdateAsync(2_000_000, CancellationToken.None);
        Assert.True(control.TryRead(out var firstRequest));
        Assert.True(firstRequest.TryStart());
        Assert.False(first.IsCompleted);
        firstRequest.Succeed();
        await first;
        Assert.False(second.IsCompleted);
        firstRequest.Succeed();
        Assert.False(second.IsCompleted);
        Assert.True(control.TryRead(out var secondRequest));
        Assert.True(secondRequest.TryStart());
        secondRequest.Succeed();
        await second;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedWritesAreNotReportedAsSuccessfulUpdates(bool waitForAcknowledgement)
    {
        var control = new MiPlayRuntimeTimeOffsetControl(waitForAcknowledgement);
        var update = control.UpdateAsync(1_000_000, CancellationToken.None);
        Assert.True(control.TryRead(out var request));
        Assert.True(request.TryStart());
        var failure = new IOException("The RTSP socket write failed.");
        request.Fail(failure);
        request.Succeed();
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => update));
    }

    [Fact]
    public async Task CancelledQueuedSharedUpdateCannotBeWrittenLater()
    {
        var control = new MiPlayRuntimeTimeOffsetControl(waitForAcknowledgement: false);
        using var lifetime = new CancellationTokenSource();
        var update = control.UpdateAsync(1_000_000, lifetime.Token);
        lifetime.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => update);
        Assert.True(control.TryRead(out var request));
        Assert.False(request.TryStart());
    }

    [Fact]
    public async Task DisconnectFailsPendingSharedUpdates()
    {
        var control = new MiPlayRuntimeTimeOffsetControl(waitForAcknowledgement: false);
        var update = control.UpdateAsync(1_000_000, CancellationToken.None);
        var failure = new EndOfStreamException("The RTSP peer disconnected.");
        control.Complete(failure);
        Assert.Same(failure, await Assert.ThrowsAsync<EndOfStreamException>(() => update));
    }
}
