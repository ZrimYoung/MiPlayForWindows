using DLNACast.Core.Abstractions;
using DLNACast.Core.Audio;
using DLNACast.Core.MiPlay;
using DLNACast.Core.Models;
using MiPlay.Desktop.Services;
using Microsoft.Win32;
using NAudio.CoreAudioApi;

namespace MiPlay.Platform.Windows;

public sealed class WindowsAudioPlatform : IDesktopAudioPlatform
{
    public event EventHandler? Suspending;
    public WindowsAudioPlatform() => SystemEvents.PowerModeChanged += OnPowerModeChanged;
    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend) Suspending?.Invoke(this, EventArgs.Empty);
    }
    private readonly AudioSourceCatalog catalog = new();
    public AudioSourceItem GetDefaultOutputDevice() => catalog.GetDefaultOutputDevice();
    public IReadOnlyList<AudioSourceItem> GetOutputDevices() => catalog.GetOutputDevices();
    public IReadOnlyList<AudioSourceItem> GetCandidateProcesses() => catalog.GetCandidateProcesses();
    public IAudioCaptureSource CreateCapture(CaptureSelection selection)
    {
        if (selection is CaptureSelection.SystemMix { MuteLocalOutput: true } or CaptureSelection.Process { MuteLocalOutput: true })
            throw new NotSupportedException("Windows 采集暂不支持静音本机。");
        return catalog.CreateCapture(selection);
    }
    public IMediaPacingClock CreateMediaClock() => MiPlayMediaPacingClock.Create();
    public void Dispose() => SystemEvents.PowerModeChanged -= OnPowerModeChanged;
}
