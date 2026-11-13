using DLNACast.Core.Abstractions;
using DLNACast.Core.MiPlay;
using DLNACast.Core.Models;

namespace MiPlay.Desktop.Services;

public interface IDesktopAudioPlatform : IAudioSourceCatalog, IDisposable
{
    AudioSourceItem GetDefaultOutputDevice();
    event EventHandler? Suspending;
    IMediaPacingClock CreateMediaClock();
}

public sealed record Speaker(string Id, string Name, string Model, RendererDevice Renderer)
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
    MiPlayCastDiagnostics Diagnostics { get; }
    event EventHandler<MiPlayCastDiagnostics>? DiagnosticsChanged;
    Task StartAsync(MiPlaySystemAudioRequest request, CancellationToken cancellationToken);
    Task StopAsync();
    Task SetVolumeAsync(int volume, CancellationToken cancellationToken);
    Task SetCaptureAsync(CaptureSelection selection, CancellationToken cancellationToken);
}

public sealed class NativeCastSession : ICastSession
{
    private readonly MiPlaySystemAudioTransmitter transmitter;
    public NativeCastSession(IDesktopAudioPlatform platform)
    {
        transmitter = new(new MiPlayLegacySystemAudioSessionRunner(platform,
            new FfmpegAacEncoderFactory(OperatingSystem.IsWindows() ? "aac_mf" : "aac"),
            platform.CreateMediaClock));
    }
    public MiPlayCastDiagnostics Diagnostics => transmitter.Diagnostics;
    public int? ReceiverVolume => transmitter.ReceiverVolume;
    public event EventHandler<MiPlayCastDiagnostics>? DiagnosticsChanged
    {
        add => transmitter.DiagnosticsChanged += value;
        remove => transmitter.DiagnosticsChanged -= value;
    }
    public Task StartAsync(MiPlaySystemAudioRequest request, CancellationToken token) => transmitter.StartAsync(request, token);
    public Task StopAsync() => transmitter.StopAsync();
    public Task SetVolumeAsync(int volume, CancellationToken token) => transmitter.SetReceiverVolumeAsync(volume, token);
    public Task SetCaptureAsync(CaptureSelection selection, CancellationToken token) => transmitter.SetCaptureSelectionAsync(selection, token);
    public ValueTask DisposeAsync() => transmitter.DisposeAsync();
}

public enum DesktopCastState { Idle, Connecting, Casting, Reconnecting, Stopping, Error }
public enum DesktopFailure { None, Permission, NoInput, Offline, Busy, Authentication, Encoder, Capture, Unknown }
public sealed record CastStatus(DesktopCastState State, string Message, DesktopFailure Failure = DesktopFailure.None,
    MiPlayCastDiagnostics? Diagnostics = null);
