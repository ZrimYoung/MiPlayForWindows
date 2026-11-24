using System.Diagnostics;
using DLNACast.Core.Abstractions;

namespace DLNACast.Core.Audio;

/// <summary>
/// Runs a platform's blocking monotonic wait on an owned thread. Audio capture,
/// encoder pipes and other receivers never lend their thread-pool workers to it.
/// </summary>
internal sealed class DedicatedMediaPacingClock : IMediaPacingClock
{
    private readonly Lock gate = new();
    private readonly AutoResetEvent wake = new(false);
    private readonly Action<ulong> waitNanoseconds;
    private readonly Thread worker;
    private Request? pending;
    private bool disposed;

    internal DedicatedMediaPacingClock(Action<ulong> waitNanoseconds)
    {
        this.waitNanoseconds = waitNanoseconds ?? throw new ArgumentNullException(nameof(waitNanoseconds));
        worker = new Thread(Run) { IsBackground = true, Name = "MiPlay media timing" };
        try { worker.Start(); }
        catch { wake.Dispose(); throw; }
    }

    public ValueTask WaitUntilAsync(long targetTimestamp, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            if (pending is not null)
                throw new InvalidOperationException("A media clock permits one pending deadline.");
            if (targetTimestamp <= Stopwatch.GetTimestamp()) return ValueTask.CompletedTask;
            var request = new Request(targetTimestamp, cancellationToken);
            pending = request;
            wake.Set();
            return new ValueTask(request.Completion.Task);
        }
    }

    private void Run()
    {
        while (true)
        {
            wake.WaitOne();
            while (true)
            {
                Request request;
                lock (gate)
                {
                    if (disposed) return;
                    if (pending is null) break;
                    request = pending;
                }
                Exception? failure = null;
                try
                {
                    request.Token.ThrowIfCancellationRequested();
                    var remaining = request.Target - Stopwatch.GetTimestamp();
                    if (remaining > 0)
                    {
                        // Bounded native sleeps also bound stop/cancellation latency.
                        var nanos = (ulong)Math.Clamp(remaining * 1_000_000_000d / Stopwatch.Frequency, 1, 20_000_000);
                        waitNanoseconds(nanos);
                        continue;
                    }
                }
                catch (Exception exception) { failure = exception; }
                lock (gate)
                {
                    if (disposed) return;
                    pending = null;
                }
                if (failure is OperationCanceledException)
                    request.Completion.TrySetCanceled(request.Token);
                else if (failure is not null)
                    request.Completion.TrySetException(failure);
                else request.Completion.TrySetResult();
            }
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            pending?.Completion.TrySetException(new ObjectDisposedException(nameof(DedicatedMediaPacingClock)));
            pending = null;
            wake.Set();
        }
        worker.Join();
        wake.Dispose();
    }

    private sealed class Request(long target, CancellationToken token)
    {
        internal long Target { get; } = target;
        internal CancellationToken Token { get; } = token;
        internal TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
