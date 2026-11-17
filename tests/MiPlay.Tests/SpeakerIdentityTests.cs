using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using DLNACast.Core.MiPlay;
using DLNACast.Core.Models;
using MiPlay.Desktop.Services;

namespace MiPlay.Tests;

public sealed class SpeakerIdentityTests
{
    [Fact]
    public async Task RuntimeIdentityWinsOverAnOlderInFlightDiscoveryReply()
    {
        var reply = new TaskCompletionSource<SpeakerIdentity?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var discovery = new SpeakerDiscovery { ReadIdentity = (_, _) => reply.Task };
        var uri = new Uri("http://192.0.2.2:8899/");
        var endpoint = new UpnpServiceEndpoint("", uri, uri, uri);
        var speaker = new Speaker("stable", "Art", "", new RendererDevice("stable", "Art", "Xiaomi", "",
            IPAddress.Parse("192.0.2.2"), uri, endpoint, endpoint, endpoint, ""));
        var pending = discovery.EnrichAsync(speaker, default);
        var dissolved = new SpeakerIdentity("Art", "L09A", "", "", "center", false);
        discovery.ObserveIdentity("stable", dissolved);
        reply.SetResult(new("Art", "L09A", "old-pair", "Old stereo", "left", true));
        Assert.Equal(dissolved, (await pending).Identity);
    }

    [Fact]
    public async Task QueryReadsGroupAndChannelWithoutSendingPlaybackOrSettingsCommands()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(timeout.Token);
            var stream = client.GetStream();
            await stream.WriteAsync(MiPlayCommandFrameCodec.Encode(0x28, 17, "1234567890123"u8), timeout.Token);
            var commands = new List<ushort>();
            for (var i = 0; i < 3; i++)
            {
                var header = new byte[9];
                await stream.ReadExactlyAsync(header, timeout.Token);
                commands.Add(BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(1)));
                var body = new byte[BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(5))];
                await stream.ReadExactlyAsync(body, timeout.Token);
            }
            Assert.Equal(new ushort[] { 0x36, 0x29, 0x1e }, commands);
            await stream.WriteAsync(MiPlayCommandFrameCodec.Encode(0x37, 0, "1.0\0"u8), timeout.Token);
            var payload = MiPlayLegacyDeviceInfoPayloadCodec.Encode(new Dictionary<string, string> {
                ["miName"] = "Art L", ["model"] = "L09A", ["groupId"] = "test-pair", ["groupName"] = "客厅立体声",
                ["channel"] = "left", ["isMaster"] = "1", ["accountId"] = "must-not-be-retained"
            });
            await stream.WriteAsync(MiPlayCommandFrameCodec.Encode(0x1f, 1, payload), timeout.Token);
            // The query closes immediately; it must not send identity setters,
            // Open, playback, volume, or a stop command on cleanup.
            Assert.Equal(0, await stream.ReadAsync(new byte[1], timeout.Token));
        });
        var identity = await new SpeakerIdentityReader().ReadAsync(IPAddress.Loopback, timeout.Token,
            ((IPEndPoint)listener.LocalEndpoint).Port);
        Assert.Equal(new SpeakerIdentity("Art L", "L09A", "test-pair", "客厅立体声", "left", true), identity);
        await server;
    }

    [Fact]
    public async Task DiscoveryCachesMetadataSkipsPlayingSpeakersAndRefreshesDissolvedGroups()
    {
        var time = DateTimeOffset.UtcNow;
        var queries = 0;
        var idle = true;
        var identity = new SpeakerIdentity("Art R", "L09A", "pair", "客厅立体声", "right", false);
        var discovery = new SpeakerDiscovery {
            Now = () => time, CanReadIdentity = _ => idle,
            ReadIdentity = (_, _) => { queries++; return Task.FromResult<SpeakerIdentity?>(identity); }
        };
        var uri = new Uri("http://192.0.2.2:8899/");
        var endpoint = new UpnpServiceEndpoint("", uri, uri, uri);
        var speaker = new Speaker("stable-id", "小爱音箱-0000", "", new RendererDevice("stable-id", "小爱音箱-0000",
            "Xiaomi", "", IPAddress.Parse("192.0.2.2"), uri, endpoint, endpoint, endpoint, ""));
        var first = await discovery.EnrichAsync(speaker, default);
        Assert.Equal("Art R", first.Name);
        Assert.Equal("pair", first.Identity!.GroupId);
        await discovery.EnrichAsync(speaker, default);
        Assert.Equal(1, queries);
        time = time.AddMinutes(2); idle = false;
        await discovery.EnrichAsync(speaker, default);
        Assert.Equal(1, queries);
        idle = true;
        identity = identity with { GroupId = "", GroupName = "", Channel = "center" };
        var ungrouped = await discovery.EnrichAsync(speaker, default);
        Assert.Equal(2, queries);
        Assert.Empty(ungrouped.Identity!.GroupId);
        discovery.InvalidateIdentity(speaker.Id);
        await discovery.EnrichAsync(speaker, default);
        Assert.Equal(3, queries); // A topology change refreshes peers without waiting a minute.
    }
}
