using DLNACast.Core.Models;

namespace MiPlay.Desktop.Services.Dlna;

public enum DlnaStreamProfile { PcmWave, Mp3Cbr320 }
public sealed record DlnaTransportStatus(string State, string Status);

/// <summary>Portable SSDP/UPnP renderer discovery; never opens an audio stream.</summary>
public sealed class DlnaSpeakerDiscovery : ISpeakerDiscovery
{
    private readonly Func<CancellationToken, Task<IReadOnlyList<Uri>>> find;
    public DlnaSpeakerDiscovery() : this(token => new SsdpMulticastSearchClient().SearchAsync(
        ["urn:schemas-upnp-org:device:MediaRenderer:1", "urn:schemas-upnp-org:service:AVTransport:1"],
        TimeSpan.FromSeconds(3), token)) { }
    internal DlnaSpeakerDiscovery(Func<CancellationToken, Task<IReadOnlyList<Uri>>> find) => this.find = find;
    public async Task<IReadOnlyList<Speaker>> SearchAsync(CancellationToken cancellationToken)
    {
        var locations = await find(cancellationToken).ConfigureAwait(false);
        using var client = new UpnpSoapClient();
        using var gate = new SemaphoreSlim(8);
        var found = await Task.WhenAll(locations.Distinct().Take(64).Select(async location =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (location.Scheme is not ("http" or "https") || location.UserInfo.Length != 0) return null;
                var xml = await client.HttpClient.GetStringAsync(location, cancellationToken).ConfigureAwait(false);
                var device = RendererDescriptionParser.Parse(xml, location);
                if (string.IsNullOrWhiteSpace(device.Udn)) return null;
                // A failed optional capability query must not hide an otherwise valid renderer.
                try
                {
                    using var renderer = new RendererController(new UpnpSoapClient(), ownsClient: true);
                    device = device with { SinkProtocolInfo = await renderer.GetSinkProtocolInfoAsync(device, cancellationToken).ConfigureAwait(false) };
                }
                catch (Exception ex) when (ex is HttpRequestException or UpnpException or TaskCanceledException or FormatException) { }
                return new Speaker("dlna:" + device.Udn, device.FriendlyName, device.ModelName, device, Transport: "DLNA");
            }
            catch (Exception ex) when (ex is HttpRequestException or UpnpException or TaskCanceledException or FormatException or System.Xml.XmlException or System.Net.Sockets.SocketException)
            { return null; }
            finally { gate.Release(); }
        })).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return found.OfType<Speaker>().DistinctBy(s => s.Id, StringComparer.OrdinalIgnoreCase).OrderBy(s => s.Name).ToArray();
    }
}
