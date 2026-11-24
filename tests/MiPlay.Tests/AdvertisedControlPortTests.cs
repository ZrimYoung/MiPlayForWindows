using System.Net;
using DLNACast.Core.MiPlay;
using DLNACast.Core.Models;
using MiPlay.Desktop.Services;

namespace MiPlay.Tests;

public sealed class AdvertisedControlPortTests
{
    [Fact]
    public void ObservedCompactDescriptorSelects18899WithoutUsingItsPrefixOrDnsServicePort()
    {
        var records = new Dictionary<string, string> { ["apps"] = "[5]", ["appsData"] = "gQAEBINJ0w==" };
        var capabilities = MiPlayMdnsCapabilities.Parse(records);
        Assert.Equal(18899, capabilities.AdvertisedControlPort);
        var speaker = SpeakerDiscovery.FromAdvertisement(new("fixture._mi-connect._udp.local", "Windows", null,
            IPAddress.Parse("192.0.2.7"), 56666, records));
        Assert.NotNull(speaker);
        Assert.Equal(18899, speaker.Renderer.MiPlayControlPort);
        Assert.Equal(18899, speaker.Renderer.DescriptionUrl.Port);
        Assert.Equal(18899, speaker.Renderer.AvTransport.ControlUrl.Port);
        Assert.NotEqual(1155, speaker.Renderer.MiPlayControlPort); // 04 83 is the descriptor prefix, not a port.
    }

    [Theory]
    [InlineData("04830001", 1)]
    [InlineData("048322C3", 8899)]
    [InlineData("048349D3", 18899)]
    [InlineData("0483FFFF", 65535)]
    public void RecognizedDescriptorReadsItsLastTwoBytesInNetworkOrder(string hex, int expected) =>
        Assert.Equal(expected, MiPlayMdnsCapabilities.Parse(Records(Convert.FromHexString(hex))).AdvertisedControlPort);

    [Theory]
    [InlineData("")]
    [InlineData("04")]
    [InlineData("0483")]
    [InlineData("048349")]
    [InlineData("04830000")]
    [InlineData("038349D3")]
    [InlineData("048249D3")]
    [InlineData("048349D300")]
    [InlineData("048349D30102030405060708090A0B0C0D0E0F")]
    public void UnknownOrIncompletePayloadKeepsLegacyDefaultAndIgnoresDnsSrv(string hex)
    {
        var records = Records(Convert.FromHexString(hex));
        Assert.Null(MiPlayMdnsCapabilities.Parse(records).AdvertisedControlPort);
        var speaker = SpeakerDiscovery.FromAdvertisement(new("fixture._mi-connect._udp.local", "Art", null,
            IPAddress.Parse("192.0.2.7"), 56666, records));
        Assert.NotNull(speaker);
        Assert.Equal(8899, speaker.Renderer.MiPlayControlPort);
        Assert.Equal(8899, speaker.Renderer.AvTransport.ControlUrl.Port);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-base64")]
    [InlineData("AQQEg0k=")]
    public void MissingOrMalformedContainerDoesNotInventAControlPort(string? data)
    {
        var records = new Dictionary<string, string> { ["apps"] = "[5]" };
        if (data is not null) records["appsData"] = data;
        Assert.Null(MiPlayMdnsCapabilities.Parse(records).AdvertisedControlPort);
    }

    [Fact]
    public void OtherApplicationPayloadIsNotAnAudioControlPort()
    {
        var records = Records([0x04, 0x83, 0x49, 0xd3]);
        records["apps"] = "[6]";
        Assert.Null(MiPlayMdnsCapabilities.Parse(records).AdvertisedControlPort);
        Assert.Null(SpeakerDiscovery.FromAdvertisement(new("fixture", "Other", null,
            IPAddress.Parse("192.0.2.7"), 56666, records)));
    }

    [Fact]
    public async Task IdentityQueryFollowsControlEndpointAndRefreshesImmediatelyWhenPortOrAddressChanges()
    {
        var observations = new List<(IPAddress Address, int Port)>();
        var discovery = new SpeakerDiscovery
        {
            Now = () => DateTimeOffset.UnixEpoch,
            ReadIdentity = (address, port, _) =>
            {
                observations.Add((address, port));
                return Task.FromResult<SpeakerIdentity?>(new("Windows", "", "", "", "center", false));
            }
        };
        var first = SpeakerAt("192.0.2.7", 8899);
        await discovery.EnrichAsync(first, default);
        await discovery.EnrichAsync(first, default);
        Assert.Single(observations);

        var changedPort = first with { Renderer = first.Renderer with { MiPlayControlPort = 18899 } };
        await discovery.EnrichAsync(changedPort, default);
        await discovery.EnrichAsync(changedPort, default);
        Assert.Equal(2, observations.Count);
        Assert.Equal((IPAddress.Parse("192.0.2.7"), 18899), observations[^1]);

        var changedAddress = changedPort with { Renderer = changedPort.Renderer with { Address = IPAddress.Parse("192.0.2.9") } };
        await discovery.EnrichAsync(changedAddress, default);
        Assert.Equal(3, observations.Count);
        Assert.Equal((IPAddress.Parse("192.0.2.9"), 18899), observations[^1]);
    }

    [Fact]
    public async Task ChangingPortDoesNotProbeAnActiveReceiverButRefreshesOnceIdle()
    {
        var idle = true;
        var ports = new List<int>();
        var discovery = new SpeakerDiscovery
        {
            Now = () => DateTimeOffset.UnixEpoch,
            CanReadIdentity = _ => idle,
            ReadIdentity = (_, port, _) => { ports.Add(port); return Task.FromResult<SpeakerIdentity?>(null); }
        };
        var speaker = SpeakerAt("192.0.2.7", 8899);
        await discovery.EnrichAsync(speaker, default);
        idle = false;
        speaker = speaker with { Renderer = speaker.Renderer with { MiPlayControlPort = 18899 } };
        await discovery.EnrichAsync(speaker, default);
        Assert.Equal(new[] { 8899 }, ports);
        idle = true;
        await discovery.EnrichAsync(speaker, default);
        Assert.Equal(new[] { 8899, 18899 }, ports);
    }

    private static Dictionary<string, string> Records(byte[] payload) => new()
    {
        ["apps"] = "[5]",
        ["appsData"] = Convert.ToBase64String(new byte[] { 1, checked((byte)payload.Length) }.Concat(payload).ToArray())
    };

    private static Speaker SpeakerAt(string address, int port)
    {
        // A separate HTTP description port must not override the selected
        // MiPlay control endpoint used for metadata queries.
        var uri = new Uri($"http://{address}:56666/");
        var endpoint = new UpnpServiceEndpoint("miplay", uri, uri, uri);
        return new("same-id", "Windows", "", new RendererDevice("same-id", "Windows", "Xiaomi", "",
            IPAddress.Parse(address), uri, endpoint, endpoint, endpoint, "") { MiPlayControlPort = port });
    }
}
