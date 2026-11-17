namespace DLNACast.Core.MiPlay;

public sealed record MiPlayReceiverIdentity(string Name, string Model, string GroupId, string GroupName,
    string Channel, bool IsMaster);

public interface IMiPlayReceiverStateSource
{
    MiPlayReceiverIdentity? ReceiverIdentity { get; }
}

/// <summary>Read-only replies on the existing control connection. Missing replies never end playback.</summary>
internal sealed class MiPlayReceiverStateObserver(Action<int> volumeChanged, Action<MiPlayReceiverIdentity> identityChanged)
{
    private readonly object sync = new();
    private ushort? volumeSequence, identitySequence;
    internal void ExpectVolume(ushort sequence) { lock (sync) volumeSequence = sequence; }
    internal void ExpectIdentity(ushort sequence) { lock (sync) identitySequence = sequence; }
    internal void InvalidateVolume() { lock (sync) volumeSequence = null; }
    internal void Receive(MiPlayCommandFrame frame)
    {
        int? volume = null;
        MiPlayReceiverIdentity? identity = null;
        lock (sync)
        {
            if (frame.Command == MiPlayProtocolConstants.GetVolumeAcknowledgementCommand && frame.Sequence == volumeSequence &&
                MiPlaySetVolumePayloadCodec.TryDecodeAcknowledgement(frame.Payload, out var value) && value <= 100)
            {
                volumeSequence = null; volume = (int)value;
            }
            if (frame.Command == MiPlayProtocolConstants.GetDeviceInfoAcknowledgementCommand && frame.Sequence == identitySequence &&
                MiPlayLegacyDeviceInfoPayloadCodec.TryDecode(frame.Payload, out var info, out var consumed) &&
                info is not null && consumed == frame.Payload.Length &&
                (info.GetValue("groupId") is not null || info.GetValue("channel") == "center"))
            {
                identitySequence = null;
                // Do not retain account IDs or unrelated device-info fields.
                identity = new(info.GetValue("miName") ?? "", info.GetValue("model") ?? "",
                    info.GetValue("groupId") ?? "", info.GetValue("groupName") ?? "",
                    info.GetValue("channel") ?? "", info.GetValue("isMaster") == "1");
            }
        }
        if (volume is { } actual) volumeChanged(actual);
        if (identity is not null) identityChanged(identity);
    }
}
