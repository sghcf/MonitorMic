package com.example.micstreamer;

import android.util.Log;

import java.io.BufferedOutputStream;
import java.io.OutputStream;
import java.net.Socket;
import java.nio.charset.StandardCharsets;
import java.util.concurrent.ArrayBlockingQueue;
import java.util.concurrent.BlockingQueue;
import java.util.concurrent.TimeUnit;

/**
 * 一个已连接的客户端：独立队列 + 写入线程。
 * 客户端慢/掉线只影响自己（队列满丢最旧块），不阻塞采集和其他客户端。
 */
final class ClientHandler {
    private static final String TAG = "MicStreamer";

    private final Socket sock;
    private final MicService svc;
    /** 约 64 * 6KB ≈ 1.3s 立体声缓冲；满则丢最旧块保实时性 */
    private final BlockingQueue<byte[]> queue = new ArrayBlockingQueue<>(64);
    private Thread thread;

    ClientHandler(Socket sock, MicService svc) {
        this.sock = sock;
        this.svc = svc;
    }

    void start() {
        thread = new Thread(() -> writerLoop(), "mic-client-tx");
        thread.start();
    }

    void offer(byte[] chunk) {
        if (!queue.offer(chunk)) {
            queue.poll();            // 丢最旧
            queue.offer(chunk);
        }
    }

    private void writerLoop() {
        OutputStream out = null;
        try {
            out = new BufferedOutputStream(sock.getOutputStream(), 32768);
            String header = "PCM " + svc.activeRate + " " + svc.activeChannels + " 16\n";
            out.write(header.getBytes(StandardCharsets.US_ASCII));
            out.flush();
            queue.clear(); // 头部之后从新数据开始
            while (svc.running && svc.clients.contains(this)) {
                byte[] chunk = queue.poll(1, TimeUnit.SECONDS);
                if (chunk == null) continue; // 超时仅作活性检查
                out.write(chunk);
                out.flush(); // 关键：立即发送，避免缓冲攒成大爆发造成周期断音
            }
        } catch (Exception e) {
            Log.w(TAG, "client " + sock.getRemoteSocketAddress() + " error: " + e);
        } finally {
            svc.clients.remove(this);
            try {
                if (out != null) out.close();
            } catch (Exception ignored) {
            }
            try {
                sock.close();
            } catch (Exception ignored) {
            }
            Log.i(TAG, "client disconnected (total " + svc.clients.size() + ")");
        }
    }

    void close() {
        try {
            sock.close();
        } catch (Exception ignored) {
        }
    }
}
