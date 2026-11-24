using System.Text.Json;

namespace DLNACast.Core.Models;

/// <summary>The observed source format is independent of the PCM format delivered to an encoder.</summary>
public sealed record AudioCaptureFormat(double SampleRate, int Channels, int BitsPerSample,
    string SampleType, bool Interleaved)
{
    internal bool IsValid => double.IsFinite(SampleRate) && SampleRate > 0 && Channels > 0 &&
        BitsPerSample > 0 && BitsPerSample % 8 == 0 && SampleType is "Float" or "SignedInteger";
}

/// <summary>Metadata and cumulative counters only; never contains captured samples.</summary>
public sealed record AudioCaptureDiagnostics(AudioCaptureFormat SourceFormat, AudioCaptureFormat DeliveredFormat,
    ulong CapturedFrames, ulong DroppedFrames, ulong CallbackCount, uint QueuedBlocks, uint MaximumQueuedBlocks,
    ulong ConvertedBlocks, ulong MaximumCallbackNanoseconds, ulong MaximumConversionNanoseconds,
    uint BufferAllocations, bool ConversionBypassed)
{
    private static readonly JsonSerializerOptions NativeJson = new(JsonSerializerDefaults.Web);
    public DateTimeOffset ObservedAt { get; init; }
    public string? DownmixPolicy { get; init; }
    public IReadOnlyList<uint>? SourceChannelLabels { get; init; }
    public string? ChannelLayoutNamespace { get; init; }

    internal static AudioCaptureDiagnostics? FromNativeJson(string json)
    {
        try
        {
            var snapshot = JsonSerializer.Deserialize<AudioCaptureDiagnostics>(json, NativeJson);
            return snapshot is { SourceFormat.IsValid: true, DeliveredFormat.IsValid: true }
                ? snapshot with { ObservedAt = DateTimeOffset.UtcNow } : null;
        }
        catch (JsonException) { return null; }
    }
}
