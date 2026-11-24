using DLNACast.Core.Audio;

namespace MiPlay.Tests;

public sealed class PcmAssemblerDiscontinuityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitBreakDiscardsOldPartialAndMarksOnlyTheNextCompletedFrame(bool miPlay)
    {
        var format = miPlay ? PcmFrameFormat.MiPlayAac : PcmFrameFormat.Default;
        await using var destination = new PcmFrameBuffer(AudioChannelRoute.Stereo, format);
        var assembler = new PcmFrameAssembler(destination);
        var partial = Enumerable.Repeat((byte)0x11, format.BytesPerFrame / 2).ToArray();
        assembler.Push(partial, 1_000_000_000UL);

        var fresh = Enumerable.Repeat((byte)0x22, format.BytesPerFrame * 2).ToArray();
        const ulong end = 1_050_000_000UL;
        assembler.Push(fresh, end, captureDiscontinuity: true);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var first = await destination.ReadCapturedFrameAsync(timeout.Token);
        var second = await destination.ReadCapturedFrameAsync(timeout.Token);
        Assert.All(first.Bytes, value => Assert.Equal((byte)0x22, value));
        Assert.All(second.Bytes, value => Assert.Equal((byte)0x22, value));
        Assert.True(first.CaptureDiscontinuity);
        Assert.False(second.CaptureDiscontinuity);
        var start = PcmPresentationTimestamp.CalculateMicroseconds(end, fresh.Length, format);
        Assert.Equal(start, first.PresentationTimeMicroseconds);
        Assert.Equal(PcmPresentationTimestamp.AddBytes(start, format.BytesPerFrame, format), second.PresentationTimeMicroseconds);
    }

    [Fact]
    public async Task EmptyBreakSurvivesUntilDataCompletesAndCannotKeepAnOldPartial()
    {
        var format = PcmFrameFormat.MiPlayAac;
        await using var destination = new PcmFrameBuffer(AudioChannelRoute.Stereo, format);
        var assembler = new PcmFrameAssembler(destination);
        assembler.Push(Enumerable.Repeat((byte)0x11, format.BytesPerFrame / 2).ToArray(), 1_000_000_000UL);
        assembler.Push([], 0, captureDiscontinuity: true);
        assembler.Push([], 0);

        var firstHalf = Enumerable.Repeat((byte)0x22, format.BytesPerFrame / 2).ToArray();
        var secondHalf = Enumerable.Repeat((byte)0x33, format.BytesPerFrame / 2).ToArray();
        const ulong firstHalfEnd = 1_020_000_000UL;
        assembler.Push(firstHalf, firstHalfEnd);
        Assert.Equal(0, destination.BufferedMilliseconds);
        assembler.Push(secondHalf, 1_030_666_666UL);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var first = await destination.ReadCapturedFrameAsync(timeout.Token);
        Assert.Equal(firstHalf.Concat(secondHalf), first.Bytes);
        Assert.True(first.CaptureDiscontinuity);
        Assert.Equal(PcmPresentationTimestamp.CalculateMicroseconds(firstHalfEnd, firstHalf.Length, format), first.PresentationTimeMicroseconds);

        assembler.Push(new byte[format.BytesPerFrame], 1_052_000_000UL);
        Assert.False((await destination.ReadCapturedFrameAsync(timeout.Token)).CaptureDiscontinuity);
    }

    [Fact]
    public async Task BreakAtAFrameBoundaryKeepsEarlierFramesAndMarksOneLaterFrame()
    {
        var format = PcmFrameFormat.MiPlayAac;
        await using var destination = new PcmFrameBuffer(AudioChannelRoute.Stereo, format);
        var assembler = new PcmFrameAssembler(destination);
        assembler.Push(Enumerable.Repeat((byte)0x11, format.BytesPerFrame).ToArray(), 1_000_000_000UL);
        assembler.Push(Enumerable.Repeat((byte)0x22, format.BytesPerFrame).ToArray(), 1_021_333_333UL, true);
        assembler.Push(Enumerable.Repeat((byte)0x33, format.BytesPerFrame).ToArray(), 1_042_666_666UL);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var before = await destination.ReadCapturedFrameAsync(timeout.Token);
        var after = await destination.ReadCapturedFrameAsync(timeout.Token);
        var following = await destination.ReadCapturedFrameAsync(timeout.Token);
        Assert.Equal((byte)0x11, before.Bytes[0]);
        Assert.False(before.CaptureDiscontinuity);
        Assert.Equal((byte)0x22, after.Bytes[0]);
        Assert.True(after.CaptureDiscontinuity);
        Assert.Equal((byte)0x33, following.Bytes[0]);
        Assert.False(following.CaptureDiscontinuity);
    }

    [Fact]
    public async Task RepeatedBreaksDiscardEveryUnfinishedPrefixWithoutRepeatingTheMarker()
    {
        var format = PcmFrameFormat.MiPlayAac;
        await using var destination = new PcmFrameBuffer(AudioChannelRoute.Stereo, format);
        var assembler = new PcmFrameAssembler(destination);
        var half = format.BytesPerFrame / 2;
        assembler.Push(Enumerable.Repeat((byte)0x11, half).ToArray(), 1_000_000_000UL);
        assembler.Push(Enumerable.Repeat((byte)0x22, half).ToArray(), 1_010_000_000UL, true);
        assembler.Push(Enumerable.Repeat((byte)0x33, format.BytesPerFrame * 2).ToArray(), 1_060_000_000UL, true);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var first = await destination.ReadCapturedFrameAsync(timeout.Token);
        var second = await destination.ReadCapturedFrameAsync(timeout.Token);
        Assert.All(first.Bytes, value => Assert.Equal((byte)0x33, value));
        Assert.True(first.CaptureDiscontinuity);
        Assert.False(second.CaptureDiscontinuity);
    }
}
