# MonitorMic

把 Xiaomi/Redmi 显示器的麦克风通过局域网提供给 Mac 或 Windows 电脑使用。

普通用户不需要自己编译源码，直接从 [Releases](https://github.com/sghcf/MonitorMic/releases/latest) 下载对应系统的安装包即可。

## 组件版本

三个软件独立维护版本，桌面客户端更新不代表显示器端服务也需要更新：

| 组件 | 当前版本 | 版本文件 | 作用 |
|---|---:|---|---|
| macOS 客户端 | **2.1.0** | `MonitorMic/VERSION` | 接收 PCM 并输出到 BlackHole |
| Windows 客户端 | **2.1.0** | `MonitorMicWin/VERSION` | 接收 PCM 并输出到 VB-CABLE |
| Android 显示器端 | **2.0.4** | `micstreamer/VERSION` | 从显示器麦克风采集并广播 PCM |

## 下载

桌面版 v2.1.0 Release 只提供桌面客户端：

- macOS：`MonitorMic-2.1.0.dmg`
- Windows 安装版：`MonitorMicSetup-2.1.0.exe`
- Windows 便携版：`MonitorMic.exe`

显示器端 APK 单独发布。需要安装或更新显示器服务时，请从 **v2.0.4 Release** 下载 `micstreamer.apk`，然后在客户端内手动选择并安装。APK 版本不需要与桌面客户端版本一致。

## macOS 使用方法

1. 下载并打开 `MonitorMic-2.1.0.dmg`，将 `MonitorMic.app` 拖入“应用程序”。
2. 安装并启用 [BlackHole 2ch](https://existential.audio/blackhole/)。
3. 打开 MonitorMic，输入显示器的局域网 IP 地址。
4. 点击“一键修复并启动”。已有显示器服务时，程序会直接启动/连接并接收音频，不要求 APK。
5. 如果显示器端尚未安装服务，在“显示器端 APK”区域点击“选择 APK”，确认路径后再点击“安装到显示器”。安装成功后程序会授予录音权限并启动服务。
6. 在 macOS 的声音设置和微信、Zoom、Discord 等应用中选择 `BlackHole 2ch` 作为麦克风。

macOS DMG 会内置 ADB（用于连接和控制显示器），不会内置 `micstreamer.apk`。因此普通连接不受 APK 文件是否存在影响。

电脑和显示器需要连接到同一个局域网。首次使用时，显示器需要开启 ADB 调试并允许电脑连接。

## Windows 使用方法

1. 下载并运行 `MonitorMicSetup-2.1.0.exe`。
2. 按安装程序提示完成安装；安装包包含 MonitorMic、ADB 和 VB-CABLE 相关文件。显示器端 APK 从 v2.0.4 Release 单独获取，并在客户端内按需选择安装。
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

显示器端服务当前版本为 **2.0.4**，`versionCode` 为 `6`。它使用 TCP `50010` 端口广播 48 kHz、16 bit PCM 音频，并支持 Mac 与 Windows 同时连接。

显示器端已安装并正常运行时，不需要每次更新桌面客户端都重新安装 APK。需要安装或更新时，从 v2.0.4 Release 下载 `micstreamer.apk`，在 macOS 客户端的“显示器端 APK”区域选择后手动安装；Windows 客户端按其自身界面执行相同操作。

## 故障排查

- 确认电脑和显示器在同一个局域网，并填写正确的显示器 IP。
- 确认显示器已开启 ADB 调试，并允许当前电脑连接。
- macOS 确认已安装 `BlackHole 2ch`，Windows 确认已安装 VB-CABLE。
- 目标应用中选择正确的虚拟麦克风：macOS 选择 `BlackHole 2ch`，Windows 选择 `CABLE Output`。
- 关闭同一台电脑上重复运行的 MonitorMic 实例后再重试。

## 开发者信息

源码位于本仓库中。macOS 客户端使用 Swift，Windows 客户端使用 .NET 8，显示器端服务使用 Java。详细技术规格和 Windows 开发记录见 [`PROJECT_SPEC_WINDOWS.md`](PROJECT_SPEC_WINDOWS.md)。

构建脚本仍保留在仓库中，主要用于开发和调试；普通用户请优先使用 Releases 中的安装包。macOS、Windows、Android 分别读取各自组件目录下的 `VERSION` 文件。

## 免责声明

本项目是 **100% 纯 AI 生成项目**，仅供学习、研究和个人测试使用。项目未经完整的商业级安全性、稳定性和兼容性验证，使用者应自行承担使用风险。因使用本项目造成的音频中断、数据丢失、设备或系统异常、驱动问题以及其他直接或间接损失，作者概不负责。
