using System.Buffers.Binary;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using DLNACast.Core.MiPlay;

// No audio, Open, source setters, or phone access. The optional write echoes
// only the immediately read current volume, never a guessed/saved value.
if (args.Length is < 1 or > 2) throw new ArgumentException("Expected exact advertised name [--echo-current-volume]");
var echo = args.Length == 2 && args[1] == "--echo-current-volume";
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
var token = timeout.Token;
var addresses = NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up)
    .SelectMany(n => n.GetIPProperties().UnicastAddresses).Select(a => a.Address)
    .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !System.Net.IPAddress.IsLoopback(a));
var found = await Task.WhenAll(addresses.Select(a => new MiPlayMdnsDiscovery().SearchAsync(a, TimeSpan.FromSeconds(3), token)));
var speaker = found.SelectMany(x => x).First(d => d.FriendlyName == args[0] && d.Address is not null);
using var client = new TcpClient { NoDelay = true };
await client.ConnectAsync(speaker.Address!, 8899, token);
await using var stream = client.GetStream();
async Task<MiPlayCommandFrame> Read()
{
    var h = new byte[9]; await stream.ReadExactlyAsync(h, token);
    var n = BinaryPrimitives.ReadUInt32BigEndian(h.AsSpan(5));
    if (h[0] != '$' || n > 65536) throw new IOException("Invalid frame");
    var b = new byte[n]; await stream.ReadExactlyAsync(b, token);
    return new(BinaryPrimitives.ReadUInt16BigEndian(h.AsSpan(1)), BinaryPrimitives.ReadUInt16BigEndian(h.AsSpan(3)), b);
}
async Task<MiPlayCommandFrame> Until(ushort command, ushort seq)
{
    while (true)
    {
        var f = await Read();
        if (f.Command is 0x0d or 0x0f)
            Console.WriteLine($"reply command={f.Command:X4} sequence={f.Sequence} length={f.Payload.Length} payload={Convert.ToHexString(f.Payload)}");
        if (f.Command == command && f.Sequence == seq) return f;
    }
}
var challenge = await Read();
if (challenge.Command != 0x28) throw new IOException("Unsupported authentication");
await stream.WriteAsync(MiPlayNativeVersionCodec.EncodeSourceVersion(0, MiPlayProtocolConstants.NativeSourceVersion12_4_8_13), token);
await stream.WriteAsync(MiPlayLegacySafetyChallengeCodec.EncodeAcknowledgement(
    MiPlayLegacySafetyChallengeCodec.CreateAcknowledgement(challenge.Sequence, challenge.Payload)), token);
await stream.WriteAsync(MiPlayCommandFrameCodec.Encode(0x0e, 1, []), token);
var before = await Until(0x0f, 1);
if (!MiPlayLegacyStatusScalarCodec.TryDecode(before.Payload, out var volume) || volume > 100)
    throw new IOException("Could not read current volume; no write performed");
Console.WriteLine($"current_volume={volume}");
if (echo)
{
    await stream.WriteAsync(MiPlayCommandFrameCodec.Encode(0x0c, 2, MiPlaySetVolumePayloadCodec.Encode(volume)), token);
    await Until(0x0d, 2);
    await stream.WriteAsync(MiPlayCommandFrameCodec.Encode(0x0e, 3, []), token);
    var after = await Until(0x0f, 3);
    if (!MiPlayLegacyStatusScalarCodec.TryDecode(after.Payload, out var actual) || actual != volume)
        throw new IOException("Volume readback did not match original value");
    Console.WriteLine("volume_unchanged=true");
}
