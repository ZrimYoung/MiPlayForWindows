using DLNACast.Core.MiPlay;

namespace DLNACast.Tests;

public sealed class MiPlayReceiverStateObserverTests
{
    [Fact]
    public void VolumeOnlyAcceptsMatchingFreshReadAndNeverWritesBack()
    {
        var values = new List<int>();
        var observer = new MiPlayReceiverStateObserver(values.Add, _ => { });
        observer.ExpectVolume(42);
        observer.Receive(new(0x000f, 41, MiPlayLegacyStatusScalarCodec.Encode(50)));
        observer.Receive(new(0x000d, 42, MiPlayLegacyStatusScalarCodec.Encode(50)));
        observer.Receive(new(0x000f, 42, MiPlayLegacyStatusScalarCodec.Encode(101)));
        Assert.Empty(values);
        observer.Receive(new(0x000f, 42, MiPlayLegacyStatusScalarCodec.Encode(32)));
        observer.Receive(new(0x000f, 42, MiPlayLegacyStatusScalarCodec.Encode(33)));
        Assert.Equal(new[] { 32 }, values);
        observer.ExpectVolume(43);
        observer.InvalidateVolume(); // A local volume change supersedes an older query.
        observer.Receive(new(0x000f, 43, MiPlayLegacyStatusScalarCodec.Encode(10)));
        Assert.Single(values);
    }

    [Fact]
    public void IdentityRequiresCorrelatedCompleteGroupEvidenceAndSupportsExplicitDissolution()
    {
        var identities = new List<MiPlayReceiverIdentity>();
        var observer = new MiPlayReceiverStateObserver(_ => { }, identities.Add);
        observer.ExpectIdentity(7);
        byte[] Payload(Dictionary<string, string> fields) => MiPlayLegacyDeviceInfoPayloadCodec.Encode(fields);
        observer.Receive(new(0x001f, 7, Payload(new() { ["miName"] = "Partial response" })));
        Assert.Empty(identities);
        observer.Receive(new(0x001f, 6, Payload(new() { ["groupId"] = "pair", ["channel"] = "left" })));
        Assert.Empty(identities);
        observer.Receive(new(0x001f, 7, Payload(new() { ["miName"] = "Art", ["model"] = "L09A",
            ["groupId"] = "pair", ["groupName"] = "Stereo", ["channel"] = "left", ["isMaster"] = "1", ["accountId"] = "discard" })));
        Assert.Equal(new MiPlayReceiverIdentity("Art", "L09A", "pair", "Stereo", "left", true), Assert.Single(identities));
        observer.ExpectIdentity(8);
        observer.Receive(new(0x001f, 8, Payload(new() { ["channel"] = "center" })));
        Assert.Empty(identities.Last().GroupId);
    }
}
