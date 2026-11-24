using System.Collections.Concurrent;
using DLNACast.Core.MiPlay;
using System.Text.Json;
using System.Runtime.InteropServices;
using MiPlay.Desktop;
using MiPlay.Desktop.Services;
using MiPlay.Platform.Mac;

if (args.Contains("--self-check")) { DesktopSelfCheck.Run(); return; }
var json = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
var outputGate = new object();
void Send(object value)
{
    lock (outputGate)
    {
        try { Console.WriteLine(JsonSerializer.Serialize(value, json)); }
        catch (IOException) { /* Parent closed its read pipe; stdin EOF owns shutdown. */ }
    }
}
using var platform = new MacAudioPlatform();
await using var controller = new DesktopController(platform, new SpeakerDiscovery(),
    new DesktopStore(Environment.GetEnvironmentVariable("MIPLAY_PROFILE_DIRECTORY")));
controller.Changed += (_, snapshot) => Send(new { @event = "snapshot", snapshot });
Send(new { @event = "snapshot", snapshot = controller.Snapshot });
controller.StartDiscovery();
using var shutdown = new CancellationTokenSource();
using var termination = PosixSignalRegistration.Create(PosixSignal.SIGTERM, signal => { signal.Cancel = true; shutdown.Cancel(); });
var pending = new ConcurrentDictionary<long, Task>();
long sequence = 0;
async Task HandleAsync(string line)
{
    try
    {
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        string Id() => root.GetProperty("id").GetString() ?? throw new ArgumentException("缺少音箱标识。");
        switch (root.GetProperty("command").GetString())
        {
            case "select": await controller.SetSelectedAsync(Id(), root.GetProperty("value").GetBoolean()); break;
            case "selectGroup": await controller.SetGroupSelectedAsync(Id(), root.GetProperty("value").GetBoolean()); break;
            case "volume": await controller.SetVolumeAsync(Id(), root.GetProperty("value").GetInt32()); break;
            case "groupVolume": await controller.SetGroupVolumeAsync(Id(), root.GetProperty("value").GetInt32()); break;
            case "groupDelay": await controller.SetGroupDelayAsync(Id(), root.GetProperty("value").GetInt32()); break;
            case "delay": await controller.SetDelayAsync(Id(), root.GetProperty("value").GetInt32()); break;
            case "mute": await controller.SetMutedAsync(root.GetProperty("value").GetBoolean()); break;
            case "synchronizedPlayback": await controller.SetSynchronizedPlaybackAsync(root.GetProperty("value").GetBoolean()); break;
            case "audioBitRate": await controller.SetAudioBitRateAsync(root.GetProperty("value").GetInt32()); break;
            case "audioSource": await controller.SetAudioSourceAsync(root.GetProperty("value").Deserialize<DesktopAudioSource>(json) ?? throw new ArgumentException("缺少音源。")); break;
            case "audioSources": await controller.RefreshAudioSourcesAsync(); break;
            case "channelRoute": await controller.SetChannelRouteAsync(Id(), root.GetProperty("value").GetString() ?? ""); break;
            case "dlnaEnabled": await controller.SetDlnaEnabledAsync(root.GetProperty("value").GetBoolean()); break;
            case "media": controller.UpdateMedia(root.GetProperty("value").Deserialize<MiPlayMediaSnapshot>(json)); break;
            case "scan": await controller.RefreshAsync(); break;
            case "stopAll": await controller.StopAllAsync(); break;
            case "export": Send(new { @event = "exported", path = controller.Export() }); break;
            default: throw new ArgumentException("不支持的操作。");
        }
    }
    catch (OperationCanceledException) { }
    catch (Exception ex) { Send(new { @event = "error", message = ex.Message }); }
}
try
{
    while (await Console.In.ReadLineAsync(shutdown.Token) is { } line)
    {
        if (line == "{\"command\":\"quit\"}") break;
        var id = Interlocked.Increment(ref sequence);
        var task = HandleAsync(line);
        pending[id] = task;
        _ = task.ContinueWith(_ => pending.TryRemove(id, out var ignored), TaskScheduler.Default);
    }
}
catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
finally
{
    // Closing the native app's input pipe (including a crash) releases capture and local muting.
    await controller.DisposeAsync();
    await Task.WhenAll(pending.Values);
}
