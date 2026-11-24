using System.Net;
using System.Net.Sockets;
using DLNACast.Core.MiPlay;
using DLNACast.Core.Models;

namespace MiPlay.Tests;

public sealed class ControlPortConnectionTests
{
    [Fact]
    public async Task ControlConnectionReachesTheAdvertisedListenerInsteadOfAnHttpOrDefaultPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var accepted = listener.AcceptTcpClientAsync(timeout.Token).AsTask();
            using var client = await MiPlayLegacySystemAudioSessionRunner.ConnectControlAsync(
                Renderer() with { MiPlayControlPort = port }, timeout.Token);
            using var receiver = await accepted;
            Assert.Equal(port, ((IPEndPoint)client.Client.RemoteEndPoint!).Port);
            Assert.Equal(0, receiver.Available);
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public void UnadvertisedAudioPortRetainsTheLegacyFallbackDespiteAnUnrelatedHttpPort() =>
        Assert.Equal(8899, MiPlayLegacySystemAudioSessionRunner.ResolveControlPort(Renderer()));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    public void InvalidAudioPortsAreRejectedBeforeConnecting(int port) =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            MiPlayLegacySystemAudioSessionRunner.ResolveControlPort(Renderer() with { MiPlayControlPort = port }));

    [Fact]
    public async Task AlreadyCancelledControlConnectionDoesNotContactTheReceiver()
    {
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            MiPlayLegacySystemAudioSessionRunner.ConnectControlAsync(Renderer() with { MiPlayControlPort = 18899 }, stop.Token));
    }

    private static RendererDevice Renderer()
    {
        var uri = new Uri("http://127.0.0.1:12345/");
        var endpoint = new UpnpServiceEndpoint("synthetic", uri, uri, uri);
        return new("synthetic", "Synthetic receiver", "", "", IPAddress.Loopback, uri, endpoint, endpoint, endpoint, "");
    }
}
