using System.Net;
using System.Net.Sockets;
using System.Text;
using MonitorMicWin;
using Xunit;

namespace MonitorMicWin.Tests;

public sealed class AudioPipelineReconnectTests
{
    [Fact]
    public async Task ReconnectsAfterMicStreamerClosesTheConnection()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var streamingCount = 0;
        var reconnected = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var pipeline = new AudioPipeline();
        pipeline.OnState += (_, streaming, _) =>
        {
            if (streaming && Interlocked.Increment(ref streamingCount) >= 2)
                reconnected.TrySetResult(true);
        };

        pipeline.Start("127.0.0.1", port);
        using var first = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(3));
        await SendFragmentedStreamAsync(first);
        first.Close();

        using var second = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(7));
        await SendFragmentedStreamAsync(second);
        await reconnected.Task.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.True(streamingCount >= 2);
    }

    [Fact]
    public async Task StopCancelsPendingReceivePromptly()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var pipeline = new AudioPipeline();

        pipeline.Start("127.0.0.1", port);
        using var client = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(3));

        var started = DateTime.UtcNow;
        pipeline.Stop();

        Assert.True((DateTime.UtcNow - started).TotalSeconds < 1.5,
            "Stop should cancel the pending TCP receive without waiting for a socket timeout.");
    }

    static async Task SendFragmentedStreamAsync(TcpClient client)
    {
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes("PCM 480"));
        await stream.WriteAsync(Encoding.ASCII.GetBytes("00 2 16\n"));
        await stream.WriteAsync(new byte[] { 1, 2 });
        await stream.WriteAsync(new byte[] { 3, 4, 5, 6 });
        await stream.FlushAsync();
    }
}
