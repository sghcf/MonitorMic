using MonitorMicWin;
using NAudio.Wave;
using Xunit;

namespace MonitorMicWin.Tests;

public sealed class AudioPipelineBufferTests
{
    [Fact]
    public void BufferNeverExceedsOnePointFiveSecondsUnderThirtyMinuteEquivalentPressure()
    {
        var provider = new BufferedWaveProvider(new WaveFormat(48000, 16, 2))
        {
            BufferLength = AudioPipeline.MaxBufferedBytes,
            DiscardOnBufferOverflow = true,
            ReadFully = true
        };
        var packet = new byte[4096];
        var maxObserved = 0;

        // 30 minutes of 48 kHz stereo PCM16 is 345,600,000 bytes. Reuse one
        // packet and push it without a consumer to exercise the drop-oldest path.
        var packets = 345_600_000 / packet.Length;
        for (var i = 0; i < packets; i++)
        {
            provider.AddSamples(packet, 0, packet.Length);
            maxObserved = Math.Max(maxObserved, provider.BufferedBytes);
        }

        Assert.Equal(AudioPipeline.MaxBufferedBytes, provider.BufferLength);
        Assert.InRange(maxObserved, 0, AudioPipeline.MaxBufferedBytes);
        Assert.InRange(provider.BufferedBytes, 0, AudioPipeline.MaxBufferedBytes);
    }
}
