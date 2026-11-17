namespace DLNACast.Core.MiPlay;

public static class MiPlayAudioQuality
{
    public static bool IsSupported(int bitRate) => bitRate is 256_000 or 320_000 or 512_000;
    public static void Validate(int bitRate)
    {
        if (!IsSupported(bitRate)) throw new ArgumentOutOfRangeException(nameof(bitRate), "请选择 256、320 或实验性 512 kbps。");
    }
}
