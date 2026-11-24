namespace DLNACast.Core.MiPlay;

/// <summary>Per-speaker lead relative to one group clock; media PTS stays shared.</summary>
internal sealed class MiPlayReceiverTiming
{
    private readonly long delta;

    public MiPlayReceiverTiming(int groupDelayMicroseconds, int receiverDelayMicroseconds)
    {
        if (groupDelayMicroseconds is < 0 or > 2_000_000)
            throw new ArgumentOutOfRangeException(nameof(groupDelayMicroseconds));
        if (receiverDelayMicroseconds is < 0 or > 2_000_000)
            throw new ArgumentOutOfRangeException(nameof(receiverDelayMicroseconds));
        delta = (long)receiverDelayMicroseconds - groupDelayMicroseconds;
    }

    public ulong ToReceiver(ulong groupOffset) => Shift(groupOffset, delta);
    public ulong ToGroup(ulong receiverOffset) => Shift(receiverOffset, -delta);

    private static ulong Shift(ulong value, long difference) => difference >= 0
        ? checked(value + (ulong)difference)
        : checked(value - (ulong)-difference);
}
