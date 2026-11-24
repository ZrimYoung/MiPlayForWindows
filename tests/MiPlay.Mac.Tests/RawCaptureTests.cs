using System.Text.Json;
using DLNACast.Core.Abstractions;
using DLNACast.Core.Audio;
using DLNACast.Core.Models;
using MiPlay.Desktop.Services;
using MiPlay.Platform.Mac;

namespace MiPlay.Mac.Tests;

public sealed class RawCaptureTests
{
    private static string FormatJson(int channels = 8, int bits = 32, string type = "Float",
        bool interleaved = true, uint[]? labels = null, double rate = 96000) =>
        JsonSerializer.Serialize(new
        {
            sampleRate = rate, channels, bitsPerSample = bits, sampleType = type, interleaved,
            channelLabels = labels ?? [], channelLayoutNamespace = labels is { Length: > 0 } ? "coreaudio" : null
        });

    [Theory]
    [InlineData("SignedInteger", 16, RawPcmSampleType.SignedInt16)]
    [InlineData("SignedInteger", 24, RawPcmSampleType.SignedInt24)]
    [InlineData("SignedInteger", 32, RawPcmSampleType.SignedInt32)]
    [InlineData("Float", 32, RawPcmSampleType.Float32)]
    public void NativeFormatsPreservePrecisionAndOpaqueChannelLabels(string type, int bits, RawPcmSampleType expected)
    {
        uint[] labels = [1, 2, 3, 4, 5, 6, 10, 11];
        var format = MacRawPcmFormat.Parse(FormatJson(bits: bits, type: type, labels: labels));
        Assert.Equal(expected, format.SampleType);
        Assert.Equal(96000, format.SampleRate);
        Assert.Equal(8, format.Channels);
        Assert.Equal(labels, format.ChannelLabels);
        Assert.Equal("coreaudio", format.ChannelLayoutNamespace);
        Assert.Equal(8 * bits / 8, format.BytesPerFrame);
    }

    [Fact]
    public void UnknownSixChannelLayoutRemainsUnknown()
    {
        var format = MacRawPcmFormat.Parse(FormatJson(channels: 6));
        Assert.Empty(format.ChannelLabels);
        Assert.Null(format.ChannelLayoutNamespace);
        Assert.Equal(6, format.Channels);
    }

    [Theory]
    [InlineData(0, 32, "Float", true, 48000)]
    [InlineData(33, 32, "Float", true, 48000)]
    [InlineData(2, 16, "Float", true, 48000)]
    [InlineData(2, 32, "Unknown", true, 48000)]
    [InlineData(2, 32, "Float", false, 48000)]
    [InlineData(2, 32, "Float", true, 7999)]
    [InlineData(2, 32, "Float", true, 192001)]
    public void UnsupportedNativeContractsFailBeforeReadingSamples(int channels, int bits, string type, bool interleaved, double rate) =>
        Assert.Throws<AudioCaptureException>(() => MacRawPcmFormat.Parse(FormatJson(channels, bits, type, interleaved, rate: rate)));

    [Fact]
    public async Task RawSinkReceivesAllSamplesOnDedicatedWorkerWithDiscontinuity()
    {
        var backend = new FakeBackend(FormatJson(labels: [1, 2, 3, 4, 5, 6, 10, 11]),
            Enumerable.Range(0, 256).Select(value => (byte)value).ToArray()) { Discontinuity = 1 };
        var sink = new CollectingSink();
        await using var capture = new MacCaptureSource(new CaptureSelection.SystemMix("fake", "Synthetic"), backend);
        await capture.StartRawAsync(sink, CancellationToken.None);
        await sink.Received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await capture.StopAsync().WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(backend.Samples, sink.Samples);
        Assert.Equal(8, sink.Format!.Channels);
        Assert.Equal(RawPcmSampleType.Float32, sink.Format.SampleType);
        Assert.Equal(8, sink.FrameCount);
        Assert.True(sink.Discontinuity);
        Assert.False(backend.ReadOnThreadPool);
        Assert.Equal(1, backend.StopCount);
        Assert.Equal(8, capture.Health.AudioCapture!.DeliveredFormat.Channels);
        Assert.Equal(sink.Format.ChannelLabels, capture.Health.AudioCapture.SourceChannelLabels);
    }

    [Fact]
    public async Task EmptyCaptureUsesBoundedWaitAndStopsBeforeReleasingNativeState()
    {
        var backend = new FakeBackend(FormatJson(), []);
        await using var capture = new MacCaptureSource(new CaptureSelection.SystemMix("fake", "Synthetic"), backend);
        await capture.StartRawAsync(new CollectingSink(), CancellationToken.None);
        await backend.WaitEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await capture.StopAsync().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.InRange(backend.MaximumWait, 1, 20);
        Assert.Equal(1, backend.StopCount);
        Assert.False(backend.StoppedWhileWaiting);
        Assert.False(capture.IsRunning);
    }

    [Fact]
    public async Task RejectedSecondStartDoesNotStopTheExistingCapture()
    {
        var backend = new FakeBackend(FormatJson(), []);
        await using var capture = new MacCaptureSource(new CaptureSelection.SystemMix("fake", "Synthetic"), backend);
        await capture.StartRawAsync(new CollectingSink(), CancellationToken.None);
        await backend.WaitEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<InvalidOperationException>(() => capture.StartRawAsync(new CollectingSink(), CancellationToken.None));
        Assert.Equal(0, backend.StopCount);
        Assert.True(capture.IsRunning);
        await capture.StopAsync();
        Assert.Equal(1, backend.StopCount);
    }

    [Fact]
    public async Task LegacySenderAdaptsAfterRawCaptureAndDisposesBeforeNativeStop()
    {
        var backend = new FakeBackend(FormatJson(), new byte[256]);
        var adapter = new FakeAdapter();
        backend.BeforeStop = () => Assert.True(adapter.Disposed);
        await using var destination = new PcmFrameBuffer();
        await using var capture = new MacCaptureSource(new CaptureSelection.SystemMix("fake", "Synthetic"), backend,
            (_, _, _, _) => adapter);
        await capture.StartAsync(destination, CancellationToken.None);
        await adapter.Received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await capture.StopAsync();
        Assert.Equal(8, adapter.LastFormat!.Channels);
        Assert.Equal(backend.Samples, adapter.RawSamples);
        Assert.True(adapter.Disposed);
        Assert.Equal(new AudioCaptureFormat(48000, 2, 16, "SignedInteger", true), capture.Health.AudioCapture!.DeliveredFormat);
        Assert.Equal("SyntheticAdapter", capture.Health.AudioCapture.DownmixPolicy);
    }

    [Fact]
    public async Task PartialNativeFrameReportsFailureAndStillAllowsCompleteStop()
    {
        var backend = new FakeBackend(FormatJson(), new byte[31]);
        var failure = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sink = new CollectingSink();
        await using var capture = new MacCaptureSource(new CaptureSelection.SystemMix("fake", "Synthetic"), backend);
        capture.CaptureFailed += (_, exception) => failure.TrySetResult(exception);
        await capture.StartRawAsync(sink, CancellationToken.None);
        Assert.IsType<AudioCaptureException>(await failure.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        await capture.StopAsync();
        Assert.False(sink.Received.Task.IsCompleted);
        Assert.Equal(1, backend.StopCount);
    }

    [Fact]
    public async Task UnsupportedFormatReleasesCaptureWithoutStartingItsWorker()
    {
        var backend = new FakeBackend(FormatJson(type: "Unknown"), []);
        await using var capture = new MacCaptureSource(new CaptureSelection.SystemMix("fake", "Synthetic"), backend);
        await Assert.ThrowsAsync<AudioCaptureException>(() => capture.StartRawAsync(new CollectingSink(), CancellationToken.None));
        Assert.Equal(1, backend.StopCount);
        Assert.Equal(0, backend.ReadCount);
    }

    [Fact]
    public async Task FloatingNegativeZeroIsRecordedAsSilentInput()
    {
        var samples = new byte[32];
        for (var i = 3; i < samples.Length; i += 4) samples[i] = 128;
        var backend = new FakeBackend(FormatJson(), samples);
        var sink = new CollectingSink();
        await using var capture = new MacCaptureSource(new CaptureSelection.SystemMix("fake", "Synthetic"), backend);
        await capture.StartRawAsync(sink, CancellationToken.None);
        await sink.Received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await capture.StopAsync();
        Assert.Equal(1, capture.Health.PacketCount);
        Assert.Null(capture.Health.LastAudiblePacketAt);
    }

    private sealed class CollectingSink : IRawPcmSink
    {
        public readonly TaskCompletionSource Received = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public byte[]? Samples;
        public RawPcmFormat? Format;
        public int FrameCount;
        public bool Discontinuity;
        public void Write(in RawPcmBlockView block)
        {
            Samples = block.Bytes.ToArray(); // This consumer intentionally owns a retained copy.
            Format = block.Format; FrameCount = block.FrameCount; Discontinuity = block.Discontinuity;
            Received.TrySetResult();
        }
    }

    private sealed class FakeAdapter : IMacPcmSendAdapter
    {
        public readonly TaskCompletionSource Received = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed;
        public RawPcmFormat? LastFormat;
        public byte[]? RawSamples;
        public void Write(in RawPcmBlockView block)
        {
            LastFormat = block.Format; RawSamples = block.Bytes.ToArray(); Received.TrySetResult();
        }
        public AudioCaptureDiagnostics DecorateDiagnostics(AudioCaptureDiagnostics capture) => capture with
        {
            DeliveredFormat = new(48000, 2, 16, "SignedInteger", true), DownmixPolicy = "SyntheticAdapter"
        };
        public void Dispose() => Disposed = true;
    }

    private sealed class FakeBackend(string formatJson, byte[] samples) : IMacRawCaptureBackend
    {
        private int reads, waiting;
        public readonly TaskCompletionSource WaitEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public byte[] Samples => samples;
        public int Discontinuity;
        public int StopCount, MaximumWait;
        public int ReadCount => Volatile.Read(ref reads);
        public bool ReadOnThreadPool, StoppedWhileWaiting;
        public Action? BeforeStop;
        public int Start(CaptureSelection selection, out IntPtr handle) { handle = new(1); return 0; }
        public string ReadFormat(IntPtr handle) => formatJson;
        public int Read(IntPtr handle, byte[] destination, out ulong endTimeNanoseconds, out int discontinuity)
        {
            ReadOnThreadPool = Thread.CurrentThread.IsThreadPoolThread;
            endTimeNanoseconds = PcmPresentationTimestamp.GetElapsedRealtimeNanoseconds();
            discontinuity = Discontinuity;
            if (Interlocked.Increment(ref reads) != 1) return 0;
            samples.CopyTo(destination, 0);
            return samples.Length;
        }
        public int Wait(IntPtr handle, int timeoutMilliseconds)
        {
            MaximumWait = Math.Max(MaximumWait, timeoutMilliseconds);
            Interlocked.Exchange(ref waiting, 1);
            WaitEntered.TrySetResult();
            try { Thread.Sleep(timeoutMilliseconds); return 0; }
            finally { Interlocked.Exchange(ref waiting, 0); }
        }
        public AudioCaptureDiagnostics ReadDiagnostics(IntPtr handle) => new(
            new(96000, 8, 32, "Float", true), new(96000, 8, 32, "Float", true),
            8, 0, 1, 0, 1, 0, 0, 0, 0, false);
        public ulong TimeNanoseconds() => PcmPresentationTimestamp.GetElapsedRealtimeNanoseconds();
        public void Stop(IntPtr handle)
        {
            BeforeStop?.Invoke();
            StoppedWhileWaiting = Volatile.Read(ref waiting) != 0;
            Interlocked.Increment(ref StopCount);
        }
    }
}
