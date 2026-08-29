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
    Task? connectionTask;
    CancellationTokenSource? connectionCts;
    TcpClient? activeClient;

    int rate = 48000;
    int channels = 2;
    float outputGain = PcmGain.Default;
    volatile bool headerParsed;
    DateTime lastDataUtc = DateTime.MinValue;
    DateTime lastLevelUtc = DateTime.MinValue;

    WasapiOut? output;
    MMDevice? outputDevice;
    BufferedWaveProvider? provider;
    string? deviceName;
    string? outputDeviceId;
    DateTime lastOutputTry = DateTime.MinValue;
    DateTime lastNoDeviceLog = DateTime.MinValue;
    DateTime lastOutputStoppedLog = DateTime.MinValue;
    string? lastObservedCableId;
    int playbackStoppedObserved;
    int rebuildingOutput;
    System.Threading.Timer? watchdog;

    // 48,000 Hz × 2 channels × 16-bit = 192,000 bytes/sec. Keep at most 0.5 sec;
    // normal operation is kept below 0.17 sec to minimize microphone latency.
    public const int MaxBufferedBytes = 96_000;
    public const int HighWaterBytes = 32_000;
    public const int OutputLatencyMilliseconds = 40;

    public bool Running => running;
    public bool Streaming => headerParsed && (DateTime.UtcNow - lastDataUtc).TotalSeconds < 2.5;
    public string StreamInfo => headerParsed ? $"{rate} Hz · {channels} ch" : "";
    public string? DeviceName => deviceName;
    public volatile bool CableInstalledNow;
    public float OutputGain
    {
        get => Volatile.Read(ref outputGain);
        set
        {
            var clamped = PcmGain.Clamp(value);
            Volatile.Write(ref outputGain, clamped);
            Log.Info($"输出增益已设置为 {clamped:0.#}×（峰值限幅）");
        }
    }

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
        connectionTask = ConnectLoopAsync(host, port, cts.Token);
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

        var task = Interlocked.Exchange(ref connectionTask, null);
        if (task != null && task.Id != Task.CurrentId && !task.IsCompleted)
        {
            try { task.Wait(1000); } catch { }
        }
        cts?.Dispose();
        watchdog?.Dispose();
        watchdog = null;
        TeardownOutput();
        headerParsed = false;
        OnState?.Invoke(false, false, "");
        OnLevel?.Invoke(0);
    }

    async Task ConnectLoopAsync(string host, int port, CancellationToken cancellationToken)
    {
        while (running && !cancellationToken.IsCancellationRequested)
        {
            TcpClient? client = null;
            try
            {
                client = new TcpClient
                {
                    ReceiveBufferSize = 65536,
                    NoDelay = true
                };
                Interlocked.Exchange(ref activeClient, client);
                await client.ConnectAsync(host, port, cancellationToken).AsTask()
                    .WaitAsync(TimeSpan.FromSeconds(5), cancellationToken)
                    .ConfigureAwait(false);
                Log.Info($"已连接显示器 {host}:{port}");
                headerParsed = false;
                lastDataUtc = DateTime.MinValue;
                await ReadLoopAsync(client, cancellationToken).ConfigureAwait(false);
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
                try { client?.Dispose(); } catch { }
                headerParsed = false;
                if (running && !cancellationToken.IsCancellationRequested)
                {
                    ClearQueuedPcm("网络断开");
                    OnState?.Invoke(true, false, "");
                }
            }

            if (running && !cancellationToken.IsCancellationRequested)
            {
                Log.Info("2s 后重连…");
                try { await Task.Delay(2000, cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            }
        }
    }

    async Task ReadLoopAsync(TcpClient client, CancellationToken cancellationToken)
    {
        var stream = client.GetStream();
        var parser = new PcmStreamParser();
        var buf = new byte[65536];
        while (running && !cancellationToken.IsCancellationRequested)
        {
            int n;
            try
            {
                n = await stream.ReadAsync(buf.AsMemory(), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
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
        if (format.SampleRate != 48000)
            throw new PcmStreamProtocolException($"不支持的采样率 {format.SampleRate} Hz，当前只接受 48000 Hz PCM16");

        if (headerParsed && rate == format.SampleRate && channels == format.Channels) return;
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
        PcmGain.ApplyInPlace(outputData, OutputGain);
        lock (gate)
        {
            if (provider == null) return;
            // Drop old queued audio at a high-water mark so latency cannot grow.
            if (provider.BufferedBytes > HighWaterBytes)
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

    void ClearQueuedPcm(string reason)
    {
        lock (gate)
        {
            provider?.ClearBuffer();
        }
        Log.Info($"音频队列已清空（{reason}）");
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
            // PlaybackState is checked by the watchdog only after the stopped
            // event/device identity has been observed. Do not rebuild a healthy
            // output just because a transient state sample is not Playing.
            ok = output != null && provider != null && deviceName != null;
        }
        if (!ok) RebuildOutput("输出未就绪");
    }

    void RebuildOutput(string reason)
    {
        if (Interlocked.Exchange(ref rebuildingOutput, 1) != 0) return;
        try
        {
            if ((DateTime.UtcNow - lastOutputTry).TotalSeconds < 3) return;
            RebuildOutputCore(reason);
        }
        finally { Volatile.Write(ref rebuildingOutput, 0); }
    }

    void RebuildOutputCore(string reason)
    {
        lastOutputTry = DateTime.UtcNow;
        var dev = FindCableDevice();
        if (dev == null)
        {
            if ((DateTime.UtcNow - lastNoDeviceLog).TotalSeconds > 30)
            {
                lastNoDeviceLog = DateTime.UtcNow;
                Log.Info($"⚠️ 未找到 VB-CABLE CABLE Input（{reason}），保留现有输出；不会改用物理扬声器");
            }
            return;
        }

        WasapiOut? outp = null;
        try
        {
            // Only replace an output after a concrete candidate has been found.
            // A transient empty enumeration must not destroy a still usable output.
            TeardownOutput();
            var prov = new BufferedWaveProvider(new WaveFormat(rate, 16, 2))
            {
                BufferLength = MaxBufferedBytes,
                DiscardOnBufferOverflow = true,
                // Keep the WASAPI client alive while the network jitter buffer is
                // temporarily empty. With false, WasapiOut can stop reading before
                // the first PCM block arrives and leave a misleading Playing state.
                ReadFully = true
            };
            outp = new WasapiOut(dev, AudioClientShareMode.Shared, false, OutputLatencyMilliseconds);
            outp.PlaybackStopped += OutputPlaybackStopped;
            outp.Init(prov);
            outp.Play();
            lock (gate)
            {
                provider = prov;
                output = outp;
                outputDevice = dev;
                deviceName = dev.FriendlyName;
                outputDeviceId = dev.ID;
                Volatile.Write(ref playbackStoppedObserved, 0);
            }
            Log.Info($"✅ 音频输出已就绪: {dev.FriendlyName}");
            Log.Info($"诊断: 输出格式 = {outp.OutputWaveFormat}");
            Log.Info($"诊断: 输出增益 = {OutputGain:0.#}×（峰值限幅）");
            outp = null; // ownership transferred to the active pipeline
            dev = null; // keep the device wrapper with the active pipeline
        }
        catch (Exception ex)
        {
            Log.Info("❌ 打开 VB-CABLE 失败: " + ex.Message);
            try { if (outp != null) outp.PlaybackStopped -= OutputPlaybackStopped; } catch { }
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
            try { if (output != null) output.PlaybackStopped -= OutputPlaybackStopped; } catch { }
            try { output?.Stop(); } catch { }
            try { output?.Dispose(); } catch { }
            try { outputDevice?.Dispose(); } catch { }
            output = null;
            outputDevice = null;
            provider = null;
            deviceName = null;
            outputDeviceId = null;
            Volatile.Write(ref playbackStoppedObserved, 0);
        }
    }

    void OutputPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        lock (gate)
        {
            if (!ReferenceEquals(sender, output)) return;
            Volatile.Write(ref playbackStoppedObserved, 1);
        }
        if (running) LogOutputStopped();
    }

    void LogOutputStopped()
    {
        if ((DateTime.UtcNow - lastOutputStoppedLog).TotalSeconds < 30) return;
        lastOutputStoppedLog = DateTime.UtcNow;
        Log.Info("⚠️ 真实音频输出已停止，等待确认后恢复");
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

    public static bool DetectCable()
    {
        using var device = FindCableDevice();
        return device != null;
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

        using var currentCable = FindCableDevice();
        CableInstalledNow = currentCable != null;
        var cableId = currentCable?.ID;
        var cableChanged = cableId != null && lastObservedCableId != null && cableId != lastObservedCableId;
        if (cableId != lastObservedCableId)
        {
            lastObservedCableId = cableId;
            if (cableChanged) Log.Info("⚠️ 输出设备已变化，准备确认后重建");
        }

        if (streaming)
        {
            bool outputMissing;
            bool outputExists;
            bool playbackStopped;
            bool deviceChanged;
            lock (gate)
            {
                outputMissing = output == null || provider == null;
                outputExists = output != null;
                playbackStopped = Volatile.Read(ref playbackStoppedObserved) != 0
                    || output?.PlaybackState == PlaybackState.Stopped;
                deviceChanged = cableId != null && outputDeviceId != null && cableId != outputDeviceId;
            }
            if (deviceChanged)
            {
                RebuildOutput("设备变化");
            }
            else if (playbackStopped)
            {
                LogOutputStopped();
                RebuildOutput("真实输出停止");
            }
            else if (outputMissing && currentCable != null)
            {
                RebuildOutput("输出未就绪");
            }
            else if (currentCable == null && outputExists)
            {
                // Device enumeration can be empty briefly while Windows reloads
                // an endpoint. Keep the existing instance and do not rebuild it.
                if ((DateTime.UtcNow - lastNoDeviceLog).TotalSeconds > 30)
                {
                    lastNoDeviceLog = DateTime.UtcNow;
                    Log.Info("⚠️ VB-CABLE 暂时无法枚举，保留当前输出实例");
                }
            }
        }
    }

    public void Dispose() => Stop();
}
