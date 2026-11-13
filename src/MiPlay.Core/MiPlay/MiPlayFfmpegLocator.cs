namespace DLNACast.Core.MiPlay;

public static class MiPlayFfmpegLocator
{
    public const string EnvironmentVariableName = "DLNACAST_FFMPEG";

    public static string? FindExecutable(
        string? environmentOverride = null,
        string? applicationDirectory = null,
        string? pathValue = null)
    {
        var explicitPath = environmentOverride ??
            Environment.GetEnvironmentVariable(EnvironmentVariableName);
        if (IsExecutable(explicitPath))
        {
            return Path.GetFullPath(explicitPath!);
        }

        var baseDirectory = applicationDirectory ?? AppContext.BaseDirectory;
        var executableName = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        var adjacent = Path.Combine(baseDirectory, executableName);
        if (File.Exists(adjacent))
        {
            return Path.GetFullPath(adjacent);
        }

        var searchPath = pathValue ?? Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(searchPath))
        {
            return null;
        }

        foreach (var directory in searchPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(directory.Trim(), executableName);
                if (File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
            {
            }
        }
        return null;
    }

    public static string RequireExecutable()
    {
        return FindExecutable() ?? throw new FileNotFoundException(
            "MiPlay requires a compatible FFmpeg AAC encoder. " +
            $"Place FFmpeg beside the app, add it to PATH, or set {EnvironmentVariableName}.");
    }

    private static bool IsExecutable(string? path) =>
        !string.IsNullOrWhiteSpace(path) && File.Exists(path);
}
