#!/bin/bash
# 一键启动：红米显示器麦克风 → Mac 虚拟麦克风
# 用法: ./start_monitor_mic.sh
set -e
export PATH="/opt/homebrew/bin:$PATH"
DIR="$(cd "$(dirname "$0")" && pwd)"

MONITOR_IP="192.168.100.7"
MAC_IP="192.168.100.50"
PORT=50010

echo "📺 连接显示器 $MONITOR_IP ..."
adb connect "$MONITOR_IP:5555" | grep -v "already connected" || true

echo "🎙 启动麦克风串流服务（无界面，不影响 HDMI 画面）..."
adb shell "am start-foreground-service -n com.example.micstreamer/.MicService --es host $MAC_IP --ei port $PORT"

echo "🔊 启动 Mac 接收器 → BlackHole 虚拟麦克风..."
echo "   （macOS 声音设置 → 输入 → 选择 BlackHole 2ch）"
exec "$DIR/.venv/bin/python" "$DIR/mac_receiver.py" "$PORT"
