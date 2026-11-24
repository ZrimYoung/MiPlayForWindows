namespace DLNACast.Core.Audio;

/// <summary>
/// A synchronous borrowed view. It does not own or copy sample memory and must
/// not be retained after the sink call; asynchronous consumers must copy first.
/// The timestamp marks the end of the captured block on the monotonic clock.
/// </summary>
public readonly ref struct RawPcmBlockView
{
    public RawPcmBlockView(ReadOnlySpan<byte> bytes, RawPcmFormat format, int frameCount,
        ulong endTimeNanoseconds, bool discontinuity = false)
    {
        ArgumentNullException.ThrowIfNull(format);
        if (frameCount <= 0) throw new ArgumentOutOfRangeException(nameof(frameCount));
        if (bytes.Length != format.GetByteCount(frameCount))
            throw new ArgumentException("A raw PCM block must contain complete frames in its declared format.", nameof(bytes));

        Bytes = bytes;
        Format = format;
        FrameCount = frameCount;
        EndTimeNanoseconds = endTimeNanoseconds;
        Discontinuity = discontinuity;
    }

    public ReadOnlySpan<byte> Bytes { get; }
    public RawPcmFormat Format { get; }
    public int FrameCount { get; }
    public ulong EndTimeNanoseconds { get; }
    public bool Discontinuity { get; }
    public ulong DurationNanoseconds => Format.GetDurationNanoseconds(FrameCount);
}
