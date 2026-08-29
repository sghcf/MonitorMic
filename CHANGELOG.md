# Changelog

## 2.0.4 - 2026-08-29

- 统一本次跨平台发布使用 `2.0.4` 公开版本号。
- Android `versionCode` 从 `5` 递增为 `6`，`versionName` 使用根目录 `VERSION` 的 `2.0.4`。
- macOS 和 Android 构建脚本继续从根目录 `VERSION` 读取公开版本号。

## 2.0.3 - 2026-08-29

- 统一 macOS App、DMG 和 Android APK 使用根目录 `VERSION` 作为公开版本号来源。
- Android `versionCode` 递增为 `5`，`versionName` 为 `2.0.3`。
- 移除构建脚本和 AndroidManifest 中遗留的 `1.2.1` 版本写死值。

## 2.0.2 - 2026-08-28

- 修复音频引擎健康检查误判：不再因 Core Audio 设备列表瞬时变化而每隔几秒重建引擎。
- 复用非标准采样率音频转换器，48 kHz 单声道/双声道 PCM 直接转换为 Float32，降低长期运行的原生内存分配。
- 将 PCM 缓冲区从逐块 `removeFirst` 改为偏移读取并定期压缩，降低长时间串流的复制和内存抖动。
- 停止音频管线时显式销毁输出单元并清空环形缓冲，避免重连或重建时残留音频缓冲区。
- 改用直接 HAL 输出单元和有上限的 Float32 环形缓冲，绕过 AVAudioEngine 反复配置导致的音频引擎停止。
- 增加单实例保护，避免同时运行多个 Mac 客户端争用同一个 BlackHole 输出设备。

## 2.0.1 - 2026-08-28

- 修复 BlackHole 设备枚举短暂变化导致音频引擎被反复误重建的问题。
- macOS 客户端要求音频引擎创建时必须成功绑定 BlackHole，避免误输出到系统扬声器。
- 验证 Mac/Windows 两个独立 TCP 客户端可同时接收显示器 PCM 流。

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
