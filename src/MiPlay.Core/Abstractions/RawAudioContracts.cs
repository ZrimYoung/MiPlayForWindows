using DLNACast.Core.Audio;
using DLNACast.Core.Models;

namespace DLNACast.Core.Abstractions;

public interface IRawPcmSink
{
    /// <summary>Consumes borrowed samples synchronously; copy before enqueueing or retaining them.</summary>
    void Write(in RawPcmBlockView block);
}

/// <summary>Optional source-format capture path; the existing transport PCM contract remains unchanged.</summary>
public interface IRawAudioCaptureSource : IAsyncDisposable
{
    CaptureSelection Selection { get; }
    bool IsRunning { get; }
    CaptureHealth Health { get; }
    event EventHandler<Exception>? CaptureFailed;
    Task StartRawAsync(IRawPcmSink destination, CancellationToken cancellationToken);
    Task StopAsync();
}

public interface IRawAudioSourceCatalog : IAudioSourceCatalog
{
    IRawAudioCaptureSource CreateRawCapture(CaptureSelection selection);
}
