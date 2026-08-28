using MonitorMicWin;
using Xunit;

namespace MonitorMicWin.Tests;

public sealed class PcmGainTests
{
    [Fact]
    public void AppliesGainToLittleEndianPcm16()
    {
        var pcm = new byte[] { 0x00, 0x10, 0x00, 0xF0 };

        PcmGain.ApplyInPlace(pcm, 2f);

        Assert.Equal(0x2000, BitConverter.ToInt16(pcm, 0));
        Assert.Equal(unchecked((short)0xE000), BitConverter.ToInt16(pcm, 2));
    }

    [Fact]
    public void LimitsPositiveAndNegativeClipping()
    {
        var pcm = new byte[] { 0xFF, 0x7F, 0x00, 0x80 };

        PcmGain.ApplyInPlace(pcm, 16f);

        Assert.Equal(short.MaxValue, BitConverter.ToInt16(pcm, 0));
        Assert.Equal(short.MinValue, BitConverter.ToInt16(pcm, 2));
    }
}
