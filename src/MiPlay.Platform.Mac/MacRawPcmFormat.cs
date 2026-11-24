using System.Text.Json;
using DLNACast.Core.Audio;
using MiPlay.Desktop.Services;

namespace MiPlay.Platform.Mac;

internal static class MacRawPcmFormat
{
    internal const int MaximumFramesPerBlock = 2048;

    internal static RawPcmFormat Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var value = document.RootElement;
            var rate = value.GetProperty("sampleRate").GetDouble();
            var channels = value.GetProperty("channels").GetInt32();
            var bits = value.GetProperty("bitsPerSample").GetInt32();
            var type = (value.GetProperty("sampleType").GetString(), bits) switch
            {
                ("Float", 32) => RawPcmSampleType.Float32,
                ("SignedInteger", 16) => RawPcmSampleType.SignedInt16,
                ("SignedInteger", 24) => RawPcmSampleType.SignedInt24,
                ("SignedInteger", 32) => RawPcmSampleType.SignedInt32,
                _ => throw new ArgumentException("Unsupported native sample format.")
            };
            if (rate < 8000 || rate > 192000 || channels is < 1 or > 32 ||
                !value.GetProperty("interleaved").GetBoolean())
                throw new ArgumentException("Invalid native capture format.");
            var labels = value.TryGetProperty("channelLabels", out var array)
                ? array.EnumerateArray().Select(label => label.GetUInt32()).ToArray() : [];
            var layoutNamespace = value.TryGetProperty("channelLayoutNamespace", out var name)
                ? name.GetString() : null;
            return new(rate, channels, type, interleaved: true, labels, layoutNamespace);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or
            KeyNotFoundException or InvalidOperationException or FormatException or OverflowException)
        {
            throw new AudioCaptureException("无法识别采集到的原始音频格式。", exception);
        }
    }
}
