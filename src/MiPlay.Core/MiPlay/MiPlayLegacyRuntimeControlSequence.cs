namespace DLNACast.Core.MiPlay;

internal sealed record MiPlayLegacyRuntimeControlCommand(
    ushort Command,
    ushort AcknowledgementCommand,
    ushort Sequence,
    byte[] Payload,
    byte[] CommandFrame,
    bool WaitForAcknowledgement);

/// <summary>
/// Allocates one shared command sequence for all controls sent after
/// SetMediaInfo. Runtime volume changes therefore advance subsequent heartbeat
/// sequences instead of colliding with the captured no-interleaving schedule.
/// </summary>
internal sealed class MiPlayLegacyRuntimeControlSequence(
    ushort initialSequence = MiPlayLegacyPostOpenPlaybackSession.FirstPeriodicHeartbeatSequence)
{
    public const int CapturedResumeRepeatDelayMilliseconds = 29;

    private ushort nextSequence = initialSequence;

    public MiPlayLegacyRuntimeControlCommand PrepareHeartbeat() =>
        Prepare(
            MiPlayProtocolConstants.HeartbeatCommand,
            MiPlayProtocolConstants.HeartbeatAcknowledgementCommand,
            []);

    public MiPlayLegacyRuntimeControlCommand PrepareGetVolume() => Prepare(
        MiPlayProtocolConstants.GetVolumeCommand, MiPlayProtocolConstants.GetVolumeAcknowledgementCommand, [], false);
    public MiPlayLegacyRuntimeControlCommand PrepareGetDeviceInfo() => Prepare(
        MiPlayProtocolConstants.GetDeviceInfoCommand, MiPlayProtocolConstants.GetDeviceInfoAcknowledgementCommand, [], false);

    public MiPlayLegacyRuntimeControlCommand PrepareMediaInfo(MiPlaySetMediaInfoPayload payload) => Prepare(
        MiPlayProtocolConstants.SetMediaInfoCommand, MiPlayProtocolConstants.SetMediaInfoAcknowledgementCommand,
        MiPlaySetMediaInfoPayloadCodec.Encode(payload), false);
    public MiPlayLegacyRuntimeControlCommand PreparePosition(long milliseconds)
    {
        if (milliseconds < 0) throw new ArgumentOutOfRangeException(nameof(milliseconds));
        var bytes = new byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(bytes, (ulong)milliseconds);
        return Prepare(0x0056, 0x0057, bytes, false);
    }
    public MiPlayLegacyRuntimeControlCommand PrepareMediaState(bool playing)
    {
        var bytes = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes, playing ? 2u : 3u);
        return Prepare(0x005e, 0x005f, bytes, false);
    }

    public MiPlayLegacyRuntimeControlCommand PreparePause() =>
        Prepare(
            MiPlayProtocolConstants.PauseCommand,
            MiPlayProtocolConstants.PauseAcknowledgementCommand,
            [],
            waitForAcknowledgement: false);

    public MiPlayLegacyRuntimeControlCommand PrepareClose() =>
        Prepare(
            MiPlayProtocolConstants.CloseDeviceCommand,
            MiPlayProtocolConstants.CloseDeviceAcknowledgementCommand,
            [],
            waitForAcknowledgement: false);

    public MiPlayLegacyRuntimeControlCommand PrepareResume() =>
        Prepare(
            MiPlayProtocolConstants.ResumeCommand,
            MiPlayProtocolConstants.ResumeAcknowledgementCommand,
            [],
            waitForAcknowledgement: false);

    public (MiPlayLegacyRuntimeControlCommand First, MiPlayLegacyRuntimeControlCommand Second)
        PrepareResumePair() =>
        (PrepareResume(), PrepareResume());

    public MiPlayLegacyRuntimeControlCommand PrepareSetVolume(int volume)
    {
        if (volume is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(volume));
        }

        return Prepare(
            MiPlayProtocolConstants.SetVolumeCommand,
            MiPlayProtocolConstants.SetVolumeAcknowledgementCommand,
            MiPlaySetVolumePayloadCodec.Encode((uint)volume));
    }

    public static bool IsExpectedAcknowledgement(
        MiPlayLegacyRuntimeControlCommand command,
        MiPlayCommandFrame acknowledgement)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(acknowledgement);
        if (acknowledgement.Command != command.AcknowledgementCommand ||
            acknowledgement.Sequence != command.Sequence) return false;
        if (command.Command == MiPlayProtocolConstants.SetVolumeCommand)
            return MiPlaySetVolumePayloadCodec.TryDecode(command.Payload, out var requested) &&
                MiPlaySetVolumePayloadCodec.TryDecodeAcknowledgement(acknowledgement.Payload, out var actual) &&
                requested == actual;
        return acknowledgement.Payload.AsSpan().SequenceEqual(command.Payload);
    }

    private MiPlayLegacyRuntimeControlCommand Prepare(
        ushort command,
        ushort acknowledgementCommand,
        byte[] payload,
        bool waitForAcknowledgement = true)
    {
        var sequence = nextSequence;
        nextSequence = unchecked((ushort)(nextSequence + 1));
        return new(
            command,
            acknowledgementCommand,
            sequence,
            payload,
            MiPlayCommandFrameCodec.Encode(command, sequence, payload),
            waitForAcknowledgement);
    }
}
