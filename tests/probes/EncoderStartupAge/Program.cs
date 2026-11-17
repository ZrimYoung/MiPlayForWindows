using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using DLNACast.Core.MiPlay;

if (args.Length != 1 || !File.Exists(args[0]))
{
    Console.Error.WriteLine("Usage: EncoderStartupAge <absolute path to FFmpeg with the aac encoder>");
    return 2;
}

// Synthetic PCM only: no audio capture, speaker connection, or playback.
// Model the desktop standalone runner: encode before media readiness, then
// consume without extra pacing. The group startup burst plan is NOT used by
// that runner and must not be applied to this baseline.
var results = new List<object>();
foreach (var repeat in Enumerable.Range(1, 3))
foreach (var waitMilliseconds in new[] { 100, 1_000, 3_000 })
foreach (var drainPrePlay in new[] { false, true })
    results.Add(await Measure(args[0], repeat, waitMilliseconds, drainPrePlay));
Console.WriteLine(JsonSerializer.Serialize(new
{
    measurement = "Synthetic PCM submission to local encoded-output consumption; NOT acoustic latency",
    codec = "FFmpeg aac / AAC-LC / 48000 Hz / stereo / 256 kbps",
    completedAtUtc = DateTimeOffset.UtcNow,
    results,
}, new JsonSerializerOptions { WriteIndented = true }));
return 0;

static async Task<object> Measure(string ffmpeg, int repeat, int waitMilliseconds, bool drainPrePlay)
{
    await using var encoder = MiPlayFfmpegAacEncoder.Start(ffmpeg, 256_000, "aac");
    var fifo = new MiPlayOrderedAccessUnitBuffer();
    var gate = new MiPlayEncoderOutputGate();
    var timings = Channel.CreateUnbounded<ulong>(new UnboundedChannelOptions
    {
        SingleReader = true, SingleWriter = true,
    });
    using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    var token = lifetime.Token;
    var clock = Stopwatch.StartNew();
    ulong NowUs() => (ulong)(clock.Elapsed.TotalMilliseconds * 1_000);
    long drained = 0;
    long submitted = 0;
    long parsed = 0;
    var pcm = new byte[1024 * 2 * 2];
    for (var sample = 0; sample < 1024; sample++)
    {
        var value = (short)(Math.Sin(sample * Math.Tau * 750 / 48_000) * 4096);
        for (var channel = 0; channel < 2; channel++)
        {
            var offset = (sample * 2 + channel) * 2;
            pcm[offset] = (byte)(value & 0xff);
            pcm[offset + 1] = (byte)((value >> 8) & 0xff);
        }
    }
    var writer = Task.Run(async () =>
    {
        try
        {
            for (long frame = 0; ; frame++)
            {
                var remaining = frame * 1024d / 48 - clock.Elapsed.TotalMilliseconds;
                if (remaining > 0) await Task.Delay(TimeSpan.FromMilliseconds(remaining), token);
                var timestamp = NowUs();
                await encoder.WritePcmAsync(pcm, token);
                await timings.Writer.WriteAsync(timestamp, token);
                Interlocked.Increment(ref submitted);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally { timings.Writer.TryComplete(); }
    }, token);
    var reader = Task.Run(async () =>
    {
        try
        {
            while (true)
            {
                var bytes = await encoder.ReadAccessUnitAsync(token)
                    ?? throw new EndOfStreamException("Encoder ended before the measurement completed.");
                var timestamp = await timings.Reader.ReadAsync(token);
                Interlocked.Increment(ref parsed);
                if (drainPrePlay && !gate.ShouldPublish(timestamp))
                {
                    Interlocked.Increment(ref drained);
                    continue;
                }
                await fifo.WriteAsync(new MiPlayEncodedAccessUnit(bytes, timestamp), token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception) { fifo.Complete(exception); throw; }
        finally { fifo.Complete(); }
    }, token);

    var samples = new List<(double At, double Age)>();
    double readyAt = 0;
    int queuedAtReady = 0;
    try
    {
        await Task.Delay(waitMilliseconds, token);
        readyAt = clock.Elapsed.TotalMilliseconds;
        queuedAtReady = fifo.BufferedCount;
        if (drainPrePlay) gate.TryArm(NowUs());
        // Read promptly, as the current standalone path does. No artificial
        // network backpressure or additional receiver scheduling is modeled.
        while (clock.Elapsed.TotalMilliseconds < readyAt + 750)
        {
            var frame = await fifo.ReadAsync(token);
            var now = clock.Elapsed.TotalMilliseconds;
            samples.Add((now - readyAt, now - frame.PresentationTimeMicroseconds / 1_000d));
        }
    }
    finally
    {
        lifetime.Cancel();
        await Task.WhenAll(writer, reader);
        // await using disposes the owned FFmpeg process even on a failed run.
    }

    var steady = samples.Where(x => x.At >= 500).Select(x => x.Age).Order().ToArray();
    double Round(double x) => Math.Round(x, 2);
    return new
    {
        repeat,
        mode = drainPrePlay ? "preplay_drain_gate" : "current_standalone_fifo",
        simulatedHandshakeMs = waitMilliseconds,
        queuedAtReady,
        firstOutputAgeMs = Round(samples[0].Age),
        waitForFirstOutputAfterReadyMs = Round(samples[0].At),
        consumedInFirst200Ms = samples.Count(x => x.At < 200),
        steadyMedianAgeMs = steady.Length == 0 ? (double?)null : Round(steady[steady.Length / 2]),
        steadyMaxAgeMs = steady.Length == 0 ? (double?)null : Round(steady[^1]),
        prePlayDiscardedFrames = Interlocked.Read(ref drained),
        submittedFrames = Interlocked.Read(ref submitted),
        parsedFrames = Interlocked.Read(ref parsed),
        consumedFrames = samples.Count,
    };
}
