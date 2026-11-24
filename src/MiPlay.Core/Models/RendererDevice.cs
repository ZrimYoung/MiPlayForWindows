using System.Net;

namespace DLNACast.Core.Models;

public sealed record UpnpServiceEndpoint(string ServiceType, Uri ControlUrl, Uri EventUrl, Uri DescriptionUrl);

public sealed record RendererDevice(
    string Udn,
    string FriendlyName,
    string Manufacturer,
    string ModelName,
    IPAddress Address,
    Uri DescriptionUrl,
    UpnpServiceEndpoint AvTransport,
    UpnpServiceEndpoint ConnectionManager,
    UpnpServiceEndpoint? RenderingControl,
    string SinkProtocolInfo)
{
    // The MiPlay audio control port is distinct from UPnP HTTP and mDNS SRV.
    // Null retains the established legacy speaker fallback.
    public int? MiPlayControlPort { get; init; }

    public string DisplayLabel => string.IsNullOrWhiteSpace(ModelName)
        ? FriendlyName
        : $"{FriendlyName} · {ModelName}";

    public override string ToString() => DisplayLabel;
}

