using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using MiPlay.Desktop.Services;

namespace MiPlay.Desktop;

public sealed record SpeakerPreferences(string Name = "", string Model = "", int? Volume = null, int DelayMilliseconds = 1000);
public sealed record DesktopSettings(string? LastSpeakerId = null, bool MuteLocalOutput = false,
    Dictionary<string, SpeakerPreferences>? Speakers = null);

public sealed class DesktopStore
{
    private readonly object sync = new();
    public string DirectoryPath { get; }
    private string SettingsPath => Path.Combine(DirectoryPath, "settings.json");
    private string LogPath => Path.Combine(DirectoryPath, "diagnostics.jsonl");
    public DesktopStore(string? baseDirectory = null)
    {
        DirectoryPath = baseDirectory ?? (OperatingSystem.IsMacOS()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "MiPlayDesktop")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MiPlayDesktop"));
        Directory.CreateDirectory(DirectoryPath);
    }
    public DesktopSettings Load()
    {
        try { return JsonSerializer.Deserialize<DesktopSettings>(File.ReadAllText(SettingsPath)) ?? new(); }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }
    public void Save(DesktopSettings settings)
    {
        lock (sync)
        {
        var temporary = SettingsPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(settings));
        File.Move(temporary, SettingsPath, true);
        }
    }
    public void Log(string category, string message, CastStatus? status = null)
    {
        lock (sync)
        {
            try
            {
                if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 1_000_000)
                    File.Move(LogPath, LogPath + ".previous", true);
                var diagnostic = status?.Diagnostics;
                // Deliberately omit raw protocol frames, account fields, keys and PCM.
                File.AppendAllText(LogPath, JsonSerializer.Serialize(new {
                    time = DateTimeOffset.UtcNow, category, message, state = status?.State.ToString(),
                    failure = status?.Failure.ToString(), diagnostic?.AccessUnits, diagnostic?.RtpFrames,
                    diagnostic?.BufferedMilliseconds, diagnostic?.Overruns, diagnostic?.Underruns,
                    diagnostic?.MaximumMediaSendGapMilliseconds, diagnostic?.MediaTimelineRebases
                }) + Environment.NewLine);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
    public string Export(Speaker? selected)
    {
        var destination = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            $"MiPlay-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        lock (sync)
        using (var archive = ZipFile.Open(destination, ZipArchiveMode.Create))
        {
            if (File.Exists(LogPath)) archive.CreateEntryFromFile(LogPath, "diagnostics.jsonl");
            if (File.Exists(LogPath + ".previous")) archive.CreateEntryFromFile(LogPath + ".previous", "previous.jsonl");
            using var writer = new StreamWriter(archive.CreateEntry("environment.json").Open());
            writer.Write(JsonSerializer.Serialize(new {
                appVersion = typeof(DesktopStore).Assembly.GetName().Version?.ToString(),
                os = RuntimeInformation.OSDescription, arch = RuntimeInformation.ProcessArchitecture.ToString(),
                speaker = selected?.Name, model = selected?.Model, firmware = "待真机记录", audible = "需人工确认"
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        return destination;
    }
}
