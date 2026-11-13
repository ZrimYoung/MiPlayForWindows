using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using DLNACast.Core.MiPlay;
using DLNACast.Core.Models;

namespace MiPlay.Desktop.Services;

public sealed class SpeakerDiscovery : ISpeakerDiscovery
{
    public async Task<IReadOnlyList<Speaker>> SearchAsync(CancellationToken cancellationToken)
    {
        var addresses = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                        n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(a => a.Address)
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))
            .Distinct().ToArray();
        var results = await Task.WhenAll(addresses.Select(a => SearchInterfaceAsync(a, cancellationToken))).ConfigureAwait(false);
        return results.SelectMany(r => r).Select(FromAdvertisement).OfType<Speaker>()
            .DistinctBy(s => s.Id, StringComparer.OrdinalIgnoreCase).OrderBy(s => s.Name).ToArray();
    }

    private static async Task<IReadOnlyList<MiPlayMdnsDevice>> SearchInterfaceAsync(IPAddress address, CancellationToken token)
    {
        try { return await new MiPlayMdnsDiscovery().SearchAsync(address, TimeSpan.FromSeconds(3), token).ConfigureAwait(false); }
        catch (SocketException) { return []; }
    }

    public static Speaker? FromAdvertisement(MiPlayMdnsDevice device)
    {
        var capabilities = device.Capabilities;
        if (!capabilities.SupportsMiPlayAudio || device.Address is null || IPAddress.IsLoopback(device.Address)) return null;
        var id = device.MicoAppData?.DeviceId;
        if (string.IsNullOrEmpty(id) && capabilities.IdHash.Length > 0) id = Convert.ToHexString(capabilities.IdHash);
        if (string.IsNullOrEmpty(id)) id = device.InstanceName;
        var model = device.TxtRecords.GetValueOrDefault("model", "");
        // The DNS service port belongs to mi-connect, not the legacy audio control channel.
        // This implementation's legacy bootstrap is validated on 8899 only.
        var port = MiPlayProtocolConstants.DefaultControlPort;
        var uri = new Uri($"http://{device.Address}:{port}/");
        var endpoint = new UpnpServiceEndpoint("miplay", uri, uri, uri);
        var renderer = new RendererDevice(id, device.FriendlyName, "Xiaomi", model, device.Address,
            uri, endpoint, endpoint, endpoint, "");
        return new Speaker(id, device.FriendlyName, model, renderer);
    }
}
