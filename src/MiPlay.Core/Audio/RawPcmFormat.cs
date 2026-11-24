namespace DLNACast.Core.Audio;

public enum RawPcmSampleType
{
    SignedInt16,
    SignedInt24,
    SignedInt32,
    Float32
}

/// <summary>
/// Describes captured PCM independently of the format required by a transport.
/// Planar blocks store complete channel planes consecutively in channel order.
/// Sample bytes use the capture process's native endianness.
/// </summary>
public sealed record RawPcmFormat
{
    public RawPcmFormat(double sampleRate, int channels, RawPcmSampleType sampleType,
        bool interleaved = true, IReadOnlyList<uint>? channelLabels = null,
        string? channelLayoutNamespace = null)
    {
        if (!double.IsFinite(sampleRate) || sampleRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (channels <= 0) throw new ArgumentOutOfRangeException(nameof(channels));
        if (!Enum.IsDefined(sampleType)) throw new ArgumentOutOfRangeException(nameof(sampleType));
        if (channelLayoutNamespace is not null && string.IsNullOrWhiteSpace(channelLayoutNamespace))
            throw new ArgumentException("A channel-layout namespace must be nonempty.", nameof(channelLayoutNamespace));
        if (channelLabels is { Count: > 0 } && channelLabels.Count != channels)
            throw new ArgumentException("Channel labels must describe every channel, or be omitted.", nameof(channelLabels));
        if (channelLabels is { Count: > 0 } && channelLayoutNamespace is null)
            throw new ArgumentException("Channel labels require their platform namespace.", nameof(channelLayoutNamespace));

        SampleRate = sampleRate;
        Channels = channels;
        SampleType = sampleType;
        Interleaved = interleaved;
        BitsPerSample = sampleType switch
        {
            RawPcmSampleType.SignedInt16 => 16,
            RawPcmSampleType.SignedInt24 => 24,
            _ => 32
        };
        BytesPerFrame = checked(channels * BytesPerSample);
        ChannelLabels = channelLabels is { Count: > 0 }
            ? Array.AsReadOnly(channelLabels.ToArray()) : Array.Empty<uint>();
        ChannelLayoutNamespace = channelLayoutNamespace;
    }

    public double SampleRate { get; }
    public int Channels { get; }
    public RawPcmSampleType SampleType { get; }
    public bool Interleaved { get; }
    public int BitsPerSample { get; }
    public int BytesPerSample => BitsPerSample / 8;
    /// <summary>Total bytes for one sample from every channel, including for planar blocks.</summary>
    public int BytesPerFrame { get; }
    public IReadOnlyList<uint> ChannelLabels { get; }
    public string? ChannelLayoutNamespace { get; }

    public int GetByteCount(int frameCount)
    {
        if (frameCount < 0) throw new ArgumentOutOfRangeException(nameof(frameCount));
        return checked(frameCount * BytesPerFrame);
    }

    public ulong GetDurationNanoseconds(int frameCount)
    {
        if (frameCount < 0) throw new ArgumentOutOfRangeException(nameof(frameCount));
        if (frameCount == 0) return 0;
        var duration = frameCount * 1_000_000_000d / SampleRate;
        // The checked cast also rejects non-finite or unrepresentable durations.
        return checked((ulong)duration);
    }

    public bool Equals(RawPcmFormat? other) => ReferenceEquals(this, other) || other is not null &&
        SampleRate == other.SampleRate && Channels == other.Channels && SampleType == other.SampleType &&
        Interleaved == other.Interleaved && ChannelLayoutNamespace == other.ChannelLayoutNamespace &&
        ChannelLabels.SequenceEqual(other.ChannelLabels);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(SampleRate); hash.Add(Channels); hash.Add(SampleType); hash.Add(Interleaved);
        hash.Add(ChannelLayoutNamespace, StringComparer.Ordinal);
        foreach (var label in ChannelLabels) hash.Add(label);
        return hash.ToHashCode();
    }
}
