using System.Text.Json;
using System.Text;

namespace DLNACast.Core.MiPlay;

public sealed record MiPlaySetMediaInfoPayload(
    string Artist,
    string Album,
    string Title,
    int DurationMilliseconds,
    string Id,
    string CoverUrl,
    int Status,
    int Volume,
    string Art,
    string SourceName,
    int DeviceState,
    long? PositionMilliseconds = null,
    string? Lyrics = null);

/// <summary>
/// Deterministic JSON codec for legacy Cmd_SetMediaInfo (0x0012). The field
/// names and order match the rooted-phone capture; the receiver parses this
/// as JSON, so escaped and unescaped forward slashes are equivalent.
/// </summary>
public static class MiPlaySetMediaInfoPayloadCodec
{
    public static MiPlaySetMediaInfoPayload CreateWindowsSystemAudio(
        int durationMilliseconds,
        string sourceName,
        int volume = 24) =>
        new(
            Artist: "Windows",
            Album: string.Empty,
            Title: "System Audio",
            DurationMilliseconds: durationMilliseconds,
            Id: string.Empty,
            CoverUrl: string.Empty,
            Status: 0,
            Volume: volume,
            Art: string.Empty,
            SourceName: sourceName,
            DeviceState: 2);

    public static byte[] Encode(MiPlaySetMediaInfoPayload payload)
    {
        Validate(payload);

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("mArtist", payload.Artist);
            writer.WriteString("mAlbum", payload.Album);
            writer.WriteString("mTitle", payload.Title);
            writer.WriteNumber("mDuration", payload.DurationMilliseconds);
            writer.WriteString("id", payload.Id);
            writer.WriteString("mCoverUrl", payload.CoverUrl);
            writer.WriteNumber("status", payload.Status);
            writer.WriteNumber("volume", payload.Volume);
            writer.WriteString("mArt", payload.Art);
            writer.WriteString("mSourceName", payload.SourceName);
            writer.WriteNumber("mDeviceState", payload.DeviceState);
            if (payload.PositionMilliseconds is { } position) writer.WriteNumber("mPosition", position);
            // Official sender encodes UTF-8 lyrics as standard Base64. Empty is
            // explicit clearing; null preserves the historical wire shape.
            if (payload.Lyrics is { } lyrics) writer.WriteString("mLrc", Convert.ToBase64String(Encoding.UTF8.GetBytes(lyrics)));
            writer.WriteEndObject();
        }
        return buffer.ToArray();
    }

    public static bool TryDecode(
        ReadOnlySpan<byte> payload,
        out MiPlaySetMediaInfoPayload? mediaInfo)
    {
        mediaInfo = null;
        try
        {
            using var document = JsonDocument.Parse(payload.ToArray());
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                root.EnumerateObject().Count() != 11 + (root.TryGetProperty("mPosition", out _) ? 1 : 0) + (root.TryGetProperty("mLrc", out _) ? 1 : 0) ||
                !TryReadString(root, "mArtist", out var artist) ||
                !TryReadString(root, "mAlbum", out var album) ||
                !TryReadString(root, "mTitle", out var title) ||
                !TryReadInt32(root, "mDuration", out var duration) ||
                !TryReadString(root, "id", out var id) ||
                !TryReadString(root, "mCoverUrl", out var coverUrl) ||
                !TryReadInt32(root, "status", out var status) ||
                !TryReadInt32(root, "volume", out var volume) ||
                !TryReadString(root, "mArt", out var art) ||
                !TryReadString(root, "mSourceName", out var sourceName) ||
                !TryReadInt32(root, "mDeviceState", out var deviceState))
            {
                return false;
            }

            long? parsedPosition = null;
            string? parsedLyrics = null;
            if (root.TryGetProperty("mLrc", out var lrc))
            {
                if (lrc.ValueKind != JsonValueKind.String || lrc.GetString() is not { } encoded || encoded.Length > 21848) return false;
                parsedLyrics = new UTF8Encoding(false, true).GetString(Convert.FromBase64String(encoded));
            }
            if (root.TryGetProperty("mPosition", out var position))
            {
                if (position.ValueKind != JsonValueKind.Number || !position.TryGetInt64(out var value)) return false;
                parsedPosition = value;
            }
            var decoded = new MiPlaySetMediaInfoPayload(
                artist,
                album,
                title,
                duration,
                id,
                coverUrl,
                status,
                volume,
                art,
                sourceName,
                deviceState, parsedPosition, parsedLyrics);
            Validate(decoded);
            mediaInfo = decoded;
            return true;
        }
        catch (Exception exception) when (
            exception is JsonException or InvalidOperationException or ArgumentException or FormatException)
        {
            return false;
        }
    }

    private static bool TryReadString(JsonElement root, string name, out string value)
    {
        value = string.Empty;
        if (!root.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }
        value = property.GetString() ?? string.Empty;
        return true;
    }

    private static bool TryReadInt32(JsonElement root, string name, out int value)
    {
        value = 0;
        return root.TryGetProperty(name, out var property) &&
               property.ValueKind == JsonValueKind.Number &&
               property.TryGetInt32(out value);
    }

    private static void Validate(MiPlaySetMediaInfoPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(payload.Artist);
        ArgumentNullException.ThrowIfNull(payload.Album);
        ArgumentNullException.ThrowIfNull(payload.Title);
        ArgumentNullException.ThrowIfNull(payload.Id);
        ArgumentNullException.ThrowIfNull(payload.CoverUrl);
        ArgumentNullException.ThrowIfNull(payload.Art);
        ArgumentException.ThrowIfNullOrWhiteSpace(payload.SourceName);
        if (payload.Lyrics is { } lyrics && (Encoding.UTF8.GetByteCount(lyrics) > 16 * 1024 || lyrics.Contains('\0')))
            throw new ArgumentException("Invalid lyrics.", nameof(payload));
        if (payload.PositionMilliseconds is < 0 or > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(payload.PositionMilliseconds));
        if (payload.DurationMilliseconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(payload.DurationMilliseconds));
        }
        if (payload.Volume is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(payload.Volume));
        }
        if (payload.Status is < 0 or > byte.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(payload.Status));
        }
        if (payload.DeviceState is < 0 or > byte.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(payload.DeviceState));
        }
    }
}
