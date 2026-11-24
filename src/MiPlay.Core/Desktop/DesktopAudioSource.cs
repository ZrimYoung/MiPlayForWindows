using DLNACast.Core.Models;

namespace MiPlay.Desktop.Services;

public sealed record DesktopAudioSource(string Mode = "system", string OutputDeviceId = "",
    int? ProcessId = null, bool IncludeChildren = true, string ProcessName = "", long? ProcessStartedAtUtcTicks = null)
{
    public void Validate()
    {
        if (Mode is not ("system" or "process") || OutputDeviceId is null || OutputDeviceId.Length > 2048 ||
            Mode == "process" && ProcessId is not > 0 || ProcessName is null || ProcessName.Length > 1024)
            throw new ArgumentException("请选择有效的音频来源。");
    }
    internal CaptureSelection Resolve(IDesktopAudioPlatform platform, bool requireActiveProcess = true)
    {
        Validate();
        if (Mode == "process")
        {
            if (!requireActiveProcess)
            {
                try
                {
                    using var running = System.Diagnostics.Process.GetProcessById(ProcessId!.Value);
                    if (running.HasExited || ProcessStartedAtUtcTicks is { } birth && running.StartTime.ToUniversalTime().Ticks != birth)
                        throw new AudioCaptureException("所选应用已退出或重新启动，请重新选择音源。");
                }
                catch (AudioCaptureException) { throw; }
                catch (Exception ex) { throw new AudioCaptureException("所选应用已退出，请重新选择音源。", ex); }
                return new CaptureSelection.Process(ProcessId!.Value, string.IsNullOrEmpty(ProcessName) ? "所选应用" : ProcessName, IncludeChildren);
            }
            var app = platform.GetCandidateProcesses().FirstOrDefault(p => p.ProcessId == ProcessId)
                ?? throw new AudioCaptureException("所选应用已退出或没有音频进程，请重新选择音源。");
            return new CaptureSelection.Process(ProcessId!.Value, app.DisplayName, IncludeChildren);
        }
        var device = string.IsNullOrEmpty(OutputDeviceId) ? platform.GetDefaultOutputDevice()
            : platform.GetOutputDevices().FirstOrDefault(d => d.Id == OutputDeviceId)
                ?? throw new AudioCaptureException("所选输出设备已断开，请重新选择音源。");
        return new CaptureSelection.SystemMix(device.Id, device.DisplayName);
    }
}

public sealed record DesktopDiagnostic(int BufferedMilliseconds, long Overruns, long Underruns,
    long AccessUnits, long RtpFrames, long WireBytes, double MaximumMediaSendGapMilliseconds,
    double MaximumInterReceiverSendSkewMilliseconds, int EncoderPendingInputFrames, int EncoderBufferedAccessUnits)
{
    public AudioCaptureDiagnostics? AudioCapture { get; init; }

    internal static DesktopDiagnostic? From(DLNACast.Core.MiPlay.MiPlayCastDiagnostics? d) => d is null ? null :
        new(d.BufferedMilliseconds, d.Overruns, d.Underruns, d.AccessUnits, d.RtpFrames, d.WireBytes,
            d.MaximumMediaSendGapMilliseconds, d.MaximumInterReceiverSendSkewMilliseconds,
            d.EncoderPendingInputFrames, d.EncoderBufferedAccessUnits) { AudioCapture = d.AudioCapture };
}
