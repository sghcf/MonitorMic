# MonitorMic

把 Xiaomi/Redmi 显示器的麦克风通过局域网提供给 Mac 或 Windows 电脑使用。

普通用户不需要自己编译源码，直接从 [Releases](https://github.com/sghcf/MonitorMic/releases/latest) 下载对应系统的安装包即可。

## 下载

当前版本：**v2.0.3**

- macOS：`MonitorMic-2.0.3.dmg`
- Windows 安装版：`MonitorMicSetup-2.0.3.exe`
- Windows 便携版：`MonitorMic.exe`
- Android 显示器端服务：`micstreamer.apk`

## macOS 使用方法

1. 下载并打开 `MonitorMic-2.0.3.dmg`，将 `MonitorMic.app` 拖入“应用程序”。
2. 安装并启用 [BlackHole 2ch](https://existential.audio/blackhole/)。
3. 打开 MonitorMic，输入显示器的局域网 IP 地址。
4. 点击“一键修复并启动”。程序会通过 ADB 安装/启动显示器端服务，并连接音频流。
5. 在 macOS 的声音设置和微信、Zoom、Discord 等应用中选择 `BlackHole 2ch` 作为麦克风。

电脑和显示器需要连接到同一个局域网。首次使用时，显示器需要开启 ADB 调试并允许电脑连接。

## Windows 使用方法

1. 下载并运行 `MonitorMicSetup-2.0.3.exe`。
2. 按安装程序提示完成安装；安装包包含 MonitorMic、ADB、显示器端 APK 和 VB-CABLE 相关文件。
3. 打开 MonitorMic，输入显示器的局域网 IP 地址并启动连接。
4. 在微信、Zoom、Discord 等应用的麦克风设置中选择 `CABLE Output (VB-Audio Virtual Cable)`。

Windows 安装版会创建开始菜单和桌面快捷方式。便携版 `MonitorMic.exe` 可以直接运行，但仍需要系统中准备好 VB-CABLE。首次使用时，显示器需要开启 ADB 调试并允许电脑连接。

## 同时使用 Mac 和 Windows

Mac 客户端和 Windows 客户端可以同时连接同一台显示器，互不依赖，也不需要 Mac 作为中转。

```text
显示器 Android 服务
├── Mac 客户端 → BlackHole 2ch
└── Windows 客户端 → VB-CABLE
```

同一台电脑建议只运行一个 MonitorMic 客户端实例。

## Android 显示器端

普通用户不需要单独操作 `micstreamer.apk`，macOS 和 Windows 客户端会在“一键修复”或首次连接时使用它。

如果需要手动安装，下载 Release 中的 `micstreamer.apk`，并按照显示器端的 Android/ADB 操作流程安装。服务使用 TCP `50010` 端口传输 PCM 音频。

## 故障排查

- 确认电脑和显示器在同一个局域网，并填写正确的显示器 IP。
- 确认显示器已开启 ADB 调试，并允许当前电脑连接。
- macOS 确认已安装 `BlackHole 2ch`，Windows 确认已安装 VB-CABLE。
- 目标应用中选择正确的虚拟麦克风：macOS 选择 `BlackHole 2ch`，Windows 选择 `CABLE Output`。
- 关闭同一台电脑上重复运行的 MonitorMic 实例后再重试。

## 开发者信息

源码位于本仓库中。macOS 客户端使用 Swift，Windows 客户端使用 .NET 8，显示器端服务使用 Java。详细技术规格和 Windows 开发记录见 [`PROJECT_SPEC_WINDOWS.md`](PROJECT_SPEC_WINDOWS.md)。

构建脚本仍保留在仓库中，主要用于开发和调试；普通用户请优先使用 Releases 中的安装包。

## 免责声明

本项目是 **100% 纯 AI 生成项目**，仅供学习、研究和个人测试使用。项目未经完整的商业级安全性、稳定性和兼容性验证，使用者应自行承担使用风险。因使用本项目造成的音频中断、数据丢失、设备或系统异常、驱动问题以及其他直接或间接损失，作者概不负责。
