# MonitorMic

把 Xiaomi/Redmi 显示器的麦克风通过局域网提供给电脑应用使用。

完整技术规格、协议说明、测试计划和 Windows 端开发任务见 [`PROJECT_SPEC_WINDOWS.md`](PROJECT_SPEC_WINDOWS.md)。

## 工作方式

显示器上的 `MicStreamer` 是 TCP 音频服务器，监听 `50010` 端口并广播 PCM 音频。macOS 版和 Windows 版是两个独立客户端：它们各自连接显示器，不互相依赖，也可以同时连接同一台显示器。

macOS 客户端把音频输出到 `BlackHole 2ch`，然后在微信、Zoom、Discord 等应用中选择 `BlackHole 2ch` 作为麦克风。Windows 客户端使用自己的虚拟声卡链路。

## macOS 版

要求：

- macOS 13 或更高版本
- Apple Silicon 或 Intel Mac（构建脚本使用当前机器架构）
- `adb`（Homebrew `android-platform-tools` 或安装包内置版本）
- BlackHole 2ch
- 显示器与 Mac 在同一局域网

构建并运行：

```sh
cd MonitorMic
./build_app.sh
open MonitorMic.app
```

第一次使用点击“一键修复并启动”，输入显示器 IP。程序会通过 ADB 连接显示器、安装/启动 `MicStreamer`，并启动 Mac 接收器。随后在 macOS 声音设置和目标应用中选择 BlackHole 2ch。

如果只需要重新连接网络音频，可以单独启动“Mac 音频接收器”；它会自动重连，输出设备重载或短暂消失时也会自动重建音频引擎。程序日志位于 `~/Library/Logs/MonitorMic/monitor-mic.log`，配置位于 `~/Library/Application Support/MonitorMic/config.json`。

## Android 服务器

Android 源码位于 `micstreamer/`，不依赖 Android Studio：

```sh
cd micstreamer
./build.sh
```

服务协议：连接后先发送一行 `PCM <rate> <channels> <bits>\n`，之后持续发送小端 PCM16。每个客户端有独立发送队列，因此 Mac 和 Windows 可以同时接收；单个客户端断开或变慢不会阻塞其他客户端。

## Windows 版

Windows 源码保留在 `MonitorMicWin/`，使用 .NET 8 和 NAudio。Windows 端请单独运行它并填写同一台显示器 IP；不要让 Windows 端依赖 Mac 端转发。

## 版本管理

当前 macOS 客户端版本在根目录 `VERSION` 中维护，构建时会同步到 `Info.plist`。发布前更新 `VERSION` 和 `CHANGELOG.md`，再构建 DMG：

```sh
cd MonitorMic
./build_app.sh
./make_dmg.sh
```

构建产物、ADB/驱动二进制、APK 输出、签名文件和本地 SDK 已加入 `.gitignore`，源码和构建脚本可以直接提交到 GitHub。

根目录的 `mac_receiver.py` 和 `start_monitor_mic.sh` 是早期“Mac 监听 TCP 端口”的实验脚本，已不再是推荐入口；新版本请使用 `MonitorMic.app`。它们保留在仓库中，便于追溯之前的实验过程。
