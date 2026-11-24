using System.Diagnostics;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace DLNACast.Core.MiPlay;

/// <summary>Volatile now-playing information; never saved to preferences or diagnostics.</summary>
public sealed record MiPlayMediaSnapshot(string Title, string Artist, string Album, string Source,
    int DurationMilliseconds, long PositionMilliseconds, bool Playing, string ArtworkBase64 = "",
    double PlaybackRate = 1, string Lyrics = "")
{
    public const int MaximumArtworkBytes = 1024 * 1024;
    public const int MaximumArtworkBase64Length = (MaximumArtworkBytes + 2) / 3 * 4;

    public string TrackId => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        string.Join('\u001f', Source, Title, Artist, Album))));
    public void Validate()
    {
        if (Lyrics is null || Encoding.UTF8.GetByteCount(Lyrics) > 16 * 1024 || Lyrics.Contains('\0'))
            throw new ArgumentException("歌词无效或过长。");
        foreach (var text in new[] { Title, Artist, Album, Source })
            if (text is null || text.Length > 1024) throw new ArgumentException("媒体文字过长。");
        if (DurationMilliseconds < 0 || PositionMilliseconds < 0 || PositionMilliseconds > int.MaxValue ||
            !double.IsFinite(PlaybackRate) || PlaybackRate is < 0 or > 4)
            throw new ArgumentException("媒体时间无效。");
        if (ArtworkBase64 is null || ArtworkBase64.Length > MaximumArtworkBase64Length) throw new ArgumentException("封面过大。");
        if (ArtworkBase64.Length > 0)
        {
            byte[] bytes;
            try { bytes = Convert.FromBase64String(ArtworkBase64); }
            catch (FormatException) { throw new ArgumentException("封面格式无效。"); }
            if (bytes.Length > MaximumArtworkBytes) throw new ArgumentException("封面过大。");
            var jpeg = bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xd8, 0xff });
            var png = bytes.AsSpan().StartsWith(new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a });
            var webp = bytes.Length >= 20 && bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) &&
                bytes.AsSpan(8, 4).SequenceEqual("WEBP"u8) &&
                BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4, 4)) == bytes.Length - 8 &&
                (bytes.AsSpan(12, 4).SequenceEqual("VP8 "u8) || bytes.AsSpan(12, 4).SequenceEqual("VP8L"u8) ||
                 bytes.AsSpan(12, 4).SequenceEqual("VP8X"u8));
            if (!jpeg && !png && !webp) throw new ArgumentException("封面必须是 JPEG、PNG 或 WebP 图片。");
        }
    }
    public MiPlaySetMediaInfoPayload ToPayload(int volume) => new(Artist, Album, Title,
        DurationMilliseconds, TrackId, "", 0, volume, ArtworkBase64,
        string.IsNullOrWhiteSpace(Source) ? "MiPlay Desktop" : Source, 2, PositionMilliseconds, Lyrics);
}

public sealed class MiPlayMediaState
{
    private sealed record Sample(MiPlayMediaSnapshot? Value, long Timestamp);
    private Sample current = new(null, 0);
    public void Update(MiPlayMediaSnapshot? value)
    {
        value?.Validate();
        Volatile.Write(ref current, new(value, Stopwatch.GetTimestamp()));
    }
    public MiPlayMediaSnapshot? Read()
    {
        var sample = Volatile.Read(ref current);
        var elapsed = Stopwatch.GetElapsedTime(sample.Timestamp).TotalSeconds;
        if (sample.Value is not { } value || elapsed > 6) return null;
        var position = value.PositionMilliseconds + (value.Playing ? elapsed * 1000 * value.PlaybackRate : 0);
        return value with { PositionMilliseconds = (long)Math.Clamp(position, 0,
            value.DurationMilliseconds > 0 ? value.DurationMilliseconds : int.MaxValue) };
    }
}
