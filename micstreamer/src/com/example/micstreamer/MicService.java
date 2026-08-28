package com.example.micstreamer;

import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.Service;
import android.content.Intent;
import android.content.pm.ServiceInfo;
import android.media.AudioFormat;
import android.media.AudioRecord;
import android.media.MediaRecorder;
import android.os.Build;
import android.os.IBinder;
import android.os.PowerManager;
import android.util.Log;

import java.util.Arrays;
import java.util.concurrent.CopyOnWriteArrayList;

/**
 * MicStreamer v1.2.0 —— 服务器模式：
 * 在显示器上监听 TCP 50010 端口，任何设备（Mac / Windows / 多台同时）
 * 连进来即推送麦克风 PCM 流。协议：连接后先发一行 "PCM <rate> <channels> <bits>\n"，
 * 之后是小端 PCM16 交错帧，直到断开。
 *
 * 没有客户端连接时释放麦克风（不占用）；显示器重启后由 BootReceiver 自动拉起。
 */
public class MicService extends Service {
    private static final String TAG = "MicStreamer";
    public static final int DEFAULT_PORT = 50010;

    /** The monitor's native far-field array mask: FL|FR|TOP_LEFT|TOP_RIGHT */
    private static final int MASK_4CH = 0x60000c;

    volatile boolean running = false;
    private Thread captureThread;
    private Thread acceptThread;
    private java.net.ServerSocket serverSocket;
    private PowerManager.WakeLock wakeLock;

    /** 当前连接的客户端 */
    final CopyOnWriteArrayList<ClientHandler> clients = new CopyOnWriteArrayList<>();

    /** 实际流参数（采集线程打开麦克风后写入） */
    volatile int activeRate = 48000;
    volatile int activeChannels = 2;

    @Override
    public IBinder onBind(Intent intent) {
        return null;
    }

    @Override
    public int onStartCommand(Intent intent, int flags, int startId) {
        running = true;
        startForegroundNotif();
        if (acceptThread == null || !acceptThread.isAlive()) {
            acceptThread = new Thread(() -> acceptLoop(), "mic-accept");
            acceptThread.start();
        }
        Log.i(TAG, "MicStreamer v1.2.0 server started on port " + DEFAULT_PORT);
        return START_STICKY;
    }

    // MARK: - 前台服务

    private void startForegroundNotif() {
        String chId = "mic";
        NotificationManager nm = getSystemService(NotificationManager.class);
        if (Build.VERSION.SDK_INT >= 26) {
            nm.createNotificationChannel(new NotificationChannel(
                    chId, "MicStreamer", NotificationManager.IMPORTANCE_MIN));
        }
        Notification n;
        if (Build.VERSION.SDK_INT >= 26) {
            n = new Notification.Builder(this, chId)
                    .setContentTitle("MicStreamer")
                    .setContentText("mic server on :" + DEFAULT_PORT)
                    .setSmallIcon(android.R.drawable.ic_btn_speak_now)
                    .build();
        } else {
            n = new Notification.Builder(this)
                    .setContentTitle("MicStreamer")
                    .setContentText("mic server on :" + DEFAULT_PORT)
                    .setSmallIcon(android.R.drawable.ic_btn_speak_now)
                    .build();
        }
        if (Build.VERSION.SDK_INT >= 29) {
            startForeground(1, n, ServiceInfo.FOREGROUND_SERVICE_TYPE_MICROPHONE);
        } else {
            startForeground(1, n);
        }
        PowerManager pm = (PowerManager) getSystemService(POWER_SERVICE);
        wakeLock = pm.newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "micstreamer:srv");
        wakeLock.acquire();
    }

    // MARK: - 接入循环

    private void acceptLoop() {
        try {
            serverSocket = new java.net.ServerSocket(DEFAULT_PORT);
        } catch (Exception e) {
            Log.e(TAG, "cannot bind port " + DEFAULT_PORT, e);
            return;
        }
        while (running) {
            try {
                java.net.Socket s = serverSocket.accept();
                s.setTcpNoDelay(true);
                ClientHandler h = new ClientHandler(s, this);
                clients.add(h);
                h.start();
                Log.i(TAG, "client connected: " + s.getRemoteSocketAddress()
                        + " (total " + clients.size() + ")");
                ensureCapture();
            } catch (Exception e) {
                if (running) Log.w(TAG, "accept error: " + e);
            }
        }
    }

    private synchronized void ensureCapture() {
        if (captureThread == null || !captureThread.isAlive()) {
            captureThread = new Thread(() -> captureLoop(), "mic-capture");
            captureThread.start();
        }
    }

    // MARK: - 采集线程（有客户端时才占用麦克风，向所有客户端广播）

    private void captureLoop() {
        while (running && !clients.isEmpty()) {
            try {
                captureOnce();
            } catch (Exception e) {
                Log.e(TAG, "capture error, retry in 2s", e);
                try {
                    Thread.sleep(2000);
                } catch (InterruptedException ignored) {
                }
            }
        }
        Log.i(TAG, "no clients, capture stopped");
    }

    private void captureOnce() throws Exception {
        int rate = 48000;
        // 远场阵列：必须保持 com.xiaomi.wakeupservice（小爱）启用，阵列才会出音；
        // 单独用 MIC 立体声即可拿到底层阵列信号（AudioFlinger 会把阵列 4ch 下混成立体声）。
        int mask = AudioFormat.CHANNEL_IN_STEREO;
        int min = AudioRecord.getMinBufferSize(rate, mask, AudioFormat.ENCODING_PCM_16BIT);
        if (min <= 0) min = 8192;
        AudioRecord rec = openMic(MediaRecorder.AudioSource.MIC, rate, mask, min * 2);
        if (rec == null) {
            // 立体声打不开时回退单声道
            mask = AudioFormat.CHANNEL_IN_MONO;
            min = AudioRecord.getMinBufferSize(rate, mask, AudioFormat.ENCODING_PCM_16BIT);
            if (min <= 0) min = 4096;
            rec = openMic(MediaRecorder.AudioSource.MIC, rate, mask, min * 2);
            if (rec == null) throw new IllegalStateException("cannot open mic");
        }
        int srcCh = rec.getChannelCount();
        activeRate = rate;
        activeChannels = 2; // 服务器端统一转成立体声推给客户端，客户端无需感知阵列通道数
        Log.i(TAG, "mic opened: src=MIC rate=" + rate + " srcCh=" + srcCh + " -> stereo");

        rec.startRecording();
        byte[] buf = new byte[Math.max(min * 2, 6144)];
        try {
            while (running && !clients.isEmpty()) {
                int n = rec.read(buf, 0, buf.length);
                if (n > 0) {
                    byte[] chunk = toStereo(buf, n, srcCh); // 各客户端队列共享同一不可变引用
                    for (ClientHandler c : clients) c.offer(chunk);
                } else if (n < 0) {
                    throw new IllegalStateException("AudioRecord.read error " + n);
                }
            }
        } finally {
            try {
                rec.stop();
            } catch (Exception ignored) {
            }
            rec.release();
        }
    }

    private static AudioRecord openMic(int source, int rate, int mask, int bufBytes) {
        try {
            AudioRecord r = new AudioRecord(source, rate, mask, AudioFormat.ENCODING_PCM_16BIT, bufBytes);
            if (r.getState() == AudioRecord.STATE_INITIALIZED) return r;
            r.release();
        } catch (Throwable ignored) {
        }
        return null;
    }

    // MARK: - 通道转换工具

    /** 任意通道数 → 16bit 立体声。2ch 原样；1ch 复制；4ch 取 (FL,FR)+(TOP,TOP) 折半。 */
    private static byte[] toStereo(byte[] in, int len, int srcCh) {
        if (srcCh == 2) return Arrays.copyOf(in, len);
        int frames = len / (srcCh * 2);
        byte[] out = new byte[frames * 4];
        int o = 0;
        for (int f = 0; f < frames; f++) {
            int base = f * srcCh * 2;
            int l, r;
            if (srcCh == 1) {
                short s = le16(in, base);
                l = r = s;
            } else { // 4ch: FL,FR,TOP_L,TOP_R → L=(FL+TOP_L)/2, R=(FR+TOP_R)/2
                short fl = le16(in, base), fr = le16(in, base + 2),
                        tl = le16(in, base + 4), tr = le16(in, base + 6);
                l = (fl + tl) / 2;
                r = (fr + tr) / 2;
            }
            out[o++] = (byte) (l & 0xff);
            out[o++] = (byte) ((l >> 8) & 0xff);
            out[o++] = (byte) (r & 0xff);
            out[o++] = (byte) ((r >> 8) & 0xff);
        }
        return out;
    }

    private static short le16(byte[] b, int off) {
        return (short) ((b[off] & 0xff) | (b[off + 1] << 8));
    }

    @Override
    public void onDestroy() {
        running = false;
        try {
            if (serverSocket != null) serverSocket.close();
        } catch (Exception ignored) {
        }
        for (ClientHandler c : clients) c.close();
        if (captureThread != null) captureThread.interrupt();
        if (wakeLock != null && wakeLock.isHeld()) wakeLock.release();
        super.onDestroy();
    }
}
