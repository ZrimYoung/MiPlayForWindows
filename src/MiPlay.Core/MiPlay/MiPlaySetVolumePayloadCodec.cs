using System.Buffers.Binary;

namespace DLNACast.Core.MiPlay;

/// <summary>
/// Raw SetVolume request and LX06 acknowledgement on the legacy 8899 path.
/// Official CmdSource code byte-swaps the integer before sending four bytes;
/// the LX06 receiver rejects lengths other than four.
/// </summary>
public static class MiPlaySetVolumePayloadCodec
{
    public const int PayloadLength = 4;
    public const uint MaximumVolume = 100;

    public static byte[] Encode(uint volume)
    {
        if (volume > MaximumVolume)
        {
            throw new ArgumentOutOfRangeException(nameof(volume));
        }

        var payload = new byte[PayloadLength];
        BinaryPrimitives.WriteUInt32BigEndian(payload, volume);
        return payload;
    }

    public static bool TryDecode(ReadOnlySpan<byte> payload, out uint volume)
    {
        volume = 0;
        if (payload.Length != PayloadLength)
        {
            return false;
        }

        volume = BinaryPrimitives.ReadUInt32BigEndian(payload);
        return volume <= MaximumVolume;
    }

    // OH2P replies with a tagged five-byte scalar; requests stay raw four-byte.
    public static bool TryDecodeAcknowledgement(ReadOnlySpan<byte> payload, out uint volume) =>
        TryDecode(payload, out volume) ||
        (MiPlayLegacyStatusScalarCodec.TryDecode(payload, out volume) && volume <= MaximumVolume);
}
