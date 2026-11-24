using System.Text.Json;
using DLNACast.Core.Abstractions;
using DLNACast.Core.Audio;
using DLNACast.Core.MiPlay;
using DLNACast.Core.Models;
using MiPlay.Desktop;
using MiPlay.Desktop.Services;

namespace MiPlay.Tests;

public sealed class CaptureDiagnosticsTests
{
    private const string NativeJson = """
        {
          "sourceFormat":{"sampleRate":96000,"channels":8,"bitsPerSample":32,"sampleType":"Float","interleaved":false},
          "deliveredFormat":{"sampleRate":48000,"channels":2,"bitsPerSample":16,"sampleType":"SignedInteger","interleaved":true},
          "capturedFrames":18446744073709551615,"droppedFrames":1024,"callbackCount":30,"queuedBlocks":2,
          "maximumQueuedBlocks":4,"convertedBlocks":28,"maximumCallbackNanoseconds":24000,
          "maximumConversionNanoseconds":180000,"bufferAllocations":2,"conversionBypassed":false
        }
        """;

    private static AudioCaptureDiagnostics Snapshot() =>
        Assert.IsType<AudioCaptureDiagnostics>(AudioCaptureDiagnostics.FromNativeJson(NativeJson));

    [Fact]
    public void NativeContractKeepsActualSourceFormatSeparateFromStereoDelivery()
    {
        var started = DateTimeOffset.UtcNow;
        var snapshot = Snapshot();
        Assert.Equal(new AudioCaptureFormat(96000, 8, 32, "Float", false), snapshot.SourceFormat);
        Assert.Equal(new AudioCaptureFormat(48000, 2, 16, "SignedInteger", true), snapshot.DeliveredFormat);
        Assert.Equal(ulong.MaxValue, snapshot.CapturedFrames);
        Assert.Equal(1024UL, snapshot.DroppedFrames);
        Assert.Equal(2U, snapshot.BufferAllocations);
        Assert.Equal(180000UL, snapshot.MaximumConversionNanoseconds);
        Assert.InRange(snapshot.ObservedAt, started, DateTimeOffset.UtcNow);
        Assert.Equal(2, PcmFrameFormat.MiPlayAac.Channels);
        Assert.Equal(16, PcmFrameFormat.MiPlayAac.BitsPerSample);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{}")]
    [InlineData("null")]
    public void MissingOrMalformedDiagnosticsCannotBePresentedAsValidAudio(string json) =>
        Assert.Null(AudioCaptureDiagnostics.FromNativeJson(json));

    [Fact]
    public void InvalidFormatDoesNotReplaceTheLastValidSnapshot()
    {
        Assert.Null(AudioCaptureDiagnostics.FromNativeJson(NativeJson.Replace("\"channels\":8", "\"channels\":0")));
        Assert.Null(AudioCaptureDiagnostics.FromNativeJson(NativeJson.Replace("\"Float\"", "\"Unknown\"")));
    }

    [Fact]
    public async Task SharedCaptureExposesTheSameSourceDiagnosticsAndKeepsFinalCountersAfterStop()
    {
        using var platform = new Platform();
        await using var shared = new SharedDesktopAudioPlatform(platform);
        await using var destination = new PcmFrameBuffer(AudioChannelRoute.Stereo, PcmFrameFormat.MiPlayAac);
        await using var capture = shared.CreateCapture(new CaptureSelection.SystemMix("default", "Default"));
        await capture.StartAsync(destination, default);
        var nativeCapture = Assert.IsType<Capture>(platform.LastCapture);
        Assert.Same(nativeCapture.Initial, capture.Health.AudioCapture);
        await capture.StopAsync();
        Assert.False(capture.IsRunning);
        Assert.Same(nativeCapture.Final, capture.Health.AudioCapture);
        Assert.Equal(2048UL, capture.Health.AudioCapture!.DroppedFrames);
    }

    [Fact]
    public async Task ReplacingCaptureUpdatesSourceFormatAndStoppingPreservesTheReplacement()
    {
        using var platform = new Platform();
        await using var destination = new PcmFrameBuffer(AudioChannelRoute.Stereo, PcmFrameFormat.MiPlayAac);
        await using var capture = new SwitchableAudioCaptureSource(platform, new CaptureSelection.SystemMix("first", "First"));
        await capture.StartAsync(destination, default);
        var first = Assert.IsType<Capture>(platform.LastCapture);
        await capture.SwitchAsync(new CaptureSelection.SystemMix("second", "Second"));
        var replacement = Assert.IsType<Capture>(platform.LastCapture);
        Assert.NotSame(first, replacement);
        Assert.Same(replacement.Initial, capture.Health.AudioCapture);
        await capture.StopAsync();
        Assert.Same(replacement.Final, capture.Health.AudioCapture);
    }

    [Fact]
    public void UiProjectionAndExportedLogRetainFormatsAndCountersWithoutRawAudio()
    {
        var snapshot = Snapshot();
        var diagnostic = new MiPlayCastDiagnostics(MiPlayCastState.Streaming, "casting",
            LastError: "private-error-sentinel", ProtocolEvidence: "raw-protocol-sentinel") { AudioCapture = snapshot };
        var card = Assert.IsType<DesktopDiagnostic>(DesktopDiagnostic.From(diagnostic));
        Assert.Same(snapshot, card.AudioCapture);
        using var ui = JsonDocument.Parse(JsonSerializer.Serialize(card, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.Equal(8, ui.RootElement.GetProperty("audioCapture").GetProperty("sourceFormat").GetProperty("channels").GetInt32());
        Assert.Equal(2, ui.RootElement.GetProperty("audioCapture").GetProperty("deliveredFormat").GetProperty("channels").GetInt32());

        var directory = Directory.CreateTempSubdirectory("miplay-capture-diagnostics-");
        try
        {
            var store = new DesktopStore(directory.FullName);
            store.Log("performance", "capture", new CastStatus(DesktopCastState.Casting, "casting", Diagnostics: diagnostic));
            var log = File.ReadAllText(Path.Combine(directory.FullName, "diagnostics.jsonl"));
            using var document = JsonDocument.Parse(log);
            var capture = document.RootElement.GetProperty("AudioCapture");
            Assert.Equal(1024UL, capture.GetProperty("DroppedFrames").GetUInt64());
            Assert.Equal(96000, capture.GetProperty("SourceFormat").GetProperty("SampleRate").GetDouble());
            Assert.Equal(48000, capture.GetProperty("DeliveredFormat").GetProperty("SampleRate").GetDouble());
            Assert.DoesNotContain("private-error-sentinel", log);
            Assert.DoesNotContain("raw-protocol-sentinel", log);
            Assert.False(capture.TryGetProperty("pcm", out _));
        }
        finally { directory.Delete(true); }
    }

    private sealed class Platform : IDesktopAudioPlatform
    {
        public Capture? LastCapture;
        public bool SupportsLocalMute => true;
        public event EventHandler? Suspending { add { } remove { } }
        public AudioSourceItem GetDefaultOutputDevice() => new("default", "Default");
        public IReadOnlyList<AudioSourceItem> GetOutputDevices() => [GetDefaultOutputDevice()];
        public IReadOnlyList<AudioSourceItem> GetCandidateProcesses() => [];
        public IAudioCaptureSource CreateCapture(CaptureSelection selection) => LastCapture = new(selection);
        public IMediaPacingClock CreateMediaClock() => new MonotonicMediaPacingClock();
        public void Dispose() { }
    }

    private sealed class Capture(CaptureSelection selection) : IAudioCaptureSource
    {
        public readonly AudioCaptureDiagnostics Initial = Snapshot();
        public readonly AudioCaptureDiagnostics Final = Snapshot() with { DroppedFrames = 2048 };
        private bool stopped;
        public CaptureSelection Selection => selection;
        public bool IsRunning { get; private set; }
        public CaptureHealth Health => new(DateTimeOffset.UtcNow, null, null, 0) { AudioCapture = stopped ? Final : Initial };
        public event EventHandler<Exception>? CaptureFailed { add { } remove { } }
        public Task StartAsync(PcmFrameBuffer destination, CancellationToken token) { IsRunning = true; return Task.CompletedTask; }
        public Task StopAsync() { IsRunning = false; stopped = true; return Task.CompletedTask; }
        public async ValueTask DisposeAsync() => await StopAsync();
    }
}
