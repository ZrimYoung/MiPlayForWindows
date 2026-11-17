using DLNACast.Core.Abstractions;
using DLNACast.Core.MiPlay;
using DLNACast.Core.Models;

namespace MiPlay.Desktop.Services;

public interface IDesktopAudioPlatform : IAudioSourceCatalog, IDisposable
{
    bool SupportsLocalMute => false;
    AudioSourceItem GetDefaultOutputDevice();
    event EventHandler? Suspending;
    IMediaPacingClock CreateMediaClock();
}

public sealed record Speaker(string Id, string Name, string Model, RendererDevice Renderer,
    SpeakerIdentity? Identity = null)
{
    public override string ToString() => string.IsNullOrEmpty(Model) ? Name : $"{Name} · {Model}";
}

public interface ISpeakerDiscovery
{
    Task<IReadOnlyList<Speaker>> SearchAsync(CancellationToken cancellationToken);
}

public interface ICastSession : IAsyncDisposable
{
    int? ReceiverVolume => null;
    SpeakerIdentity? ReceiverIdentity => null;
    MiPlayCastDiagnostics Diagnostics { get; }
    event EventHandler<MiPlayCastDiagnostics>? DiagnosticsChanged;
    Task StartAsync(MiPlaySystemAudioRequest request, CancellationToken cancellationToken);
    Task StopAsync();
    Task SetVolumeAsync(int volume, CancellationToken cancellationToken);
    Task SetCaptureAsync(CaptureSelection selection, CancellationToken cancellationToken);
    Task SetDelayAsync(int milliseconds, CancellationToken cancellationToken) =>
        Task.FromException(new NotSupportedException("当前会话不支持实时调节延迟。"));
}

public sealed class NativeCastSession : ICastSession
{
    private readonly MiPlaySystemAudioTransmitter transmitter;
    public NativeCastSession(IDesktopAudioPlatform platform, MiPlayMediaState? mediaState = null)
    {
        transmitter = new(new MiPlayLegacySystemAudioSessionRunner(platform,
            new FfmpegAacEncoderFactory(OperatingSystem.IsWindows() ? "aac_mf" : "aac"),
            platform.CreateMediaClock, observeReceiverState: true, mediaState: mediaState,
            sourceName: DesktopSourceName.Resolve()));
    }
    public MiPlayCastDiagnostics Diagnostics => transmitter.Diagnostics;
    public int? ReceiverVolume => transmitter.ReceiverVolume;
    public SpeakerIdentity? ReceiverIdentity => transmitter.ReceiverIdentity is { } info
        ? new(info.Name, info.Model, info.GroupId, info.GroupName, info.Channel, info.IsMaster) : null;
    public event EventHandler<MiPlayCastDiagnostics>? DiagnosticsChanged
    {
        add => transmitter.DiagnosticsChanged += value;
        remove => transmitter.DiagnosticsChanged -= value;
    }
    public Task StartAsync(MiPlaySystemAudioRequest request, CancellationToken token) => transmitter.StartAsync(request, token);
    public Task StopAsync() => transmitter.StopAsync();
    public Task SetVolumeAsync(int volume, CancellationToken token) => transmitter.SetReceiverVolumeAsync(volume, token);
    public Task SetCaptureAsync(CaptureSelection selection, CancellationToken token) => transmitter.SetCaptureSelectionAsync(selection, token);
    public Task SetDelayAsync(int milliseconds, CancellationToken token) => transmitter.SetPlaybackDelayAsync(checked(milliseconds * 1000), token);
    public ValueTask DisposeAsync() => transmitter.DisposeAsync();
}

internal static class DesktopSourceName
{
    public static string Resolve() => Normalize(Environment.GetEnvironmentVariable("MIPLAY_SOURCE_NAME"))
        ?? Normalize(Environment.MachineName) ?? "MiPlay Desktop";

    internal static string? Normalize(string? value)
    {
        value = value?.Trim();
        return !string.IsNullOrEmpty(value) && System.Text.Encoding.UTF8.GetByteCount(value) <= 128 &&
               !value.Any(char.IsControl) ? value : null;
    }
}

public enum DesktopCastState { Idle, Connecting, Casting, Reconnecting, Stopping, Error }
public enum DesktopFailure { None, Permission, NoInput, Offline, Busy, Authentication, Encoder, Capture, Unknown, VolumeControl, Compatibility }
public sealed record CastStatus(DesktopCastState State, string Message, DesktopFailure Failure = DesktopFailure.None,
    MiPlayCastDiagnostics? Diagnostics = null);
