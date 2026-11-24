namespace DLNACast.Core.MiPlay;

/// <summary>The volume write completed, but its confirmation did not arrive. This is not a socket failure.</summary>
public sealed class MiPlayVolumeAcknowledgementTimeoutException(ushort sequence, Exception? inner = null)
    : TimeoutException($"MiPlay volume command 0x000C sequence 0x{sequence:X4} was not acknowledged.", inner);
