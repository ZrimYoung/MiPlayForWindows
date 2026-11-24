using System.Text;
using System.Text.Json;
using DLNACast.Core.Audio;
using DLNACast.Core.Models;
using MiPlay.Desktop.Services;

namespace MiPlay.Platform.Mac;

/// <summary>The existing sender's PCM contract is adapted here, after raw capture.</summary>
internal sealed class MacPcmSendAdapter : IMacPcmSendAdapter
{
    private readonly RawPcmFormat sourceFormat;
    private readonly PcmFrameAssembler assembler;
    private readonly CaptureHealthTracker health;
    private readonly byte[] output = new byte[65536];
    private readonly StringBuilder json = new(4096);
    private IntPtr converter;
    private bool pendingDiscontinuity;

    internal MacPcmSendAdapter(string formatJson, RawPcmFormat format, PcmFrameBuffer destination, CaptureHealthTracker health)
    {
        if (destination.Format.SampleRate != 48000 || destination.Format.Channels != 2 || destination.Format.BitsPerSample != 16)
            throw new ArgumentException("The current sender adapter requires 48 kHz PCM16 stereo.", nameof(destination));
        sourceFormat = format;
        assembler = new(destination);
        this.health = health;
        var result = NativeAudio.mp_pcm_converter_create(formatJson, out converter);
        if (result != 0)
        {
            if (converter != IntPtr.Zero) NativeAudio.mp_pcm_converter_destroy(converter);
            converter = IntPtr.Zero;
            throw new AudioCaptureException($"无法创建发送音频转换器（{result}）。");
        }
    }

    public unsafe void Write(in RawPcmBlockView block)
    {
        ObjectDisposedException.ThrowIf(converter == IntPtr.Zero, this);
        if (!Equals(block.Format, sourceFormat)) throw new AudioCaptureException("原始音频格式已变化，请重新启动采集。");
        if (block.Discontinuity)
        {
            if (NativeAudio.mp_pcm_converter_reset(converter) != 0)
                throw new AudioCaptureException("音频中断后的转换器重置失败。");
            assembler.Push(ReadOnlySpan<byte>.Empty, block.EndTimeNanoseconds, true);
        }
        pendingDiscontinuity |= block.Discontinuity;
        int count;
        fixed (byte* source = block.Bytes)
        fixed (byte* destination = output)
            count = NativeAudio.mp_pcm_convert(converter, source, block.Bytes.Length, destination, output.Length);
        if (count < 0 || count > output.Length || count % 4 != 0)
            throw new AudioCaptureException($"发送音频转换失败（{count}）。");
        if (count == 0) return;
        var pcm = output.AsSpan(0, count);
        health.Record(pcm);
        assembler.Push(pcm, block.EndTimeNanoseconds, pendingDiscontinuity);
        pendingDiscontinuity = false;
    }

    public AudioCaptureDiagnostics DecorateDiagnostics(AudioCaptureDiagnostics capture)
    {
        capture = capture with { DeliveredFormat = new(48000, 2, 16, "SignedInteger", true) };
        json.Clear();
        if (NativeAudio.mp_pcm_converter_info_json(converter, json, json.Capacity) <= 0) return capture;
        using var document = JsonDocument.Parse(json.ToString());
        var value = document.RootElement;
        return capture with
        {
            DeliveredFormat = new(48000, 2, 16, "SignedInteger", true),
            ConvertedBlocks = value.GetProperty("convertedBlocks").GetUInt64(),
            MaximumConversionNanoseconds = value.GetProperty("maxConversionNanoseconds").GetUInt64(),
            BufferAllocations = value.GetProperty("bufferAllocations").GetUInt32(),
            ConversionBypassed = value.GetProperty("conversionBypassed").GetBoolean(),
            DownmixPolicy = value.GetProperty("downmixPolicy").GetString()
        };
    }

    public void Dispose()
    {
        if (converter == IntPtr.Zero) return;
        NativeAudio.mp_pcm_converter_destroy(converter);
        converter = IntPtr.Zero;
    }
}
