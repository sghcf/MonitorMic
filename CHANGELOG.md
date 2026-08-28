# Changelog

## 2.0.0 - 2026-08-28

- 重构 macOS 客户端为“单显示器、单接收器”模型。
- 保持 Android 服务器多客户端广播，Mac 与 Windows 可同时独立连接。
- 使用一个 BlackHole 专用音频引擎，避免音频误播到系统扬声器。
- 增强 TCP 断线重连、PCM 头校验、缓冲限长和音频引擎自愈。
- 配置迁移到 `~/Library/Application Support/MonitorMic/config.json`。
- 日志迁移到 `~/Library/Logs/MonitorMic/monitor-mic.log`。
- 增加 GitHub 友好的忽略规则、构建说明和版本号文件。

## 1.2.1

- Android MicStreamer 支持多个客户端同时接收。
- Windows 客户端使用服务器模式连接显示器。
