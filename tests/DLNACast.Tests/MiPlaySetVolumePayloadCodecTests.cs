using DLNACast.Core.MiPlay;

namespace DLNACast.Tests;

public sealed class MiPlaySetVolumePayloadCodecTests
{
    [Theory]
    [InlineData("00000039", 57)]
    [InlineData("0000000039", 57)]
    [InlineData("0000000000", 0)]
    [InlineData("0000000064", 100)]
    public void AcceptsRawAndObservedOh2pTaggedAcknowledgements(string hex, uint expected)
    {
        Assert.True(MiPlaySetVolumePayloadCodec.TryDecodeAcknowledgement(Convert.FromHexString(hex), out var actual));
        Assert.Equal(expected, actual);
        Assert.Equal(4, MiPlaySetVolumePayloadCodec.Encode(actual).Length);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0100000039")]
    [InlineData("0000000065")]
    [InlineData("000000003900")]
    public void RejectsInvalidAcknowledgementTagsLengthsAndValues(string hex) =>
        Assert.False(MiPlaySetVolumePayloadCodec.TryDecodeAcknowledgement(Convert.FromHexString(hex), out _));

    [Theory]
    [InlineData(0, "00000000")]
    [InlineData(24, "00000018")]
    [InlineData(100, "00000064")]
    public void EncodesOfficialRawBigEndianShape(uint volume, string expectedHex)
    {
        var payload = MiPlaySetVolumePayloadCodec.Encode(volume);

        Assert.Equal(expectedHex, Convert.ToHexString(payload));
        Assert.True(MiPlaySetVolumePayloadCodec.TryDecode(payload, out var decoded));
        Assert.Equal(volume, decoded);
    }

    [Fact]
    public void RejectsTaggedGetVolumeShapeAndOutOfRangeValues()
    {
        Assert.False(MiPlaySetVolumePayloadCodec.TryDecode(
            MiPlayLegacyStatusScalarCodec.Encode(24),
            out _));
        Assert.False(MiPlaySetVolumePayloadCodec.TryDecode(
            Convert.FromHexString("00000065"),
            out _));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            MiPlaySetVolumePayloadCodec.Encode(101));
    }
}
