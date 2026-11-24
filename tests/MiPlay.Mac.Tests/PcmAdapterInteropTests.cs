using System.Runtime.InteropServices;
using DLNACast.Core.Audio;
using DLNACast.Core.Models;
using MiPlay.Platform.Mac;

namespace MiPlay.Mac.Tests;

public sealed class PcmAdapterInteropTests
{
    private const string StereoJson = """
        {"sampleRate":48000,"channels":2,"bitsPerSample":16,"sampleType":"SignedInteger",
         "interleaved":true,"channelLabels":[],"channelLayoutNamespace":null}
        """;
    private static readonly Lazy<IntPtr> Library = new(() =>
    {
        var handle = NativeLibrary.Load(Environment.GetEnvironmentVariable("MIPLAY_TEST_NATIVE_AUDIO")!);
        NativeLibrary.SetDllImportResolver(typeof(MacAudioPlatform).Assembly,
            (name, _, _) => name == "MiPlayAudio" ? handle : IntPtr.Zero);
        return handle;
    });

    [NativePcmFact]
    public async Task ManagedSenderAdapterKeepsStereoBytesAndReportsItsDeliveredFormat()
    {
        _ = Library.Value;
        await using var destination = new PcmFrameBuffer(AudioChannelRoute.Stereo, PcmFrameFormat.MiPlayAac);
        var format = MacRawPcmFormat.Parse(StereoJson);
        var health = new CaptureHealthTracker();
        using var adapter = new MacPcmSendAdapter(StereoJson, format, destination, health);
        var samples = Enumerable.Range(0, 4096).Select(value => (byte)(value * 17)).ToArray();
        adapter.Write(new(samples, format, 1024, PcmPresentationTimestamp.GetElapsedRealtimeNanoseconds()));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var frame = await destination.ReadCapturedFrameAsync(timeout.Token);
        Assert.Equal(samples, frame.Bytes);
        Assert.Equal(1, health.Snapshot().PacketCount);
        var capture = new AudioCaptureDiagnostics(new(48000, 2, 16, "SignedInteger", true),
            new(48000, 2, 16, "SignedInteger", true), 1024, 0, 1, 0, 1, 0, 0, 0, 0, false);
        var diagnostics = adapter.DecorateDiagnostics(capture);
        Assert.Equal("Stereo", diagnostics.DownmixPolicy);
        Assert.True(diagnostics.ConversionBypassed);
        Assert.Equal(new AudioCaptureFormat(48000, 2, 16, "SignedInteger", true), diagnostics.DeliveredFormat);
    }

    [NativePcmFact]
    public async Task EightChannelRawPcmIsAdaptedOnlyAtTheSenderBoundary()
    {
        _ = Library.Value;
        const string sourceJson = """
            {"sampleRate":48000,"channels":8,"bitsPerSample":32,"sampleType":"Float",
             "interleaved":true,"channelLabels":[],"channelLayoutNamespace":null}
            """;
        var format = MacRawPcmFormat.Parse(sourceJson);
        var floats = Enumerable.Repeat(0.25f, 2048 * 8).ToArray();
        var raw = MemoryMarshal.AsBytes(floats.AsSpan()).ToArray();
        var original = raw.ToArray();
        await using var destination = new PcmFrameBuffer(AudioChannelRoute.Stereo, PcmFrameFormat.MiPlayAac);
        using var adapter = new MacPcmSendAdapter(sourceJson, format, destination, new());
        adapter.Write(new(raw, format, 2048, PcmPresentationTimestamp.GetElapsedRealtimeNanoseconds()));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var frame = await destination.ReadCapturedFrameAsync(timeout.Token);
        Assert.Equal(original, raw);
        Assert.Equal(4096, frame.Bytes.Length);
        Assert.Contains(frame.Bytes, value => value != 0);
        var capture = new AudioCaptureDiagnostics(new(48000, 8, 32, "Float", true),
            new(48000, 8, 32, "Float", true), 2048, 0, 1, 0, 1, 0, 0, 0, 0, false);
        var diagnostics = adapter.DecorateDiagnostics(capture);
        Assert.Equal(8, diagnostics.SourceFormat.Channels);
        Assert.Equal(new AudioCaptureFormat(48000, 2, 16, "SignedInteger", true), diagnostics.DeliveredFormat);
        Assert.Equal("DiscreteAverage", diagnostics.DownmixPolicy);
    }

    [NativePcmFact]
    public async Task DiscontinuityResetsConverterAndDiscardsOnlyOldPartialSenderFrame()
    {
        _ = Library.Value;
        await using var destination = new PcmFrameBuffer(AudioChannelRoute.Stereo, PcmFrameFormat.MiPlayAac);
        var format = MacRawPcmFormat.Parse(StereoJson);
        using var adapter = new MacPcmSendAdapter(StereoJson, format, destination, new());
        var oldPartial = Enumerable.Repeat((byte)0x11, 2048).ToArray();
        adapter.Write(new(oldPartial, format, 512, PcmPresentationTimestamp.GetElapsedRealtimeNanoseconds()));
        var current = Enumerable.Repeat((byte)0x44, 4096).ToArray();
        adapter.Write(new(current, format, 1024, PcmPresentationTimestamp.GetElapsedRealtimeNanoseconds(), discontinuity: true));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var frame = await destination.ReadCapturedFrameAsync(timeout.Token);
        Assert.Equal(current, frame.Bytes);
        Assert.True(frame.CaptureDiscontinuity);
    }

    private sealed class NativePcmFactAttribute : FactAttribute
    {
        public NativePcmFactAttribute()
        {
            if (!OperatingSystem.IsMacOS() || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MIPLAY_TEST_NATIVE_AUDIO")))
                Skip = "Set MIPLAY_TEST_NATIVE_AUDIO to a built bridge for synthetic PCM interop tests.";
        }
    }
}
