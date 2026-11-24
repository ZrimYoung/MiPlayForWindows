using System.Runtime.InteropServices;
using System.Text.Json;
using DLNACast.Core.Abstractions;
using DLNACast.Core.Audio;
using DLNACast.Core.Models;

namespace MiPlay.Tests;

public sealed class RawPcmTests
{
    [Theory]
    [InlineData(1, RawPcmSampleType.SignedInt16, 16)]
    [InlineData(2, RawPcmSampleType.SignedInt24, 24)]
    [InlineData(6, RawPcmSampleType.SignedInt32, 32)]
    [InlineData(8, RawPcmSampleType.Float32, 32)]
    public void SourceDimensionsDetermineByteCountsForBothMemoryLayouts(int channels,
        RawPcmSampleType type, int bits)
    {
        foreach (var interleaved in new[] { true, false })
        {
            var format = new RawPcmFormat(96000, channels, type, interleaved);
            Assert.Equal(bits, format.BitsPerSample);
            Assert.Equal(channels * bits / 8, format.BytesPerFrame);
            Assert.Equal(960 * channels * bits / 8, format.GetByteCount(960));
            Assert.Equal(10_000_000UL, format.GetDurationNanoseconds(960));
            Assert.Equal(interleaved, format.Interleaved);
        }
        Assert.Equal(2, PcmFrameFormat.MiPlayAac.Channels);
        Assert.Equal(16, PcmFrameFormat.MiPlayAac.BitsPerSample);
    }

    [Theory]
    [InlineData(44100, 441, 10_000_000UL)]
    [InlineData(48000, 1024, 21_333_333UL)]
    [InlineData(192000, 1920, 10_000_000UL)]
    public void BlockDurationUsesSourceRateInsteadOfTheSendingRate(double rate, int frames, ulong duration)
    {
        var format = new RawPcmFormat(rate, 8, RawPcmSampleType.Float32);
        var block = new RawPcmBlockView(new byte[format.GetByteCount(frames)], format, frames, 0, true);
        Assert.Equal(duration, block.DurationNanoseconds);
        Assert.Equal(0UL, block.EndTimeNanoseconds);
        Assert.True(block.Discontinuity);
    }

    [Theory]
    [InlineData(6, true)]
    [InlineData(6, false)]
    [InlineData(8, true)]
    [InlineData(8, false)]
    public void BorrowedMultichannelSamplesReachTheSinkWithoutDownmixOrMutation(int channels, bool interleaved)
    {
        const int frames = 7;
        var samples = new float[frames * channels];
        for (var frame = 0; frame < frames; frame++)
            for (var channel = 0; channel < channels; channel++)
                samples[interleaved ? frame * channels + channel : channel * frames + frame] = frame * 10 + channel + .25f;
        var bytes = MemoryMarshal.AsBytes(samples.AsSpan()).ToArray();
        var original = bytes.ToArray();
        var format = new RawPcmFormat(96000, channels, RawPcmSampleType.Float32, interleaved);
        var block = new RawPcmBlockView(bytes, format, frames, 123_456);
        var sink = new CopyingSink();
        sink.Write(in block);

        Assert.Equal(original, bytes);
        Assert.Equal(original, sink.Bytes);
        Assert.Same(format, sink.Format);
        Assert.Equal(frames, sink.Frames);
        Assert.Equal(123_456UL, sink.EndTime);
        var received = MemoryMarshal.Cast<byte, float>(sink.Bytes);
        for (var frame = 0; frame < frames; frame++)
            for (var channel = 0; channel < channels; channel++)
                Assert.Equal(frame * 10 + channel + .25f,
                    received[interleaved ? frame * channels + channel : channel * frames + frame]);

        // The producer may immediately reuse its buffer. An asynchronous sink's
        // explicit copy remains stable, while the borrowed view follows the buffer.
        bytes[0] ^= 0xff;
        Assert.Equal(bytes[0], block.Bytes[0]);
        Assert.Equal(original[0], sink.Bytes[0]);
    }

    [Fact]
    public void UnknownSurroundLayoutRemainsUnknownAndPlatformLabelsAreImmutable()
    {
        var unknown = new RawPcmFormat(48000, 8, RawPcmSampleType.Float32);
        Assert.Empty(unknown.ChannelLabels);
        Assert.Null(unknown.ChannelLayoutNamespace);
        var labels = new uint[] { 1, 2, 3, 4, 5, 6 };
        var known = new RawPcmFormat(48000, 6, RawPcmSampleType.Float32,
            channelLabels: labels, channelLayoutNamespace: "coreaudio");
        var same = new RawPcmFormat(48000, 6, RawPcmSampleType.Float32,
            channelLabels: labels.ToArray(), channelLayoutNamespace: "coreaudio");
        labels[0] = 999;
        Assert.Equal(1U, known.ChannelLabels[0]);
        Assert.Equal(known, same);
        Assert.Equal(known.GetHashCode(), same.GetHashCode());
        Assert.NotEqual(known, new RawPcmFormat(48000, 6, RawPcmSampleType.Float32,
            channelLabels: same.ChannelLabels, channelLayoutNamespace: "another-platform"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-48000)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void InvalidRatesAreRejected(double rate) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new RawPcmFormat(rate, 2, RawPcmSampleType.Float32));

    [Fact]
    public void InvalidChannelsTypesAndLayoutMetadataAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RawPcmFormat(48000, 0, RawPcmSampleType.Float32));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RawPcmFormat(48000, -1, RawPcmSampleType.Float32));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RawPcmFormat(48000, 2, (RawPcmSampleType)99));
        Assert.Throws<ArgumentException>(() => new RawPcmFormat(48000, 6, RawPcmSampleType.Float32,
            channelLabels: [1, 2], channelLayoutNamespace: "coreaudio"));
        Assert.Throws<ArgumentException>(() => new RawPcmFormat(48000, 2, RawPcmSampleType.Float32,
            channelLabels: [1, 2]));
        Assert.Throws<ArgumentException>(() => new RawPcmFormat(48000, 2, RawPcmSampleType.Float32,
            channelLayoutNamespace: " "));
    }

    [Fact]
    public void IncompleteFramesAndInvalidCountsCannotBePublished()
    {
        var format = new RawPcmFormat(48000, 8, RawPcmSampleType.Float32);
        Assert.Throws<ArgumentException>(() =>
        {
            _ = new RawPcmBlockView(new byte[31], format, 1, 0);
        });
        Assert.Throws<ArgumentException>(() =>
        {
            _ = new RawPcmBlockView(new byte[64], format, 1, 0);
        });
        Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            _ = new RawPcmBlockView([], format, 0, 0);
        });
        Assert.Throws<ArgumentOutOfRangeException>(() => format.GetByteCount(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => format.GetDurationNanoseconds(-1));
        Assert.Equal(0, format.GetByteCount(0));
        Assert.Equal(0UL, format.GetDurationNanoseconds(0));
    }

    [Fact]
    public void OversizedFramesAndDurationsCannotWrapAround()
    {
        Assert.Throws<OverflowException>(() => new RawPcmFormat(48000, int.MaxValue, RawPcmSampleType.Float32));
        var format = new RawPcmFormat(48000, 8, RawPcmSampleType.Float32);
        Assert.Throws<OverflowException>(() => format.GetByteCount(int.MaxValue));
        var extremelySlow = new RawPcmFormat(double.Epsilon, 1, RawPcmSampleType.SignedInt16);
        Assert.Throws<OverflowException>(() => extremelySlow.GetDurationNanoseconds(1));
    }

    [Fact]
    public void SourceChannelLabelsAndAdapterPolicySurviveDiagnosticJson()
    {
        const string json = """
            {
              "sourceFormat":{"sampleRate":96000,"channels":6,"bitsPerSample":32,"sampleType":"Float","interleaved":true},
              "deliveredFormat":{"sampleRate":48000,"channels":2,"bitsPerSample":16,"sampleType":"SignedInteger","interleaved":true},
              "sourceChannelLabels":[1,2,3,4,5,6],"channelLayoutNamespace":"coreaudio",
              "downmixPolicy":"layout-aware-stereo"
            }
            """;
        var snapshot = Assert.IsType<AudioCaptureDiagnostics>(AudioCaptureDiagnostics.FromNativeJson(json));
        Assert.Equal(new uint[] { 1, 2, 3, 4, 5, 6 }, snapshot.SourceChannelLabels);
        Assert.Equal("coreaudio", snapshot.ChannelLayoutNamespace);
        Assert.Equal("layout-aware-stereo", snapshot.DownmixPolicy);
        using var output = JsonDocument.Parse(JsonSerializer.Serialize(snapshot, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.Equal(6, output.RootElement.GetProperty("sourceChannelLabels").GetArrayLength());
        Assert.False(output.RootElement.TryGetProperty("samples", out _));
    }

    private sealed class CopyingSink : IRawPcmSink
    {
        public byte[] Bytes = [];
        public RawPcmFormat? Format;
        public int Frames;
        public ulong EndTime;

        public void Write(in RawPcmBlockView block)
        {
            Bytes = block.Bytes.ToArray();
            Format = block.Format;
            Frames = block.FrameCount;
            EndTime = block.EndTimeNanoseconds;
        }
    }
}
