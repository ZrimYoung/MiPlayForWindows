using System.Buffers.Binary;
using System.Diagnostics;
using DLNACast.Core.MiPlay;

namespace MiPlay.Tests;

public sealed class EncoderIntegrationTests
{
    [FfmpegFact]
    public async Task ActualEncoderProducesDecodableStereoAudioAndReleasesTheChildProcess()
    {
        foreach (var bitrate in new[] { 256000, 320000, 512000 }) await VerifyBitrateAsync(bitrate);
    }
    private static async Task VerifyBitrateAsync(int bitrate)
    {
        // CI and the documented local check supply the packaged encoder explicitly.
        var path = Environment.GetEnvironmentVariable("MIPLAY_TEST_FFMPEG");
        Assert.False(string.IsNullOrWhiteSpace(path));
        var codec = OperatingSystem.IsWindows() ? "aac_mf" : "aac";
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        int pid;
        byte[] encoded;
        await using (var encoder = MiPlayFfmpegAacEncoder.Start(path, bitrate, codec))
        {
            pid = encoder.ProcessId;
            using var output = new MemoryStream();
            var reader = Task.Run(async () =>
            {
                while (await encoder.ReadAccessUnitAsync(timeout.Token) is { } frame)
                    await output.WriteAsync(frame, timeout.Token);
            });
            for (var i = 0; i < 48; i++)
                await encoder.WritePcmAsync(MiPlayPcmTestTone.CreateFrame(i * 1024, 1024), timeout.Token);
            await encoder.CompleteInputAsync(timeout.Token);
            await reader;
            encoded = output.ToArray();
            Assert.True(encoded.Length > 1000);
        }
        try { using var process = Process.GetProcessById(pid); Assert.True(process.HasExited); }
        catch (ArgumentException) { }

        var start = new ProcessStartInfo(path) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "-hide_banner", "-loglevel", "error", "-f", "aac", "-i", "pipe:0", "-f", "s16le", "-ar", "48000", "-ac", "2", "pipe:1" }) start.ArgumentList.Add(argument);
        using var decoder = Process.Start(start)!;
        using var decoded = new MemoryStream();
        var copy = decoder.StandardOutput.BaseStream.CopyToAsync(decoded, timeout.Token);
        var error = decoder.StandardError.ReadToEndAsync(timeout.Token);
        await decoder.StandardInput.BaseStream.WriteAsync(encoded, timeout.Token);
        decoder.StandardInput.Close();
        await copy;
        await decoder.WaitForExitAsync(timeout.Token);
        Assert.True(decoder.ExitCode == 0, await error);
        var pcm = decoded.ToArray();
        Assert.InRange(pcm.Length / 4, 48 * 1024, 52 * 1024);
        double energy = 0, rightEnergy = 0, differenceEnergy = 0;
        for (var offset = 0; offset < pcm.Length; offset += 4)
        {
            var left = BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(offset, 2)) / 32768d;
            var right = BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(offset + 2, 2)) / 32768d;
            energy += left * left;
            rightEnergy += right * right;
            differenceEnergy += (left - right) * (left - right);
        }
        Assert.InRange(Math.Sqrt(energy / (pcm.Length / 4)), .06, .11);
        Assert.InRange(Math.Sqrt(rightEnergy / (pcm.Length / 4)), .06, .11);
        // AAC is lossy and can decorrelate tiny noise components. Compare the
        // complete channels rather than imposing a sample-exact peak bound.
        Assert.InRange(Math.Sqrt(differenceEnergy / (pcm.Length / 4)), 0, .01);
    }
}

public sealed class FfmpegFactAttribute : FactAttribute
{
    public FfmpegFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MIPLAY_TEST_FFMPEG")))
            Skip = "Set MIPLAY_TEST_FFMPEG to run the real encoder/decoder integration test.";
    }
}
