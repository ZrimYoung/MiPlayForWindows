using System.Net;
using MiPlay.Desktop.Services.Dlna;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Collections.Concurrent;
using DLNACast.Core.MiPlay;
using DLNACast.Core.Models;

namespace MiPlay.Desktop.Services;

public sealed class SpeakerDiscovery : ISpeakerDiscovery
{
    public bool DlnaEnabled { get; set; }
    private readonly DlnaSpeakerDiscovery dlna = new();
    private readonly ConcurrentDictionary<string, IdentityEntry> identities = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> invalidated = new(StringComparer.OrdinalIgnoreCase);
    internal void InvalidateIdentity(string id) => invalidated[id] = 0;
    internal Func<string, bool> CanReadIdentity { get; set; } = _ => true;
    internal Func<IPAddress, int, CancellationToken, Task<SpeakerIdentity?>> ReadIdentity { get; set; } =
        (address, port, token) => new SpeakerIdentityReader().ReadAsync(address, token, port);
    internal Func<DateTimeOffset> Now { get; set; } = () => DateTimeOffset.UtcNow;

    internal void ObserveIdentity(string id, SpeakerIdentity identity)
    {
        var entry = identities.GetOrAdd(id, _ => new());
        lock (entry) { entry.Revision++; entry.Identity = identity; }
    }

    public async Task<IReadOnlyList<Speaker>> SearchAsync(CancellationToken cancellationToken)
    {
        var dlnaScan = DlnaEnabled ? dlna.SearchAsync(cancellationToken) : Task.FromResult<IReadOnlyList<Speaker>>([]);
        var addresses = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                        n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(a => a.Address)
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))
            .Distinct().ToArray();
        var results = await Task.WhenAll(addresses.Select(a => SearchInterfaceAsync(a, cancellationToken))).ConfigureAwait(false);
        var speakers = results.SelectMany(r => r).Select(FromAdvertisement).OfType<Speaker>()
            .DistinctBy(s => s.Id, StringComparer.OrdinalIgnoreCase).ToArray();
        var enriched = await Task.WhenAll(speakers.Select(s => EnrichAsync(s, cancellationToken))).ConfigureAwait(false);
        return enriched.Concat(await dlnaScan.ConfigureAwait(false)).OrderBy(s => s.Name).ToArray();
    }

    internal async Task<Speaker> EnrichAsync(Speaker speaker, CancellationToken token)
    {
        var entry = identities.GetOrAdd(speaker.Id, _ => new());
        await entry.Gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var controlPort = speaker.Renderer.MiPlayControlPort ?? MiPlayProtocolConstants.DefaultControlPort;
            if (CanReadIdentity(speaker.Id) && (invalidated.TryRemove(speaker.Id, out _) ||
                entry.Address != speaker.Renderer.Address.ToString() || entry.Port != controlPort || Now() >= entry.RetryAt))
            {
                entry.Address = speaker.Renderer.Address.ToString();
                entry.Port = controlPort;
                entry.RetryAt = Now().AddSeconds(60);
                long revision; lock (entry) revision = entry.Revision;
                try
                {
                    var result = await ReadIdentity(speaker.Renderer.Address, controlPort, token).ConfigureAwait(false);
                    lock (entry) if (entry.Revision == revision && result is not null) entry.Identity = result;
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
                catch (IOException) { }
                catch (SocketException) { }
            }
            SpeakerIdentity? identity; lock (entry) identity = entry.Identity;
            if (identity is null) return speaker;
            var name = string.IsNullOrWhiteSpace(identity.Name) ? speaker.Name : identity.Name;
            var model = string.IsNullOrWhiteSpace(identity.Model) ? speaker.Model : identity.Model;
            return speaker with { Name = name, Model = model, Identity = identity,
                Renderer = speaker.Renderer with { FriendlyName = name, ModelName = model } };
        }
        finally { entry.Gate.Release(); }
    }

    private sealed class IdentityEntry
    {
        internal readonly SemaphoreSlim Gate = new(1, 1);
        internal SpeakerIdentity? Identity;
        internal string? Address;
        internal int Port;
        internal DateTimeOffset RetryAt;
        internal long Revision;
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
        // DNS SRV belongs to mi-connect. Only a verified app-data descriptor
        // overrides the legacy default; other payload layouts remain unknown.
        var port = capabilities.AdvertisedControlPort ?? MiPlayProtocolConstants.DefaultControlPort;
        var uri = new Uri($"http://{device.Address}:{port}/");
        var endpoint = new UpnpServiceEndpoint("miplay", uri, uri, uri);
        var renderer = new RendererDevice(id, device.FriendlyName, "Xiaomi", model, device.Address,
            uri, endpoint, endpoint, endpoint, "") { MiPlayControlPort = port };
        return new Speaker(id, device.FriendlyName, model, renderer);
    }
}
