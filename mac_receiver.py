#!/usr/bin/env python3
"""Mac 端接收程序：接收红米显示器的 PCM 流并写入 BlackHole 虚拟麦克风。

显示器端 (MicStreamer App)                    Mac 端 (本脚本)
AudioRecord 48kHz/16bit/2ch  ──TCP:50010──>  本脚本  ──>  BlackHole 2ch 输出
                                                       ↓
                                              macOS 声音输入选 "BlackHole 2ch"
                                              微信/Zoom/Discord 即可使用显示器麦克风

用法:  python3 mac_receiver.py [port]
"""
import socket
import struct
import sys
import threading
import time
import queue

import numpy as np
import sounddevice as sd

PORT = int(sys.argv[1]) if len(sys.argv) > 1 else 50010
DEVICE_NAME_HINT = "BlackHole"   # 输出设备名包含此字符串即被选用


def find_blackhole():
    for i, d in enumerate(sd.query_devices()):
        if DEVICE_NAME_HINT.lower() in d["name"].lower() and d["max_output_channels"] >= 2:
            return i
    return None


def parse_header(conn_file):
    line = conn_file.readline().strip().decode("ascii", "replace")
    # 形如: PCM 48000 2 16
    parts = line.split()
    if len(parts) == 4 and parts[0] == "PCM":
        return int(parts[1]), int(parts[2]), int(parts[3])
    raise ValueError(f"bad header: {line!r}")


def main():
    out_idx = find_blackhole()
    if out_idx is None:
        print("❌ 未找到 BlackHole 设备。请先安装 BlackHole 2ch 并重启核心音频。")
        print("   当前可用输出设备:")
        for i, d in enumerate(sd.query_devices()):
            if d["max_output_channels"] > 0:
                print(f"   [{i}] {d['name']}")
        sys.exit(1)
    print(f"✅ 虚拟麦克风输出设备: [{out_idx}] {sd.query_devices(out_idx)['name']}")

    srv = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    srv.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    srv.bind(("0.0.0.0", PORT))
    srv.listen(1)
    print(f"🎧 监听 0.0.0.0:{PORT}，等待显示器连接…")

    # 音频队列: 网络线程 → 音频回调
    q = queue.Queue(maxsize=200)

    while True:  # 断线重连主循环
        conn, addr = srv.accept()
        conn.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
        print(f"📱 显示器已连接: {addr}")
        f = conn.makefile("rb")
        try:
            rate, ch, bits = parse_header(f)
            print(f"🎙 流参数: {rate} Hz, {ch} 声道, {bits} bit")
        except Exception as e:
            print("⚠️ 头部解析失败:", e)
            conn.close()
            continue

        stop = threading.Event()

        def reader():
            try:
                while not stop.is_set():
                    data = f.read(2048)
                    if not data:
                        break
                    try:
                        q.put(data, timeout=1)
                    except queue.Full:
                        pass  # 音频消费不过来就丢帧，保证实时性
            except Exception:
                pass
            finally:
                stop.set()

        rt = threading.Thread(target=reader, daemon=True)
        rt.start()

        # 抖动缓冲：先攒够约 150ms 的音频再开始播放，消除周期性断音
        print("⏳ 缓冲中…")
        t0 = time.time()
        while q.qsize() < 35 and not stop.is_set() and time.time() - t0 < 5:
            time.sleep(0.01)

        def callback(outdata, frames, time_info, status):
            need = frames * 2  # 输出为双声道
            buf = bytearray()
            while len(buf) < need * 2:
                try:
                    buf += q.get_nowait()
                except queue.Empty:
                    break
            pcm = np.frombuffer(bytes(buf), dtype=np.int16)
            if ch == 2:
                pcm = pcm[: len(pcm) // 2 * 2]
                stereo = pcm.reshape(-1, 2).astype(np.float32) / 32768.0
            else:  # 4ch: 取前两路 / 或下混
                pcm = pcm[: len(pcm) // ch * ch]
                multi = pcm.reshape(-1, ch).astype(np.float32) / 32768.0
                stereo = np.stack([multi[:, 0], multi[:, 1] if ch > 1 else multi[:, 0]], axis=1)
            out = np.zeros((frames, 2), dtype=np.float32)
            n = min(frames, len(stereo))
            out[:n] = stereo[:n]
            outdata[:] = out

        try:
            with sd.OutputStream(device=out_idx, samplerate=rate, channels=2,
                                 dtype="float32", blocksize=1024, callback=callback):
                print("🔊 虚拟麦克风已激活。macOS 输入选择 BlackHole 即可。")
                while not stop.is_set():
                    stop.wait(0.5)
        except Exception as e:
            print("⚠️ 音频输出错误:", e)
        finally:
            stop.set()
            conn.close()
            while not q.empty():
                try:
                    q.get_nowait()
                except queue.Empty:
                    break
            print("🔌 连接断开，等待重连…")


if __name__ == "__main__":
    main()
