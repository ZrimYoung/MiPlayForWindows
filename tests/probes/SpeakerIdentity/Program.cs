using MiPlay.Desktop.Services;
using System.Text.Json;
using System.Buffers.Binary;
using System.Net.Sockets;
using System.Net;
using System.Text;
using DLNACast.Core.MiPlay;

if (args.Length == 2 && args[0] is "--trace" or "--trace-safety" or "--trace-auth" or "--trace-identity")
{
    // Diagnostic only. Modern modes never send identity or playback commands.
    var modern = args[0] != "--trace";
    var authenticate = args[0] is "--trace-auth" or "--trace-identity";
    var readIdentity = args[0] == "--trace-identity";
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
    using var client = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
    await client.ConnectAsync(args[1], 8899, deadline.Token);
    await using var stream = client.GetStream();
    async Task<MiPlayCommandFrame> Read()
    {
        var header = new byte[9]; await stream.ReadExactlyAsync(header, deadline.Token);
        if (header[0] != (byte)'$') throw new IOException("Invalid frame marker");
        var n = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(5));
        if (n > 65536) throw new IOException("Frame too large");
        var body = new byte[n]; await stream.ReadExactlyAsync(body, deadline.Token);
        var f = new MiPlayCommandFrame(BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(1)), BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(3)), body);
        Console.WriteLine($"command=0x{f.Command:x4}, seq={f.Sequence}, bytes={body.Length}");
        return f;
    }
    try
    {
        var challenge = await Read();
        if (challenge.Command != 0x28 || !challenge.Payload.All(b => b >= '0' && b <= '9')) return;
        var version = MiPlayNativeVersionCodec.EncodeSourceVersion(0, modern
            ? MiPlayProtocolConstants.NativeSourceVersion18_0_0_3
            : MiPlayProtocolConstants.NativeSourceVersion12_4_8_13);
        var ack = MiPlayLegacySafetyChallengeCodec.EncodeAcknowledgement(MiPlayLegacySafetyChallengeCodec.CreateAcknowledgement(challenge.Sequence, challenge.Payload));
        await stream.WriteAsync(version.Concat(ack).ToArray(), deadline.Token);
        if (modern)
            await stream.WriteAsync(MiPlaySafetyCommandCodec.Encode(MiPlayProtocolConstants.SafetyInfoCommand,
                1, MiPlaySafetyInfoOffer.Native18_0_0_3.ToJsonPayload()), deadline.Token);
        bool queried = false;
        MiPlaySafetyAuthChallenge? localChallenge = null;
        string? authKey = null;
        MiPlaySafetyDataSessionCipher? authCipher = null;
        bool peerAnswered = false, localVerified = false;
        for (int i = 0; i < 12; i++)
        {
            var f = await Read();
            if (modern)
            {
                if (peerAnswered && localVerified && readIdentity)
                {
                    if (authCipher is null || !authCipher.TryDecryptVersion1(f.Payload, out var decoded) || decoded is null)
                    { Console.WriteLine("Post-authentication payload did not decode; stopped."); break; }
                    if (f.Command == MiPlayProtocolConstants.GetDeviceInfoAcknowledgementCommand && f.Sequence == 3)
                    {
                        var parsed = MiPlayLegacyDeviceInfoPayloadCodec.TryDecode(decoded.Plaintext, out var info, out var used);
                        Console.WriteLine(JsonSerializer.Serialize(new { identityDecoded = parsed && used == decoded.Plaintext.Length,
                            name = info?.GetValue("miName"), model = info?.GetValue("model"), type = info?.GetValue("deviceType") }));
                        break;
                    }
                    continue;
                }
                if (f.Command == MiPlayProtocolConstants.SafetyInfoAcknowledgementCommand)
                {
                    if (MiPlaySafetyEnvelopeCodec.TryDecode(f.Payload, out var envelope, out var used) &&
                        envelope is not null && used == f.Payload.Length &&
                        MiPlaySafetyInfoCodec.TryDecodeAcknowledgement(envelope.Payload, out var selection) && selection is not null)
                    {
                        Console.WriteLine(JsonSerializer.Serialize(selection));
                        if (authenticate && selection.Result == "0" && selection.Selection.AuthKeyType == 1 &&
                            selection.Selection.AuthAlgorithmType == 4 && selection.Selection.IntegrityType == 1 &&
                            selection.Selection.AesKeyType == 1 && selection.Selection.AesIvType == 2)
                        {
                            var local = (IPEndPoint)client.Client.LocalEndPoint!;
                            var peer = (IPEndPoint)client.Client.RemoteEndPoint!;
                            authKey = new MiPlayTcpSessionInfo(local.Address, (ushort)local.Port, peer.Address, (ushort)peer.Port).DeriveType1SafetyKey();
                            authCipher = new MiPlaySafetyDataSessionCipher(
                                Encoding.ASCII.GetBytes(MiPlaySafetyKeyDerivation.SelectDerivedAesMaterial(authKey, 1)),
                                // Phone binary 0x2bbf24/0x2bbf54 copies the key
                                // string into both AES key and IV buffers, despite
                                // the selected IV type. Do not generalize this to
                                // other receiver profiles or production sessions.
                                Encoding.ASCII.GetBytes(MiPlaySafetyKeyDerivation.SelectDerivedAesMaterial(authKey, 1)));
                            localChallenge = MiPlaySafetyAuthCodec.CreateChallenge(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000);
                            var plaintext = MiPlaySafetyEnvelopeCodec.Encode(false, MiPlayProtocolConstants.SafetyValueType, localChallenge.ToJsonPayload());
                            await stream.WriteAsync(MiPlayCommandFrameCodec.Encode(MiPlayProtocolConstants.SafetyAuthCommand,
                                2, authCipher.EncryptVersion1(plaintext)), deadline.Token);
                            continue;
                        }
                    }
                    break;
                }
                if (f.Command is MiPlayProtocolConstants.SafetyAuthCommand or MiPlayProtocolConstants.SafetyAuthAcknowledgementCommand)
                {
                    if (!authenticate || authKey is null || localChallenge is null || authCipher is null ||
                        !authCipher.TryDecryptVersion1(f.Payload, out var decrypted) || decrypted is null ||
                        !MiPlaySafetyEnvelopeCodec.TryDecode(decrypted.Plaintext, out var envelope, out var used) ||
                        envelope is null || used != decrypted.Plaintext.Length)
                    { Console.WriteLine("Unsupported authentication envelope; stopped."); break; }
                    if (f.Command == MiPlayProtocolConstants.SafetyAuthCommand && !envelope.IsAcknowledgement && !peerAnswered &&
                        MiPlaySafetyAuthCodec.TryDecodeChallenge(envelope.Payload, out var challenge2) && challenge2 is not null)
                    {
                        var proof = MiPlaySafetyAuthCodec.CreateAcknowledgement(challenge2.AuthMessage, authKey, MiPlaySafetyHashAlgorithm.Sha256);
                        var plaintext = MiPlaySafetyEnvelopeCodec.Encode(true, MiPlayProtocolConstants.SafetyValueType, proof.ToJsonPayload());
                        await stream.WriteAsync(MiPlayCommandFrameCodec.Encode(MiPlayProtocolConstants.SafetyAuthAcknowledgementCommand,
                            f.Sequence, authCipher.EncryptVersion1(plaintext)), deadline.Token);
                        peerAnswered = true;
                    }
                    else if (f.Command == MiPlayProtocolConstants.SafetyAuthAcknowledgementCommand && envelope.IsAcknowledgement && f.Sequence == 2 && !localVerified &&
                        MiPlaySafetyAuthCodec.TryDecodeAcknowledgement(envelope.Payload, out var proof) && proof is not null)
                    {
                        localVerified = MiPlaySafetyAuthCodec.VerifyAcknowledgement(localChallenge.AuthMessage, authKey, MiPlaySafetyHashAlgorithm.Sha256, proof);
                        if (!localVerified) { Console.WriteLine("Peer proof did not verify; stopped."); break; }
                    }
                    else { Console.WriteLine("Unexpected authentication sequence; stopped."); break; }
                    if (peerAnswered && localVerified)
                    {
                        if (!readIdentity)
                        { Console.WriteLine("Local proof sent; receiver proof verified. No post-authentication command sent."); break; }
                        await stream.WriteAsync(MiPlayPostAuthGetDeviceInfoProbe.ToSafetyDataCommandFrame(3, authCipher), deadline.Token);
                        Console.WriteLine("Receiver proof verified; sent one read-only GetDeviceInfo.");
                    }
                }
                continue;
            }
            if (!queried && f.Command == MiPlayProtocolConstants.NativeSourceVersionAcknowledgementCommand)
            {
                queried = true;
                await stream.WriteAsync(MiPlayCommandFrameCodec.Encode(MiPlayProtocolConstants.GetDeviceInfoCommand, 1, []), deadline.Token);
            }
            if (f.Command == MiPlayProtocolConstants.GetDeviceInfoAcknowledgementCommand)
            {
                bool decoded = MiPlayLegacyDeviceInfoPayloadCodec.TryDecode(f.Payload, out var info, out var used);
                Console.WriteLine(JsonSerializer.Serialize(new { decoded, used, name = info?.GetValue("miName"), model = info?.GetValue("model"), type = info?.GetValue("deviceType"), needLrc = info?.GetValue("needLrc") }));
                break;
            }
        }
    }
    catch (Exception e) { Console.WriteLine(e.GetType().Name + ": " + e.Message); }
    return;
}


using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
var speakers = await new SpeakerDiscovery().SearchAsync(timeout.Token);
foreach (var speaker in speakers)
{
    try
    {
        var identity = speaker.Identity;
        Console.WriteLine(JsonSerializer.Serialize(new { speaker.Id, speaker.Name, Address = speaker.Renderer.Address.ToString(), Identity = identity }));
    }
    catch (Exception ex) { Console.WriteLine(JsonSerializer.Serialize(new { speaker.Name, Error = ex.Message })); }
}
