using MonitorMicWin;
using Xunit;

namespace MonitorMicWin.Tests;

public sealed class PcmStreamParserTests
{
    [Fact]
    public void ParsesHeaderAcrossTcpReads()
    {
        var parser = new PcmStreamParser();
        var chunks = new List<byte[]>();

        parser.Feed("PCM 480"u8, m => chunks.Add(m.ToArray()));
        parser.Feed("00 2 16\n"u8, m => chunks.Add(m.ToArray()));

        Assert.Equal(new PcmStreamFormat(48000, 2, 16), parser.Format);
        Assert.Empty(chunks);
    }

    [Fact]
    public void PreservesPcmFollowingHeaderInSameRead()
    {
        var parser = new PcmStreamParser();
        var chunks = new List<byte[]>();
        var data = "PCM 48000 2 16\n"u8.ToArray()
            .Concat(new byte[] { 1, 2, 3, 4, 5 })
            .ToArray();

        parser.Feed(data, m => chunks.Add(m.ToArray()));

        Assert.Single(chunks);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, chunks[0]);
        Assert.Equal(1, parser.PendingPcmBytes);
    }

    [Fact]
    public void HoldsIncompleteFrameUntilNextRead()
    {
        var parser = new PcmStreamParser();
        var chunks = new List<byte[]>();
        parser.Feed("PCM 48000 2 16\n\x01\x02\x03"u8, m => chunks.Add(m.ToArray()));
        parser.Feed(new byte[] { 4 }, m => chunks.Add(m.ToArray()));
        parser.Feed(new byte[] { 5, 6, 7, 8 }, m => chunks.Add(m.ToArray()));

        Assert.Equal(2, chunks.Count);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, chunks[0]);
        Assert.Equal(new byte[] { 5, 6, 7, 8 }, chunks[1]);
        Assert.Equal(0, parser.PendingPcmBytes);
    }

    [Theory]
    [InlineData("PCM 48000 1 16\n", 1)]
    [InlineData("PCM 48000 2 16\n", 2)]
    public void AcceptsSupportedMonoAndStereoFormats(string header, int channels)
    {
        var parser = new PcmStreamParser();
        parser.Feed(System.Text.Encoding.ASCII.GetBytes(header), _ => { });

        Assert.Equal(channels, parser.Format!.Value.Channels);
    }

    [Theory]
    [InlineData("PCM 0 2 16\n")]
    [InlineData("PCM 48000 0 16\n")]
    [InlineData("PCM 48000 9 16\n")]
    [InlineData("PCM 48000 2 8\n")]
    [InlineData("PCM 48000 2 24\n")]
    [InlineData("WAV 48000 2 16\n")]
    public void RejectsInvalidHeaders(string header)
    {
        var parser = new PcmStreamParser();

        var ex = Assert.Throws<PcmStreamProtocolException>(() =>
            parser.Feed(System.Text.Encoding.ASCII.GetBytes(header), _ => { }));

        Assert.Contains("非法 PCM 头", ex.Message);
    }

    [Fact]
    public void RejectsHeaderThatNeverTerminates()
    {
        var parser = new PcmStreamParser(maxHeaderBytes: 16);

        Assert.Throws<PcmStreamProtocolException>(() =>
            parser.Feed(new byte[17], _ => { }));
    }
}
