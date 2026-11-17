using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using DLNACast.Core.MiPlay;

namespace MiPlay.Desktop.Services;

public sealed record SpeakerIdentity(string Name, string Model, string GroupId, string GroupName,
    string Channel, bool IsMaster, bool HasGroupInfo = true);

/// <summary>Only version/authentication and GetDeviceInfo; never opens playback or changes receiver settings.</summary>
public sealed class SpeakerIdentityReader
{
    public async Task<SpeakerIdentity?> ReadAsync(IPAddress address, CancellationToken token, int port = 8899)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        var ct = timeout.Token;
        using var client = new TcpClient(address.AddressFamily) { NoDelay = true };
        await client.ConnectAsync(address, port, ct).ConfigureAwait(false);
        await using var stream = client.GetStream();
        var challenge = await ReadFrameAsync(stream, ct).ConfigureAwait(false);
        if (challenge.Command != MiPlayProtocolConstants.LegacySafetyChallengeCommand ||
            challenge.Payload.Length is < 12 or > 17 ||
            challenge.Payload.Any(value => value is < (byte)'0' or > (byte)'9')) return null;
        await stream.WriteAsync(MiPlayNativeVersionCodec.EncodeSourceVersion(0,
            MiPlayProtocolConstants.NativeSourceVersion12_4_8_13), ct).ConfigureAwait(false);
        await stream.WriteAsync(MiPlayLegacySafetyChallengeCodec.EncodeAcknowledgement(
            MiPlayLegacySafetyChallengeCodec.CreateAcknowledgement(challenge.Sequence, challenge.Payload)), ct).ConfigureAwait(false);
        await stream.WriteAsync(MiPlayCommandFrameCodec.Encode(
            MiPlayProtocolConstants.GetDeviceInfoCommand, 1, []), ct).ConfigureAwait(false);
        for (var i = 0; i < 8; i++)
        {
            var frame = await ReadFrameAsync(stream, ct).ConfigureAwait(false);
            if (frame.Command == MiPlayProtocolConstants.GetDeviceInfoAcknowledgementCommand && frame.Sequence == 1 &&
                MiPlayLegacyDeviceInfoPayloadCodec.TryDecode(frame.Payload, out var info, out var consumed) &&
                info is not null && consumed == frame.Payload.Length)
                return new(info.GetValue("miName") ?? "", info.GetValue("model") ?? "",
                    info.GetValue("groupId") ?? "", info.GetValue("groupName") ?? "",
                    info.GetValue("channel") ?? "", info.GetValue("isMaster") == "1",
                    info.GetValue("groupId") is not null || info.GetValue("channel") == "center");
        }
        return null;
    }

    private static async Task<MiPlayCommandFrame> ReadFrameAsync(Stream stream, CancellationToken token)
    {
        var header = new byte[9];
        await stream.ReadExactlyAsync(header, token).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(5));
        if (header[0] != (byte)'$' || length > 64 * 1024) throw new IOException("Invalid speaker identity response.");
        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, token).ConfigureAwait(false);
        return new(BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(1)),
            BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(3)), payload);
    }
}
