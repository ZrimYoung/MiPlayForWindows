using System.Threading.Channels;

namespace DLNACast.Core.MiPlay;

/// <summary>
/// Serializes source-initiated runtime TIME_OFFSET requests onto the single
/// reverse RTSP connection. The owner completes shared requests after writing;
/// single-receiver requests still require the matching acknowledgement.
/// </summary>
internal sealed class MiPlayRuntimeTimeOffsetControl
{
    private static readonly TimeSpan CompletionTimeout = TimeSpan.FromSeconds(3);
    private readonly Channel<MiPlayRuntimeTimeOffsetRequest> requests =
        Channel.CreateUnbounded<MiPlayRuntimeTimeOffsetRequest>(
            new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false,
            });

    private readonly int initialPlaybackDelayMicroseconds;
    private int playbackDelayMicroseconds;
    private ulong? baseTimeOffset;

    public MiPlayRuntimeTimeOffsetControl(bool waitForAcknowledgement = true, int initialPlaybackDelayMicroseconds = 1_000_000)
    {
        ValidateDelay(initialPlaybackDelayMicroseconds);
        WaitForAcknowledgement = waitForAcknowledgement;
        this.initialPlaybackDelayMicroseconds = playbackDelayMicroseconds = initialPlaybackDelayMicroseconds;
    }

    private static void ValidateDelay(int value)
    {
        if (value is < 0 or > 2_000_000) throw new ArgumentOutOfRangeException(nameof(value));
    }

    // Only the RTSP owner resolves requests. Keep the original group clock separate
    // from the user's adjustment so recovery/resume cannot overwrite or compound it.
    internal ulong Resolve(MiPlayRuntimeTimeOffsetRequest request, ulong currentTimeOffset)
    {
        baseTimeOffset ??= currentTimeOffset;
        if (request.PlaybackDelayMicroseconds is int delay) playbackDelayMicroseconds = delay;
        else baseTimeOffset = request.TimeOffsetMicroseconds;
        var delta = (long)playbackDelayMicroseconds - initialPlaybackDelayMicroseconds;
        return delta >= 0 ? checked(baseTimeOffset.Value + (ulong)delta)
            : checked(baseTimeOffset.Value - (ulong)-delta);
    }

    public Task SetPlaybackDelayAsync(int microseconds, CancellationToken token)
    {
        ValidateDelay(microseconds);
        return EnqueueAsync(new MiPlayRuntimeTimeOffsetRequest(0, microseconds), token);
    }

    public bool WaitForAcknowledgement { get; }

    public Task UpdateAsync(ulong timeOffsetMicroseconds, CancellationToken cancellationToken) =>
        EnqueueAsync(new MiPlayRuntimeTimeOffsetRequest(timeOffsetMicroseconds), cancellationToken);

    private async Task EnqueueAsync(MiPlayRuntimeTimeOffsetRequest request, CancellationToken cancellationToken)
    {
        await requests.Writer.WriteAsync(request, cancellationToken).ConfigureAwait(false);
        using var cancellationRegistration = cancellationToken.Register(
            static state => ((MiPlayRuntimeTimeOffsetRequest)state!).CancelIfPending(),
            request);
        try
        {
            await request.Completion
                .WaitAsync(CompletionTimeout, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException exception)
        {
            var timeout = new TimeoutException(
                WaitForAcknowledgement
                    ? "The receiver did not acknowledge the runtime MiPlay TIME_OFFSET within three seconds."
                    : "The source did not write the runtime MiPlay TIME_OFFSET within three seconds.",
                exception);
            request.Fail(timeout);
            throw timeout;
        }
    }

    public bool TryRead(out MiPlayRuntimeTimeOffsetRequest request) =>
        requests.Reader.TryRead(out request!);

    public ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken) =>
        requests.Reader.WaitToReadAsync(cancellationToken);

    public void Complete(Exception reason)
    {
        ArgumentNullException.ThrowIfNull(reason);
        requests.Writer.TryComplete(reason);
        while (requests.Reader.TryRead(out var request))
        {
            request.Fail(reason);
        }
    }
}

internal sealed class MiPlayRuntimeTimeOffsetRequest(ulong timeOffsetMicroseconds, int? playbackDelayMicroseconds = null)
{
    private readonly TaskCompletionSource completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int state;

    public ulong TimeOffsetMicroseconds { get; } = timeOffsetMicroseconds;
    public int? PlaybackDelayMicroseconds { get; } = playbackDelayMicroseconds;
    public Task Completion => completion.Task;

    public bool TryStart() => Interlocked.CompareExchange(ref state, 1, 0) == 0;

    public void CancelIfPending()
    {
        if (Interlocked.CompareExchange(ref state, 2, 0) == 0)
        {
            completion.TrySetCanceled();
        }
    }

    public void Succeed()
    {
        if (Interlocked.CompareExchange(ref state, 2, 1) == 1)
        {
            completion.TrySetResult();
        }
    }

    public void Fail(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var previous = Interlocked.Exchange(ref state, 2);
        if (previous != 2)
        {
            completion.TrySetException(exception);
        }
    }
}
