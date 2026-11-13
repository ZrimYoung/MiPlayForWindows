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
    public AudioSourceItem GetDefaultOutputDevice()
    {
        using var enumerator = new MMDeviceEnumerator();
        using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        return new(device.ID, device.FriendlyName);
    }
    public IReadOnlyList<AudioSourceItem> GetOutputDevices() => [GetDefaultOutputDevice()];
    public IReadOnlyList<AudioSourceItem> GetCandidateProcesses() => [];
    public IAudioCaptureSource CreateCapture(CaptureSelection selection) => selection is CaptureSelection.SystemMix system
        ? new SystemLoopbackCaptureSource(system) : throw new NotSupportedException("首版支持系统声音投放。");
    public IMediaPacingClock CreateMediaClock() => MiPlayMediaPacingClock.Create();
    public void Dispose() => SystemEvents.PowerModeChanged -= OnPowerModeChanged;
}
