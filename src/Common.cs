namespace PSXVAG;

internal static class Common
{
    public const int BYTES_PER_FRAME = 0x10;
    public const int SAMPLES_PER_FRAME = (BYTES_PER_FRAME - 0x02) * 2;
}