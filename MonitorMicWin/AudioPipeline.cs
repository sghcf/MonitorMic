using System.Net.Sockets;
using System.Text;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MonitorMicWin;

/// <summary>
/// 音频管线（v1.2.0 客户端模式）：主动连接显示器 MicStreamer 服务器 (TCP 50010)，
/// 解析 PCM 头 → 抖动缓冲 → WASAPI → VB-CABLE。断线 2s 自动重连。
/// 自愈逻辑：输出设备丢失/出现自动重建；播放停滞自动重建；支持测试音诊断。
/// </summary>
sealed class AudioPipeline : IDisposable
{
    public const int Port = 50010;

    /// <summary>receiverRunning, streaming, info</summary>
    public event Action<bool, bool, string>? OnState;
    /// <summary>0..1 电平</summary>
    public event Action<float>? OnLevel;

    readonly object gate = new();
    volatile bool running;
    Thread? connThread;

    // 流参数（来自 PCM 头）
    int rate = 48000, channels = 2;
    volatile bool headerParsed;
    DateTime lastDataUtc = DateTime.MinValue;
    DateTime lastLevelUtc = DateTime.MinValue;

    // 输出
    WasapiOut? output;
    BufferedWaveProvider? provider;
    string? deviceName;
    DateTime lastOutputTry = DateTime.MinValue;
    DateTime lastNoDeviceLog = DateTime.MinValue;
    DateTime lastDiagLog = DateTime.MinValue;

    System.Threading.Timer? watchdog;

    public bool Running => running;
    public bool Streaming => headerParsed && (DateTime.UtcNow - lastDataUtc).TotalSeconds < 2.5;
    public string StreamInfo => headerParsed ? $"{rate} Hz · {channels} ch" : "";
    public string? DeviceName => deviceName;
    /// <summary>由看门狗后台线程刷新，UI 只读缓存（COM 枚举不能上 100ms UI 定时器）。</summary>
    public volatile bool CableInstalledNow;

    public void Start(string host, int port = Port)
    {
        Stop();
        running = true;
        connThread = new Thread(() => ConnLoop(host, port)) { IsBackground = true, Name = "tcp-conn" };
        connThread.Start();
        watchdog = new System.Threading.Timer(_ => WatchdogTick(), null, 2000, 2000);
        Log.Info($"音频接收器已启动，连接 {host}:{port}（断线自动重连）");
        OnState?.Invoke(true, false, "");
    }

    public void Stop()
    {
        running = false;
        watchdog?.Dispose(); watchdog = null;
        TeardownOutput();
        OnState?.Invoke(false, false, "");
        OnLevel?.Invoke(0);
    }

    // MARK: - TCP 客户端

    void ConnLoop(string host, int port)
    {
        while (running)
        {
            try
            {
                using var client = new TcpClient { ReceiveTimeout = 10000, ReceiveBufferSize = 65536 };
                var ar = client.BeginConnect(host, port, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(5000))
                    throw new TimeoutException("连接超时");
                client.EndConnect(ar);
                Log.Info($"已连接显示器 {host}:{port}");
                headerParsed = false;
                lastDataUtc = DateTime.MinValue;
                ReadLoop(client);
            }
            catch (Exception ex)
            {
                if (running)
                {
                    Log.Info($"连接断开/失败: {ex.Message}");
                    OnState?.Invoke(true, false, "");
                }
            }
            headerParsed = false;
            if (running)
            {
                Log.Info("2s 后重连…");
                Thread.Sleep(2000);
            }
        }
    }

    void ReadLoop(TcpClient client)
    {
        var stream = client.GetStream();
        var headerBuf = new MemoryStream();
        var buf = new byte[65536];
        while (running)
        {
            int n;
            try { n = stream.Read(buf, 0, buf.Length); }
            catch (IOException) // 10s 读超时
            {
                if ((DateTime.UtcNow - lastDataUtc).TotalSeconds > 10)
                    throw new Exception("超过 10s 无数据");
                continue;
            }
            if (n <= 0) throw new Exception("连接被关闭");

            if (headerParsed)
            {
                IngestPcm(buf, n);
            }
            else
            {
                headerBuf.Write(buf, 0, n);
                var all = headerBuf.GetBuffer();
                int total = (int)headerBuf.Length;
                int nl = Array.IndexOf(all, (byte)'\n', 0, total);
                if (nl >= 0 && TryParseHeader(all, nl))
                {
                    int rest = total - nl - 1;
                    if (rest > 0)
                    {
                        var restBuf = new byte[rest];
                        Array.Copy(all, nl + 1, restBuf, 0, rest);
                        IngestPcm(restBuf, rest);
                    }
                }
                else if (total > 256)
                {
                    headerBuf.SetLength(0); // 防垃圾数据
                }
            }
        }
    }

    bool TryParseHeader(byte[] lineBytes, int len)
    {
        var line = Encoding.ASCII.GetString(lineBytes, 0, len).Trim();
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 4 && parts[0] == "PCM"
            && int.TryParse(parts[1], out var r) && int.TryParse(parts[2], out var ch))
        {
            rate = r; channels = ch; headerParsed = true;
            lastDataUtc = DateTime.UtcNow;
            Log.Info($"流参数: {r} Hz / {ch} 声道 / {parts[3]} bit");
            OnState?.Invoke(true, true, $"{r} Hz · {ch} ch");
            return true;
        }
        return false;
    }

    // MARK: - PCM → VB-CABLE

    void IngestPcm(byte[] data, int len)
    {
        lastDataUtc = DateTime.UtcNow;
        UpdateLevel(data, len);
        EnsureOutput();
        lock (gate)
        {
            if (provider == null) return;
            // 缓冲积压超过 1.2s 说明消费跟不上，清空追实时（防延迟越攒越大）
            if (provider.BufferedDuration.TotalSeconds > 1.2)
                provider.ClearBuffer();
            try { provider.AddSamples(data, 0, len); }
            catch (Exception ex) { Log.Info("缓冲写入失败: " + ex.Message); }
        }
    }

    void UpdateLevel(byte[] data, int len)
    {
        var now = DateTime.UtcNow;
        if ((now - lastLevelUtc).TotalMilliseconds < 100) return;
        lastLevelUtc = now;
        double sum = 0; int n = 0;
        for (int i = 0; i + 1 < len; i += 96 * channels) // 抽样
        {
            short s = (short)(data[i] | (data[i + 1] << 8));
            sum += Math.Abs(s); n++;
        }
        if (n == 0) return;
        var level = (float)Math.Min(1.0, sum / n / 32768.0 * 8.0);
        OnLevel?.Invoke(level);
    }

    /// <summary>确保输出设备在线且正在播放（带 3s 防抖）。首次打开不告警。</summary>
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
        lastOutputTry = DateTime.UtcNow;
        TeardownOutput();
        var dev = FindCableDevice();
        if (dev == null)
        {
            if ((DateTime.UtcNow - lastNoDeviceLog).TotalSeconds > 30)
            {
                lastNoDeviceLog = DateTime.UtcNow;
                Log.Info("⚠️ 未找到 VB-CABLE 虚拟声卡，请点击「安装 VB-CABLE」按钮");
            }
            return;
        }
        try
        {
            var prov = new BufferedWaveProvider(new WaveFormat(rate, 16, channels))
            {
                BufferDuration = TimeSpan.FromSeconds(2),
                DiscardOnBufferOverflow = false,
                ReadFully = false
            };
            var outp = new WasapiOut(dev, AudioClientShareMode.Shared, false, 120);
            outp.Init(prov);
            outp.Play();
            lock (gate)
            {
                provider = prov;
                output = outp;
                deviceName = dev.FriendlyName;
            }
            Log.Info($"✅ 音频输出已就绪: {dev.FriendlyName}");
            Log.Info($"诊断: 输出格式 = {outp.OutputWaveFormat}");
        }
        catch (Exception ex)
        {
            Log.Info("❌ 打开 VB-CABLE 失败: " + ex.Message);
            dev.Dispose();
        }
    }

    void TeardownOutput()
    {
        lock (gate)
        {
            try { output?.Stop(); } catch { }
            try { output?.Dispose(); } catch { }
            output = null;
            provider = null;
            deviceName = null;
        }
    }

    static MMDevice? FindCableDevice()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var d in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                // VB-CABLE 的播放端名为 "CABLE Input (VB-Audio Virtual Cable)"
                if (d.FriendlyName.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase))
                    return d;
                d.Dispose();
            }
        }
        catch { }
        return null;
    }

    /// <summary>播放 1.5s 440Hz 测试音 → CABLE Input，用于验证虚拟声卡链路。</summary>
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
            int seconds = 1, sr = rate, ch = channels;
            int total = sr * seconds * ch;
            var pcm = new byte[total * 2];
            for (int i = 0; i < total; i++)
            {
                double t = (double)(i / ch) / sr;
                short v = (short)(Math.Sin(2 * Math.PI * 440 * t) * 32767 * 0.4);
                pcm[i * 2] = (byte)(v & 0xff);
                pcm[i * 2 + 1] = (byte)(v >> 8);
            }
            lock (gate)
            {
                prov.ClearBuffer();
                prov.AddSamples(pcm, 0, pcm.Length);
            }
            Log.Info("🔔 已发送 440Hz 测试音 → 请查看 Windows 设置里「CABLE Output」麦克风电平是否有反应");
        }
        catch (Exception ex)
        {
            Log.Info("测试音播放失败: " + ex.Message);
        }
    }

    // MARK: - 看门狗（后台线程）：输出停滞自愈 + 设备缓存刷新 + 周期诊断

    int tickCount;

    void WatchdogTick()
    {
        var streaming = Streaming;
        OnState?.Invoke(running, streaming, streaming ? $"{rate} Hz · {channels} ch" : "");
        if (!streaming) { OnLevel?.Invoke(0); }

        // 每 2s 在后台线程刷新设备存在性缓存（COM 枚举放 UI 线程会卡）
        CableInstalledNow = FindCableDevice() != null;

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
        tickCount++;
    }

    public void Dispose() => Stop();
}
