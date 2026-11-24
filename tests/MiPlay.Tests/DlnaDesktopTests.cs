using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;
using DLNACast.Core.Abstractions;
using DLNACast.Core.Audio;
using DLNACast.Core.MiPlay;
using DLNACast.Core.Models;
using MiPlay.Desktop.Services;
using MiPlay.Desktop.Services.Dlna;

namespace MiPlay.Tests;

public sealed class DlnaDesktopTests
{
    [Fact]
    public void FormatsAndDescriptionRemainPortableAndExplicit()
    {
        Assert.Equal(new[] { DlnaStreamProfile.PcmWave, DlnaStreamProfile.Mp3Cbr320 }, ProtocolInfoMatcher.SelectProfiles("", true));
        Assert.Equal(new[] { DlnaStreamProfile.Mp3Cbr320 }, ProtocolInfoMatcher.SelectProfiles("http-get:*:audio/mpeg:*", true));
        Assert.Throws<NotSupportedException>(() => ProtocolInfoMatcher.SelectProfiles("http-get:*:audio/aac:*", true));
        Assert.False(SsdpResponseParser.TryGetDescriptionLocation("LOCATION: file:///tmp/a\r\n"u8, out _));
        var header = DlnaLiveStream.WaveHeader(PcmFrameFormat.MiPlayAac);
        Assert.Equal(48000u, BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(24)));
        Assert.Equal(192000u, BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(28)));
        Assert.Equal("RIFF", Encoding.ASCII.GetString(header, 0, 4));
        var evil = FakeRenderer.Description.Replace("/transport", "http://192.0.2.10/control");
        Assert.Throws<FormatException>(() => RendererDescriptionParser.Parse(evil, new Uri("http://127.0.0.1/description")));
    }

    [Fact]
    public async Task DiscoveryReadsDescriptionAndCapabilitiesWithoutCapturingAudio()
    {
        await using var renderer = new FakeRenderer();
        var discovery = new DlnaSpeakerDiscovery(_ => Task.FromResult<IReadOnlyList<Uri>>([new(renderer.Base, "description")]));
        var found = Assert.Single(await discovery.SearchAsync(CancellationToken.None));
        Assert.Equal("dlna:uuid:portable-test", found.Id);
        Assert.Equal("DLNA", found.Transport);
        Assert.Equal("Loopback renderer", found.Name);
        Assert.Contains("audio/wav", found.Renderer.SinkProtocolInfo);
        Assert.DoesNotContain("Play", renderer.Actions);
    }

    [Fact]
    public async Task WaveStreamingRoutesChannelsControlsVolumeSwitchesSourceAndReleasesCapture()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var renderer = new FakeRenderer();
        var platform = new FakePlatform();
        await using var session = new DlnaCastSession(platform);
        Assert.Equal(0, platform.Active);
        var device = RendererDescriptionParser.Parse(FakeRenderer.Description, new Uri(renderer.Base, "description"));
        await session.StartAsync(new(device, new CaptureSelection.SystemMix("first", "First"), "unused-for-wave",
            AudioChannelRoute.RightAsMono), timeout.Token);
        var first = await renderer.Received.Task.WaitAsync(timeout.Token);
        Assert.Equal("RIFF", Encoding.ASCII.GetString(first, 0, 4));
        Assert.Equal((short)-2000, BinaryPrimitives.ReadInt16LittleEndian(first.AsSpan(44)));
        Assert.Equal((short)-2000, BinaryPrimitives.ReadInt16LittleEndian(first.AsSpan(46)));
        Assert.Equal(MiPlayCastState.Streaming, session.Diagnostics.State);
        await session.SetVolumeAsync(63, timeout.Token);
        Assert.Equal(63, session.ReceiverVolume);
        Assert.Equal(63, renderer.Volume);
        renderer.RejectVolume = true;
        await Assert.ThrowsAsync<NotSupportedException>(() => session.SetVolumeAsync(70, timeout.Token));
        Assert.Equal(MiPlayCastState.Streaming, session.Diagnostics.State);
        Assert.Equal(63, session.ReceiverVolume);
        await session.SetCaptureAsync(new CaptureSelection.SystemMix("second", "Second"), timeout.Token);
        Assert.Equal("second", Assert.IsType<CaptureSelection.SystemMix>(platform.LastSelection).EndpointId);
        await Assert.ThrowsAsync<NotSupportedException>(() => session.SetDelayAsync(100, timeout.Token));
        await session.StopAsync();
        Assert.Equal(0, platform.Active);
        Assert.Equal(MiPlayCastState.Idle, session.Diagnostics.State);
        Assert.Contains("Stop", renderer.Actions);
    }

    [Fact]
    public async Task CancellationWhileRendererNeverFetchesReleasesCaptureAndListener()
    {
        await using var renderer = new FakeRenderer { FetchOnPlay = false };
        var platform = new FakePlatform();
        await using var session = new DlnaCastSession(platform);
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
        var device = RendererDescriptionParser.Parse(FakeRenderer.Description, new Uri(renderer.Base, "description"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.StartAsync(
            new(device, new CaptureSelection.SystemMix("first", "First"), "unused"), stop.Token));
        await session.StopAsync();
        Assert.Equal(0, platform.Active);
    }

    [Fact]
    public async Task SlowIncompleteHttpHeadersAreCancelledAndListenerIsReleased()
    {
        await using var frames = new PcmFrameBuffer(AudioChannelRoute.Stereo, PcmFrameFormat.MiPlayAac);
        var stream = new DlnaLiveStream(IPAddress.Loopback, frames, DlnaStreamProfile.PcmWave, "unused", CancellationToken.None);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, stream.Uri.Port);
        await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes("GET " + stream.Uri.AbsolutePath + " HTTP/1.1\r\n"));
        await stream.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        var rebound = new TcpListener(IPAddress.Loopback, stream.Uri.Port);
        rebound.Start(); rebound.Stop();
    }

    [Fact]
    public async Task MissingMp3EncoderFailsClearlyAndReleasesCapture()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var renderer = new FakeRenderer { RejectWave = true };
        var platform = new FakePlatform();
        await using var session = new DlnaCastSession(platform);
        var device = RendererDescriptionParser.Parse(FakeRenderer.Description, new Uri(renderer.Base, "description"));
        var error = await Assert.ThrowsAsync<IOException>(() => session.StartAsync(new(device,
            new CaptureSelection.SystemMix("test", "Test"), Path.Combine(Path.GetTempPath(), "nonexistent-miplay-test-ffmpeg")), timeout.Token));
        Assert.Contains("FFmpeg", error.Message);
        await session.StopAsync();
        Assert.Equal(0, platform.Active);
    }

    [FfmpegFact]
    public async Task RejectedWaveFallsBackToActualDecodableMp3AndDisposesEncoder()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var renderer = new FakeRenderer { RejectWave = true };
        var platform = new FakePlatform();
        await using var session = new DlnaCastSession(platform);
        var device = RendererDescriptionParser.Parse(FakeRenderer.Description, new Uri(renderer.Base, "description"));
        var path = Environment.GetEnvironmentVariable("MIPLAY_TEST_FFMPEG")!;
        await session.StartAsync(new(device, new CaptureSelection.SystemMix("test", "Test"), path), timeout.Token);
        var bytes = await renderer.Received.Task.WaitAsync(timeout.Token);
        Assert.True(bytes.Length > 512);
        Assert.NotEqual("RIFF", Encoding.ASCII.GetString(bytes, 0, 4));
        Assert.Equal(2, renderer.SetUris.Count);
        Assert.EndsWith(".mp3", renderer.SetUris.Last().AbsolutePath);
        await session.StopAsync();
        Assert.Equal(0, platform.Active);
        var start = new ProcessStartInfo(path) { RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in new[] { "-hide_banner", "-loglevel", "error", "-f", "mp3", "-i", "pipe:0", "-f", "s16le", "pipe:1" }) start.ArgumentList.Add(a);
        using var decoder = Process.Start(start)!;
        using var decoded = new MemoryStream();
        var output = decoder.StandardOutput.BaseStream.CopyToAsync(decoded, timeout.Token);
        var error = decoder.StandardError.ReadToEndAsync(timeout.Token);
        await decoder.StandardInput.BaseStream.WriteAsync(bytes, timeout.Token); decoder.StandardInput.Close();
        await decoder.WaitForExitAsync(timeout.Token); await output;
        Assert.True(decoder.ExitCode == 0, await error);
        Assert.True(decoded.Length > 1000);
    }

    private sealed class FakePlatform : IDesktopAudioPlatform
    {
        public int Active;
        public CaptureSelection? LastSelection;
        public event EventHandler? Suspending { add { } remove { } }
        public AudioSourceItem GetDefaultOutputDevice() => new("test", "Test");
        public IReadOnlyList<AudioSourceItem> GetOutputDevices() => [GetDefaultOutputDevice()];
        public IReadOnlyList<AudioSourceItem> GetCandidateProcesses() => [];
        public IAudioCaptureSource CreateCapture(CaptureSelection selection) { LastSelection = selection; return new FakeCapture(this, selection); }
        public IMediaPacingClock CreateMediaClock() => new MonotonicMediaPacingClock();
        public void Dispose() { }
    }
    private sealed class FakeCapture(FakePlatform owner, CaptureSelection selection) : IAudioCaptureSource
    {
        public CaptureSelection Selection => selection;
        public bool IsRunning { get; private set; }
        public CaptureHealth Health => new(DateTimeOffset.UtcNow, null, null, 0);
        public event EventHandler<Exception>? CaptureFailed { add { } remove { } }
        private CancellationTokenSource? lifetime; private Task? pump;
        public Task StartAsync(PcmFrameBuffer destination, CancellationToken token)
        {
            lifetime = CancellationTokenSource.CreateLinkedTokenSource(token); IsRunning = true; Interlocked.Increment(ref owner.Active);
            var frame = new byte[destination.FrameByteCount];
            for (var i = 0; i < frame.Length; i += 4)
            { BinaryPrimitives.WriteInt16LittleEndian(frame.AsSpan(i), 1000); BinaryPrimitives.WriteInt16LittleEndian(frame.AsSpan(i + 2), -2000); }
            pump = Task.Run(async () =>
            { try { while (!lifetime.IsCancellationRequested) { destination.Write(frame); await Task.Delay(5, lifetime.Token); } } catch (OperationCanceledException) { } });
            return Task.CompletedTask;
        }
        public async Task StopAsync()
        {
            if (!IsRunning) return;
            IsRunning = false; lifetime!.Cancel(); await pump!; lifetime.Dispose(); Interlocked.Decrement(ref owner.Active);
        }
        public async ValueTask DisposeAsync() => await StopAsync();
    }
    private sealed class FakeRenderer : IAsyncDisposable
    {
        public const string Description = """
        <root><device><deviceType>urn:schemas-upnp-org:device:MediaRenderer:1</deviceType><UDN>uuid:portable-test</UDN>
        <friendlyName>Loopback renderer</friendlyName><manufacturer>Test</manufacturer><modelName>Mock</modelName><serviceList>
        <service><serviceType>urn:schemas-upnp-org:service:AVTransport:1</serviceType><controlURL>/transport</controlURL></service>
        <service><serviceType>urn:schemas-upnp-org:service:ConnectionManager:1</serviceType><controlURL>/connection</controlURL></service>
        <service><serviceType>urn:schemas-upnp-org:service:RenderingControl:1</serviceType><controlURL>/volume</controlURL></service>
        </serviceList></device></root>
        """;
        private readonly HttpListener listener = new();
        private readonly CancellationTokenSource lifetime = new();
        private readonly Task server;
        private readonly ConcurrentBag<Task> requests = [];
        private Task? fetch;
        private Uri? current;
        public Uri Base { get; }
        public bool RejectWave, RejectVolume, FetchOnPlay = true;
        public int Volume = 35;
        public ConcurrentQueue<string> Actions = new();
        public ConcurrentQueue<Uri> SetUris = new();
        public TaskCompletionSource<byte[]> Received = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public FakeRenderer()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start(); var port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
            Base = new Uri($"http://127.0.0.1:{port}/"); listener.Prefixes.Add(Base.AbsoluteUri); listener.Start();
            server = Task.Run(async () =>
            {
                try
                { while (!lifetime.IsCancellationRequested) { var context = await listener.GetContextAsync().WaitAsync(lifetime.Token); requests.Add(HandleAsync(context)); } }
                catch (Exception ex) when (ex is OperationCanceledException or HttpListenerException or ObjectDisposedException) { }
            });
        }
        private async Task HandleAsync(HttpListenerContext context)
        {
            try
            {
                var text = Description;
                if (context.Request.HttpMethod == "POST")
                {
                    var action = (context.Request.Headers["SOAPAction"] ?? "").Trim('"').Split('#').Last(); Actions.Enqueue(action);
                    using var reader = new StreamReader(context.Request.InputStream);
                    var body = XDocument.Parse(await reader.ReadToEndAsync(lifetime.Token));
                    string Field(string key) => body.Descendants().First(x => x.Name.LocalName == key).Value;
                    if (action == "SetAVTransportURI")
                    {
                        current = new Uri(Field("CurrentURI")); SetUris.Enqueue(current);
                        if (RejectWave && current.AbsolutePath.EndsWith(".wav"))
                        { context.Response.StatusCode = 500; text = "<error><errorCode>714</errorCode><errorDescription>unsupported WAV</errorDescription></error>"; }
                    }
                    else if (action == "Play" && FetchOnPlay) fetch = FetchAsync(current!);
                    else if (action == "SetVolume")
                    {
                        if (RejectVolume) { context.Response.StatusCode = 500; text = "<error><errorCode>401</errorCode><errorDescription>Unsupported action</errorDescription></error>"; }
                        else Volume = int.Parse(Field("DesiredVolume"));
                    }
                    if (context.Response.StatusCode == 200) text = action switch
                    {
                        "GetProtocolInfo" => "<response><Sink>http-get:*:audio/wav:*,http-get:*:audio/mpeg:*</Sink></response>",
                        "GetVolume" => $"<response><CurrentVolume>{Volume}</CurrentVolume></response>",
                        "GetTransportInfo" => "<response><CurrentTransportState>PLAYING</CurrentTransportState></response>",
                        _ => "<response/>"
                    };
                }
                var bytes = Encoding.UTF8.GetBytes(text); context.Response.ContentLength64 = bytes.Length;
                await context.Response.OutputStream.WriteAsync(bytes, lifetime.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or HttpListenerException) { }
            finally { context.Response.Close(); }
        }
        private async Task FetchAsync(Uri uri)
        {
            try
            {
                using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false });
                using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, lifetime.Token);
                await using var stream = await response.Content.ReadAsStreamAsync(lifetime.Token);
                var buffer = new byte[8192]; var count = 0;
                while (count < buffer.Length)
                { var read = await stream.ReadAsync(buffer.AsMemory(count), lifetime.Token); if (read == 0) break; count += read; }
                Received.TrySetResult(buffer.AsSpan(0, count).ToArray());
                while (await stream.ReadAsync(buffer, lifetime.Token) > 0) { }
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or HttpRequestException) { }
        }
        public async ValueTask DisposeAsync()
        {
            lifetime.Cancel(); listener.Close(); await server; await Task.WhenAll(requests);
            if (fetch is not null) await fetch;
            lifetime.Dispose();
        }
    }
}
