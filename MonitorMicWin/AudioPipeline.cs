using System.Net.Sockets;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MonitorMicWin;

/// <summary>
/// Client-side audio pipeline: connect to the Android MicStreamer TCP server,
/// parse framed PCM16, keep a bounded jitter buffer, and render only to VB-CABLE.
/// </summary>
sealed class AudioPipeline : IDisposable
{
    public const int Port = 50010;

    public event Action<bool, bool, string>? OnState;
    public event Action<float>? OnLevel;

    readonly object gate = new();
    volatile bool running;
    Thread? connThread;
    CancellationTokenSource? connectionCts;
    TcpClient? activeClient;

    int rate = 48000;
    int channels = 2;
    volatile bool headerParsed;
    DateTime lastDataUtc = DateTime.MinValue;
    DateTime lastLevelUtc = DateTime.MinValue;

    WasapiOut? output;
    MMDevice? outputDevice;
    BufferedWaveProvider? provider;
    string? deviceName;
    DateTime lastOutputTry = DateTime.MinValue;
    DateTime lastNoDeviceLog = DateTime.MinValue;
    DateTime lastDiagLog = DateTime.MinValue;
    int rebuildingOutput;
    System.Threading.Timer? watchdog;

    public bool Running => running;
    public bool Streaming => headerParsed && (DateTime.UtcNow - lastDataUtc).TotalSeconds < 2.5;
    public string StreamInfo => headerParsed ? $"{rate} Hz · {channels} ch" : "";
    public string? DeviceName => deviceName;
    public volatile bool CableInstalledNow;

    public void Start(string host, int port = Port)
    {
        Stop();
        host = host.Trim();
        var hostType = Uri.CheckHostName(host);
        if (hostType is not (UriHostNameType.IPv4 or UriHostNameType.Dns))
        {
            Log.Info($"❌ 无效的显示器 IP: {host}");
            return;
        }

        running = true;
        var cts = new CancellationTokenSource();
        connectionCts = cts;
        connThread = new Thread(() => ConnLoop(host, port, cts.Token))
        {
            IsBackground = true,
            Name = "tcp-conn"
        };
        connThread.Start();
        watchdog = new System.Threading.Timer(_ => WatchdogTick(), null, 2000, 2000);
        Log.Info($"音频接收器已启动，连接 {host}:{port}（断线自动重连）");
        OnState?.Invoke(true, false, "");
    }

    public void Stop()
    {
        running = false;
        var cts = Interlocked.Exchange(ref connectionCts, null);
        cts?.Cancel();
        var client = Interlocked.Exchange(ref activeClient, null);
        try { client?.Close(); } catch { }

        var thread = connThread;
        if (thread != null && thread != Thread.CurrentThread && thread.IsAlive)
        {
            try { thread.Join(1000); } catch { }
        }
        cts?.Dispose();
        connThread = null;
        watchdog?.Dispose();
        watchdog = null;
        TeardownOutput();
        headerParsed = false;
        OnState?.Invoke(false, false, "");
        OnLevel?.Invoke(0);
    }

    void ConnLoop(string host, int port, CancellationToken cancellationToken)
    {
        while (running && !cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var client = new TcpClient
                {
                    ReceiveTimeout = 10000,
                    ReceiveBufferSize = 65536,
                    NoDelay = true
                };
                Interlocked.Exchange(ref activeClient, client);
                client.ConnectAsync(host, port)
                    .WaitAsync(TimeSpan.FromSeconds(5), cancellationToken)
                    .GetAwaiter().GetResult();
                Log.Info($"已连接显示器 {host}:{port}");
                headerParsed = false;
                lastDataUtc = DateTime.MinValue;
                ReadLoop(client);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (PcmStreamProtocolException ex)
            {
                if (running) Log.Info($"❌ 协议头无效，断开并重连: {ex.Message}");
            }
            catch (Exception ex)
            {
                if (running)
                {
                    Log.Info($"连接断开/失败: {ex.Message}");
                    OnState?.Invoke(true, false, "");
                }
            }
            finally
            {
                Interlocked.Exchange(ref activeClient, null);
                headerParsed = false;
                OnState?.Invoke(running, false, "");
            }

            if (running && !cancellationToken.IsCancellationRequested)
            {
                Log.Info("2s 后重连…");
                if (cancellationToken.WaitHandle.WaitOne(2000)) break;
            }
        }
    }

    void ReadLoop(TcpClient client)
    {
        var stream = client.GetStream();
        var parser = new PcmStreamParser();
        var buf = new byte[65536];
        while (running)
        {
            int n;
            try
            {
                n = stream.Read(buf, 0, buf.Length);
            }
            catch (IOException)
            {
                if ((DateTime.UtcNow - lastDataUtc).TotalSeconds > 10)
                    throw new IOException("超过 10s 无数据");
                continue;
            }
            if (n <= 0) throw new IOException("连接被关闭");

            parser.Feed(buf.AsSpan(0, n), pcm =>
            {
                if (!headerParsed && parser.Format is { } format)
                    ApplyFormat(format);
                IngestPcm(pcm.Span);
            });
            if (!headerParsed && parser.Format is { } parsedFormat)
                ApplyFormat(parsedFormat);
        }
    }

    void ApplyFormat(PcmStreamFormat format)
    {
        rate = format.SampleRate;
        channels = format.Channels;
        headerParsed = true;
        lastDataUtc = DateTime.UtcNow;
        Log.Info($"流参数: {format}");
        OnState?.Invoke(true, true, $"{rate} Hz · {channels} ch");
    }

    void IngestPcm(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0) return;
        lastDataUtc = DateTime.UtcNow;
        UpdateLevel(data);
        EnsureOutput();

        // VB-CABLE is exposed as a stereo render endpoint. A mono stream is
        // explicitly duplicated instead of relying on an implicit device format.
        var outputData = channels == 1 ? MonoToStereo(data) : data.ToArray();
        lock (gate)
        {
            if (provider == null) return;
            // Drop old queued audio at a high-water mark so latency cannot grow.
            if (provider.BufferedDuration.TotalSeconds > 0.8)
                provider.ClearBuffer();
            try
            {
                provider.AddSamples(outputData, 0, outputData.Length);
            }
            catch (Exception ex)
            {
                Log.Info("缓冲写入失败: " + ex.Message);
            }
        }
    }

    static byte[] MonoToStereo(ReadOnlySpan<byte> data)
    {
        var frames = data.Length / 2;
        var stereo = new byte[frames * 4];
        for (var i = 0; i < frames; i++)
        {
            var source = i * 2;
            var target = i * 4;
            stereo[target] = stereo[target + 2] = data[source];
            stereo[target + 1] = stereo[target + 3] = data[source + 1];
        }
        return stereo;
    }

    void UpdateLevel(ReadOnlySpan<byte> data)
    {
        var now = DateTime.UtcNow;
        if ((now - lastLevelUtc).TotalMilliseconds < 100) return;
        lastLevelUtc = now;
        double sum = 0;
        var count = 0;
        for (var i = 0; i + 1 < data.Length; i += 96 * channels)
        {
            var sample = (short)(data[i] | (data[i + 1] << 8));
            sum += Math.Abs(sample);
            count++;
        }
        if (count == 0) return;
        var level = (float)Math.Min(1.0, sum / count / 32768.0 * 8.0);
        OnLevel?.Invoke(level);
    }

    void EnsureOutput()
    {
        if ((DateTime.UtcNow - lastOutputTry).TotalSeconds < 3) return;
        bool ok;
        lock (gate)
        {
            ok = output is { PlaybackState: PlaybackState.Playing } && deviceName != null;
        }
        if (!ok) RebuildOutput();
    }

    void RebuildOutput()
    {
        if (Interlocked.Exchange(ref rebuildingOutput, 1) != 0) return;
        try { RebuildOutputCore(); }
        finally { Volatile.Write(ref rebuildingOutput, 0); }
    }

    void RebuildOutputCore()
    {
        lastOutputTry = DateTime.UtcNow;
        TeardownOutput();
        var dev = FindCableDevice();
        if (dev == null)
        {
            if ((DateTime.UtcNow - lastNoDeviceLog).TotalSeconds > 30)
            {
                lastNoDeviceLog = DateTime.UtcNow;
                Log.Info("⚠️ 未找到 VB-CABLE CABLE Input，请安装/重启 VB-CABLE；不会改用物理扬声器");
            }
            return;
        }

        WasapiOut? outp = null;
        try
        {
            var prov = new BufferedWaveProvider(new WaveFormat(rate, 16, 2))
            {
                BufferDuration = TimeSpan.FromSeconds(1.5),
                DiscardOnBufferOverflow = true,
                ReadFully = false
            };
            outp = new WasapiOut(dev, AudioClientShareMode.Shared, false, 120);
            outp.Init(prov);
            outp.Play();
            lock (gate)
            {
                provider = prov;
                output = outp;
                outputDevice = dev;
                deviceName = dev.FriendlyName;
            }
            Log.Info($"✅ 音频输出已就绪: {dev.FriendlyName}");
            Log.Info($"诊断: 输出格式 = {outp.OutputWaveFormat}");
            outp = null; // ownership transferred to the active pipeline
            dev = null; // keep the device wrapper with the active pipeline
        }
        catch (Exception ex)
        {
            Log.Info("❌ 打开 VB-CABLE 失败: " + ex.Message);
            try { outp?.Dispose(); } catch { }
        }
        finally
        {
            try { dev?.Dispose(); } catch { }
        }
    }

    void TeardownOutput()
    {
        lock (gate)
        {
            try { output?.Stop(); } catch { }
            try { output?.Dispose(); } catch { }
            try { outputDevice?.Dispose(); } catch { }
            output = null;
            outputDevice = null;
            provider = null;
            deviceName = null;
        }
    }

    static MMDevice? FindCableDevice()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                if (device.FriendlyName.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase))
                    return device;
                device.Dispose();
            }
        }
        catch { }
        return null;
    }

    public void PlayTestTone()
    {
        try
        {
            EnsureOutput();
            BufferedWaveProvider? prov;
            lock (gate) prov = provider;
            if (prov == null)
            {
                Log.Info("❌ 输出未就绪（未找到 CABLE Input？），无法播放测试音");
                return;
            }

            const int seconds = 1;
            var totalFrames = rate * seconds;
            var pcm = new byte[totalFrames * 2 * 2];
            for (var frame = 0; frame < totalFrames; frame++)
            {
                var t = (double)frame / rate;
                var sample = (short)(Math.Sin(2 * Math.PI * 440 * t) * 32767 * 0.4);
                var offset = frame * 4;
                pcm[offset] = pcm[offset + 2] = (byte)(sample & 0xff);
                pcm[offset + 1] = pcm[offset + 3] = (byte)(sample >> 8);
            }
            lock (gate)
            {
                prov.ClearBuffer();
                prov.AddSamples(pcm, 0, pcm.Length);
            }
            Log.Info("🔔 已发送 440Hz 测试音 → 请查看 Windows 设置中 CABLE Output 的输入电平");
        }
        catch (Exception ex)
        {
            Log.Info("测试音播放失败: " + ex.Message);
        }
    }

    void WatchdogTick()
    {
        var streaming = Streaming;
        OnState?.Invoke(running, streaming, streaming ? $"{rate} Hz · {channels} ch" : "");
        if (!streaming) OnLevel?.Invoke(0);

        using (var currentCable = FindCableDevice())
            CableInstalledNow = currentCable != null;

        if (streaming)
        {
            bool dead;
            lock (gate)
            {
                dead = output == null || output.PlaybackState != PlaybackState.Playing;
            }
            if (dead)
            {
                Log.Info("⚠️ 音频输出异常停止，正在重建…");
                RebuildOutput();
            }
            else if ((DateTime.UtcNow - lastDiagLog).TotalSeconds > 10)
            {
                lastDiagLog = DateTime.UtcNow;
                lock (gate)
                {
                    if (output != null && provider != null)
                        Log.Info($"诊断: 缓冲 {provider.BufferedDuration.TotalMilliseconds:F0}ms，播放状态 {output.PlaybackState}");
                }
            }
        }
    }

    public void Dispose() => Stop();
}
