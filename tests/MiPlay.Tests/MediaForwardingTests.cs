using System.Text.Json;
using DLNACast.Core.MiPlay;

namespace MiPlay.Tests;

public sealed class MediaForwardingTests
{
    [Fact]
    public void LyricsAreUtf8Base64AndClearWhenTrackChanges()
    {
        const string lyrics = "[00:01.00]测试歌词😀\n[00:02.00]第二行";
        var media = new MiPlayMediaSnapshot("A", "Artist", "Album", "Music", 10000, 1000, true, Lyrics: lyrics);
        media.Validate();
        var payload = media.ToPayload(20);
        var bytes = MiPlaySetMediaInfoPayloadCodec.Encode(payload);
        using var json = JsonDocument.Parse(bytes);
        Assert.Equal(lyrics, System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(json.RootElement.GetProperty("mLrc").GetString()!)));
        Assert.True(MiPlaySetMediaInfoPayloadCodec.TryDecode(bytes, out var decoded));
        Assert.Equal(payload, decoded);
        Assert.Equal(media.TrackId, (media with { Lyrics = "" }).TrackId);
        var next = new MiPlayMediaSnapshot("B", "Artist", "Album", "Music", 10000, 0, true);
        using var empty = JsonDocument.Parse(MiPlaySetMediaInfoPayloadCodec.Encode(next.ToPayload(20)));
        Assert.Equal("", empty.RootElement.GetProperty("mLrc").GetString());
        Assert.Throws<ArgumentException>(() => (media with { Lyrics = new string('字', 6000) }).Validate());
        Assert.Throws<ArgumentException>(() => (media with { Lyrics = "bad\0text" }).Validate());
    }

    [Fact]
    public void LateLyricsProgressAndTrackChangesNeverReplaceWireTitle()
    {
        var initial = new MiPlayMediaSnapshot("Song A", "Artist", "Album", "Music", 120000, 0, true);
        var state = new MiPlayMediaState();
        foreach (var update in new[] { initial,
            initial with { Lyrics = "[00:01.00]Synthetic line", PositionMilliseconds = 1000 },
            initial with { Lyrics = "[00:01.00]Synthetic line", PositionMilliseconds = 60000, Playing = false },
            initial with { Title = "Song B", Lyrics = "[00:02.00]Another line", PositionMilliseconds = 2000 } })
        {
            state.Update(update);
            var bytes = MiPlaySetMediaInfoPayloadCodec.Encode(state.Read()!.ToPayload(20));
            using var json = JsonDocument.Parse(bytes);
            Assert.Equal(update.Title, json.RootElement.GetProperty("mTitle").GetString());
            Assert.Equal(update.Artist, json.RootElement.GetProperty("mArtist").GetString());
            Assert.Equal(update.Lyrics, System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(
                json.RootElement.GetProperty("mLrc").GetString()!)));
        }
    }

    [Fact]
    public void LargerArtworkRoundTripsAndDecodedByteLimitIsEnforced()
    {
        var bytes = new byte[MiPlayMediaSnapshot.MaximumArtworkBytes];
        bytes[0] = 0xff; bytes[1] = 0xd8; bytes[2] = 0xff;
        var media = new MiPlayMediaSnapshot("Cover", "Artist", "Album", "Music", 200000, 12000, true,
            Convert.ToBase64String(bytes));
        media.Validate();
        var payload = MiPlaySetMediaInfoPayloadCodec.Encode(media.ToPayload(25));
        var wire = MiPlayCommandFrameCodec.Encode(MiPlayProtocolConstants.SetMediaInfoCommand, 7, payload);
        Assert.True(MiPlayCommandFrameCodec.TryDecode(wire, out var frame, out var consumed));
        Assert.Equal(wire.Length, consumed);
        Assert.True(MiPlaySetMediaInfoPayloadCodec.TryDecode(frame!.Payload, out var decoded));
        Assert.Equal(media.ArtworkBase64, decoded!.Art);
        Assert.True(payload.Length < MiPlayCommandFrameCodec.MaximumPayloadLength);
        // These lengths have identical padded Base64 lengths: checking text alone is insufficient.
        var over = bytes.Concat(new byte[] { 0 }).ToArray();
        Assert.Equal(media.ArtworkBase64.Length, Convert.ToBase64String(over).Length);
        Assert.Throws<ArgumentException>(() => (media with { ArtworkBase64 = Convert.ToBase64String(over) }).Validate());
    }

    [Fact]
    public void ArtworkFormatsRemainExplicitAndBounded()
    {
        var state = new MiPlayMediaState();
        var media = new MiPlayMediaSnapshot("Test", "Artist", "Album", "Music", 1000, 0, false);
        byte[] png = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];
        state.Update(media with { ArtworkBase64 = Convert.ToBase64String(png) });
        var webp = new byte[24];
        "RIFF"u8.CopyTo(webp); "WEBPVP8 "u8.CopyTo(webp.AsSpan(8));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(webp.AsSpan(4), 16);
        state.Update(media with { ArtworkBase64 = Convert.ToBase64String(webp) });
        Assert.Equal(Convert.ToBase64String(webp), state.Read()!.ArtworkBase64);
        webp[4] = 0;
        Assert.Throws<ArgumentException>(() => state.Update(media with { ArtworkBase64 = Convert.ToBase64String(webp) }));
        Assert.Throws<ArgumentException>(() => state.Update(media with { ArtworkBase64 = Convert.ToBase64String("GIF89a"u8) }));
        state.Update(media with { ArtworkBase64 = "" });
        Assert.Empty(state.Read()!.ArtworkBase64);
    }

    [Theory]
    [InlineData("not-base64!")]
    [InlineData("/w==")]
    public void InvalidLyricEncodingIsRejected(string encoded)
    {
        var payload = MiPlaySetMediaInfoPayloadCodec.CreateWindowsSystemAudio(0, "Test") with { Lyrics = "" };
        var json = System.Text.Encoding.UTF8.GetString(MiPlaySetMediaInfoPayloadCodec.Encode(payload));
        json = json.Replace("\"mLrc\":\"\"", "\"mLrc\":\"" + encoded + "\"");
        Assert.False(MiPlaySetMediaInfoPayloadCodec.TryDecode(System.Text.Encoding.UTF8.GetBytes(json), out _));
    }
    [Fact]
    public void MetadataKeepsUnicodeArtworkAndPositionAndDoesNotIncludePositionInTrackIdentity()
    {
        var media = new MiPlayMediaSnapshot("歌曲😀", "歌手", "专辑", "QQ 音乐", 200000, 42000, false, "/9j/AA==");
        media.Validate();
        var payload = media.ToPayload(35);
        var bytes = MiPlaySetMediaInfoPayloadCodec.Encode(payload);
        Assert.True(MiPlaySetMediaInfoPayloadCodec.TryDecode(bytes, out var decoded));
        Assert.Equal(payload, decoded);
        using var json = JsonDocument.Parse(bytes);
        Assert.Equal(42000, json.RootElement.GetProperty("mPosition").GetInt64());
        Assert.Equal(media.TrackId, (media with { PositionMilliseconds = 70000, ArtworkBase64 = "" }).TrackId);
        Assert.NotEqual(media.TrackId, (media with { Title = "下一首" }).TrackId);
    }

    [Fact]
    public void InvalidMetadataCannotReplaceLastValidStateAndClearDropsArtwork()
    {
        var state = new MiPlayMediaState();
        var media = new MiPlayMediaSnapshot("Track", "Artist", "Album", "Player", 100000, 32000, false, "/9j/AA==");
        state.Update(media);
        Assert.Throws<ArgumentException>(() => state.Update(media with { ArtworkBase64 = "file:///private/file" }));
        Assert.Throws<ArgumentException>(() => state.Update(media with { PositionMilliseconds = -1 }));
        Assert.Throws<ArgumentException>(() => state.Update(media with { ArtworkBase64 = new string('A', MiPlayMediaSnapshot.MaximumArtworkBase64Length + 1) }));
        Assert.Equal(media, state.Read());
        state.Update(null);
        Assert.Null(state.Read());
    }

    [Theory]
    [InlineData(256000)]
    [InlineData(320000)]
    [InlineData(512000)]
    public void QualityValuesReachEncoderAndKeepAacLc48Khz(int bitrate)
    {
        MiPlayAudioQuality.Validate(bitrate);
        var args = MiPlayFfmpegAacEncoder.CreateArgumentList(bitrate);
        Assert.Contains(bitrate.ToString(), args);
        Assert.Contains("aac_low", args); Assert.Contains("48000", args);
        Assert.Throws<ArgumentOutOfRangeException>(() => MiPlayAudioQuality.Validate(511999));
        Assert.Throws<ArgumentOutOfRangeException>(() => MiPlayFfmpegAacEncoder.CreateArgumentList(512001));
    }
}
