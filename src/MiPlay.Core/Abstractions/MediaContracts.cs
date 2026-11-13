using System.Diagnostics;
using DLNACast.Core.MiPlay;

namespace DLNACast.Core.Abstractions;

public interface IAacEncoder : IAsyncDisposable
{
    ValueTask WritePcmAsync(ReadOnlyMemory<byte> pcm, CancellationToken cancellationToken = default);
    ValueTask CompleteInputAsync(CancellationToken cancellationToken = default);
    ValueTask<byte[]?> ReadAccessUnitAsync(CancellationToken cancellationToken = default);
}

public interface IAacEncoderFactory
{
    IAacEncoder Start(string executablePath, int bitRate);
}

public sealed class FfmpegAacEncoderFactory(string codecName) : IAacEncoderFactory
{
    public IAacEncoder Start(string executablePath, int bitRate) =>
        MiPlayFfmpegAacEncoder.Start(executablePath, bitRate, codecName);
}

public interface IMediaPacingClock : IDisposable
{
    ValueTask WaitUntilAsync(long targetTimestamp, CancellationToken cancellationToken);
}

/// <summary>A cancellable monotonic fallback, independent of UI timers and wall-clock changes.</summary>
public sealed class MonotonicMediaPacingClock : IMediaPacingClock
{
    public async ValueTask WaitUntilAsync(long targetTimestamp, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remaining = (targetTimestamp - Stopwatch.GetTimestamp()) / (double)Stopwatch.Frequency;
            if (remaining <= 0) return;
            if (remaining > .002)
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(.02, remaining - .001)), cancellationToken).ConfigureAwait(false);
            else
                Thread.SpinWait(64);
        }
    }

    public void Dispose() { }
}
