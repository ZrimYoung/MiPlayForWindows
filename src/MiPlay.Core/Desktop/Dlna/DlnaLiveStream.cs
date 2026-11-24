using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using DLNACast.Core.Audio;

namespace MiPlay.Desktop.Services.Dlna;

/// <summary>One renderer, one random URL, one streaming consumer and bounded I/O.</summary>
internal sealed class DlnaLiveStream : IAsyncDisposable
{
    private readonly TcpListener listener;
    private readonly IPAddress renderer;
    private readonly PcmFrameBuffer frames;
    private readonly DlnaStreamProfile profile;
    private readonly string ffmpeg;
    private readonly CancellationTokenSource lifetime;
    private readonly object sync = new();
    private readonly HashSet<Task> clients = [];
    private readonly Task accepting;
    private readonly TaskCompletionSource connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<Exception> failed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int streaming, disposed;
    private long bytes;
    public Uri Uri { get; }
    public Task Connected => connected.Task;
    public Task<Exception> Failed => failed.Task;
    public long WireBytes => Interlocked.Read(ref bytes);
    public DlnaLiveStream(IPAddress renderer, PcmFrameBuffer frames, DlnaStreamProfile profile, string ffmpeg,
        CancellationToken token)
    {
        this.renderer = renderer; this.frames = frames; this.profile = profile; this.ffmpeg = ffmpeg;
        lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        var local = ResolveLocalAddress(renderer);
        listener = Bind(local);
        var path = "/stream/" + Guid.NewGuid().ToString("N") + (profile == DlnaStreamProfile.PcmWave ? ".wav" : ".mp3");
        Uri = new UriBuilder("http", local.ToString(), ((IPEndPoint)listener.LocalEndpoint).Port, path).Uri;
        accepting = AcceptAsync();
    }
    private static IPAddress ResolveLocalAddress(IPAddress renderer)
    {
        using var route = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        route.Connect(new IPEndPoint(renderer, 9));
        return ((IPEndPoint)route.LocalEndPoint!).Address;
    }
    private static TcpListener Bind(IPAddress local)
    {
        // Different receivers get separate listeners without exposing every interface.
        for (var port = 49555; port <= 49565; port++)
        {
            var listener = new TcpListener(local, port);
            try { listener.Start(4); return listener; }
            catch (SocketException) { listener.Stop(); }
        }
        throw new IOException("DLNA 音频端口 49555–49565 已占用。");
    }
    private async Task AcceptAsync()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(lifetime.Token).ConfigureAwait(false);
                lock (sync)
                {
                    clients.RemoveWhere(t => t.IsCompleted);
                    if (clients.Count >= 4) { client.Dispose(); continue; }
                    clients.Add(HandleAsync(client));
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
        { if (!lifetime.IsCancellationRequested) failed.TrySetResult(ex); }
    }
    private async Task HandleAsync(TcpClient client)
    {
        var ownsStream = false;
        using (client)
        using (var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
        {
            try
            {
                client.NoDelay = true; client.SendBufferSize = 16 * 1024;
                var stream = client.GetStream();
                requestTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                if ((client.Client.RemoteEndPoint as IPEndPoint)?.Address.Equals(renderer) != true)
                { await RespondAsync(stream, "403 Forbidden", requestTimeout.Token).ConfigureAwait(false); return; }
                var request = await HeadersAsync(stream, requestTimeout.Token).ConfigureAwait(false);
                if (request is null) return;
                var parts = request.Split("\r\n", 2)[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 3 || parts[1] != Uri.AbsolutePath)
                { await RespondAsync(stream, "404 Not Found", requestTimeout.Token).ConfigureAwait(false); return; }
                if (parts[0] is not ("GET" or "HEAD"))
                { await RespondAsync(stream, "405 Method Not Allowed", requestTimeout.Token).ConfigureAwait(false); return; }
                if (request.Split("\r\n").Any(l => l.StartsWith("Range:", StringComparison.OrdinalIgnoreCase) &&
                    !l[(l.IndexOf(':') + 1)..].Trim().Equals("bytes=0-", StringComparison.OrdinalIgnoreCase)))
                { await RespondAsync(stream, "416 Range Not Satisfiable", requestTimeout.Token).ConfigureAwait(false); return; }
                if (parts[0] == "GET")
                {
                    if (Interlocked.CompareExchange(ref streaming, 1, 0) != 0)
                    { await RespondAsync(stream, "409 Conflict", requestTimeout.Token).ConfigureAwait(false); return; }
                    ownsStream = true;
                }
                var mime = profile == DlnaStreamProfile.PcmWave ? "audio/wav" : "audio/mpeg";
                var headers = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: {mime}\r\nConnection: close\r\nCache-Control: no-store\r\ntransferMode.dlna.org: Streaming\r\ncontentFeatures.dlna.org: DLNA.ORG_OP=00;DLNA.ORG_CI=0\r\n\r\n");
                await stream.WriteAsync(headers, requestTimeout.Token).ConfigureAwait(false);
                if (!ownsStream) return;
                requestTimeout.CancelAfter(Timeout.InfiniteTimeSpan);
                frames.TrimToLatest(2);
                if (profile == DlnaStreamProfile.PcmWave)
                {
                    await WriteAsync(stream, WaveHeader(frames.Format), lifetime.Token).ConfigureAwait(false);
                    connected.TrySetResult();
                    while (!lifetime.IsCancellationRequested)
                    {
                        var frame = await frames.ReadFrameOrSilenceAsync(lifetime.Token).ConfigureAwait(false);
                        await WriteAsync(stream, frame, lifetime.Token).ConfigureAwait(false);
                    }
                }
                else
                {
                    connected.TrySetResult();
                    await WriteMp3Async(stream, lifetime.Token).ConfigureAwait(false);
                    if (!lifetime.IsCancellationRequested) throw new IOException("FFmpeg MP3 编码器提前退出，请检查包内 MP3 编码支持。");
                }
            }
            catch (Exception ex)
            { if (ownsStream && !lifetime.IsCancellationRequested) failed.TrySetResult(ex); }
            finally { if (ownsStream) Interlocked.Exchange(ref streaming, 0); }
        }
    }
    private async Task WriteAsync(Stream stream, ReadOnlyMemory<byte> data, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        await stream.WriteAsync(data, timeout.Token).ConfigureAwait(false);
        Interlocked.Add(ref bytes, data.Length);
    }
    private async Task WriteMp3Async(Stream output, CancellationToken token)
    {
        var start = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var argument in new[] { "-hide_banner", "-loglevel", "error", "-f", "s16le", "-ar", frames.SampleRateHz.ToString(),
            "-ac", "2", "-i", "pipe:0", "-c:a", "libmp3lame", "-b:a", "320k", "-write_xing", "0", "-id3v2_version", "0", "-f", "mp3", "-flush_packets", "1", "pipe:1" })
            start.ArgumentList.Add(argument);
        using var process = StartEncoder(start);
        using var inputLifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        var error = DrainErrorsAsync(process.StandardError, inputLifetime.Token);
        var input = Task.Run(async () =>
        {
            try
            {
                while (!inputLifetime.IsCancellationRequested)
                {
                    var frame = await frames.ReadFrameOrSilenceAsync(inputLifetime.Token).ConfigureAwait(false);
                    await process.StandardInput.BaseStream.WriteAsync(frame, inputLifetime.Token).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException) { }
            finally { try { process.StandardInput.Close(); } catch { } }
        });
        try
        {
            var buffer = new byte[8192];
            while (true)
            {
                var read = await process.StandardOutput.BaseStream.ReadAsync(buffer, token).ConfigureAwait(false);
                if (read == 0) break;
                await WriteAsync(output, buffer.AsMemory(0, read), token).ConfigureAwait(false);
            }
        }
        finally
        {
            inputLifetime.Cancel();
            if (!process.HasExited) try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            await input.ConfigureAwait(false);
            try { await error.ConfigureAwait(false); } catch (OperationCanceledException) { }
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }
    private static Process StartEncoder(ProcessStartInfo start)
    {
        try { return Process.Start(start) ?? throw new IOException("无法启动 FFmpeg MP3 编码器。"); }
        catch (System.ComponentModel.Win32Exception error)
        { throw new IOException("无法启动 FFmpeg MP3 编码器，请检查测试包是否完整。", error); }
    }
    private static async Task DrainErrorsAsync(StreamReader reader, CancellationToken token)
    {
        var buffer = new char[1024];
        // Drain without retaining stderr; remote metadata/credentials never enter diagnostics.
        while (await reader.ReadAsync(buffer, token).ConfigureAwait(false) > 0) { }
    }
    internal static byte[] WaveHeader(PcmFrameFormat format)
    {
        var header = new byte[44];
        "RIFF"u8.CopyTo(header); BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), uint.MaxValue);
        "WAVEfmt "u8.CopyTo(header.AsSpan(8)); BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(20), 1); BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(22), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(24), (uint)format.SampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(28), (uint)(format.SampleRate * 4));
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(32), 4); BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(34), 16);
        "data"u8.CopyTo(header.AsSpan(36)); BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(40), uint.MaxValue);
        return header;
    }
    private static Task RespondAsync(Stream stream, string status, CancellationToken token) =>
        stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), token).AsTask();
    private static async Task<string?> HeadersAsync(Stream stream, CancellationToken token)
    {
        var bytes = new byte[8192]; var length = 0;
        while (length < bytes.Length)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(length), token).ConfigureAwait(false);
            if (read == 0) return null;
            length += read;
            if (bytes.AsSpan(0, length).IndexOf("\r\n\r\n"u8) >= 0) return Encoding.ASCII.GetString(bytes, 0, length);
        }
        return null;
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        lifetime.Cancel(); listener.Stop();
        await accepting.ConfigureAwait(false);
        Task[] pending; lock (sync) pending = clients.ToArray();
        await Task.WhenAll(pending).ConfigureAwait(false);
        lifetime.Dispose();
    }
}
