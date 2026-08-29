# MonitorMic 项目技术规格与 Windows 端开发任务书

版本：`2.0.1`

基线提交：`e564836`，标签：`v2.0.1`

本文档是当前项目的完整技术说明，也是交给 Windows 端 Codex 的开发任务书。Windows 端 Codex 应在 Windows 本地工作区重新检查、实现和测试 Windows 客户端，不要把 macOS 客户端当成 Windows 的运行依赖。

## 1. 项目目标

将 Xiaomi/Redmi 显示器上的远场麦克风，通过局域网提供给电脑上的语音应用使用。

实际部署有三个角色：

```text
显示器 Android MicStreamer
        │ TCP 50010 广播 PCM
        ├──────────────> Mac MonitorMic → BlackHole 2ch → Mac 应用麦克风
        └──────────────> Windows MonitorMic → VB-CABLE → Windows 应用麦克风
```

Mac 和 Windows 是两个完全独立的客户端。两台电脑可以同时连接同一台显示器；电脑之间不转发音频，也不需要互相在线。Android 服务负责同时向所有 TCP 客户端广播同一份音频。

## 2. 已确认的协议行为

### 2.1 Android 服务

- 包名：`com.example.micstreamer`
- Service：`com.example.micstreamer/.MicService`
- 默认 TCP 端口：`50010`
- 当前 APK 版本：`2.0.4`，versionCode `6`；显示器端版本独立于桌面客户端
- 服务启动命令：

```sh
adb shell am start-foreground-service -n com.example.micstreamer/.MicService
```

- 服务不再接收电脑 IP 参数；电脑客户端主动连接显示器的 `50010` 端口。
- 没有客户端时不打开麦克风；第一个客户端连接后才启动 `AudioRecord`。
- 显示器重启后由 `BootReceiver` 尝试自动启动服务；电脑端仍需负责检测和重启。
- `MicService` 通过 `CopyOnWriteArrayList<ClientHandler>` 管理多个客户端。
- 每个客户端有独立发送线程和独立队列；单个客户端变慢或断开不能阻塞其他客户端。
- 客户端队列满时丢弃最旧数据，优先保证实时性和延迟上限。

### 2.2 TCP 音频协议

客户端连接建立后，服务先发送一行 ASCII 头：

```text
PCM 48000 2 16\n
```

头部之后是持续的 little-endian PCM16 交错采样：

- 采样率：当前实际测试为 `48000 Hz`
- 声道：当前实际测试为 `2 ch`
- 位深：`16 bit`
- 数据排列：`L0, R0, L1, R1 ...`
- 网络没有消息边界；一次 TCP read 可能包含半个头、完整头、头加 PCM，或者多个 PCM 块。
- 客户端必须自己缓存数据，不能假设一次 `recv/read` 就对应一个音频块。
- 客户端必须按 `channels * 2` 对齐 PCM 帧，尾部不完整帧要留到下一次读取。

Windows 客户端必须兼容头部和数据分包、断线重连、服务端重启以及短暂无数据。

## 3. 已完成的 macOS 客户端

源码目录：`MonitorMic/Sources/`

### 3.1 主要模块

- `MonitorMicApp.swift`：SwiftUI App 入口、菜单栏常驻、主窗口。
- `ContentView.swift`：单显示器配置、ADB 状态、服务状态、接收状态、电平表和日志界面。
- `AppState.swift`：主线程状态、配置、ADB 操作编排、自动修复。
- `ADBController.swift`：封装 adb 子进程、超时、连接、安装 APK、启动/停止服务。
- `AudioReceiver.swift`：Network.framework TCP 客户端、PCM 解析、抖动缓冲、PCM 转换、AVAudioEngine 输出和自愈。
- `LaunchAtLogin.swift`：通过 `~/Library/LaunchAgents` 实现登录自启。

### 3.2 macOS 音频链路

macOS 客户端是“单显示器、单接收器”模型：

1. `NWConnection` 主动连接 `显示器 IP:50010`。
2. 缓存并解析 `PCM rate channels bits` 头部。
3. 按 1024 帧拆分 PCM，使用 `AVAudioConverter` 转成 48 kHz、双声道、Float32。
4. 使用 `AVAudioPlayerNode` 和 `AVAudioEngine` 播放到 `BlackHole 2ch`。
5. 先缓存约 6 个 1024 帧缓冲后再播放，避免刚连接时断续。
6. 输出积压时丢弃旧数据，避免延迟无限增长。
7. TCP 断开后 2 秒重连。
8. 音频引擎停止、BlackHole 重载或输出设备暂时变化时自动重建。

重要原则：没有 BlackHole 时不能把麦克风流误输出到 Mac 扬声器；创建引擎时必须成功绑定 BlackHole。

### 3.3 macOS 配置和日志

- 配置：`~/Library/Application Support/MonitorMic/config.json`
- 日志：`~/Library/Logs/MonitorMic/monitor-mic.log`
- 当前配置只有一个显示器 IP，默认值为 `192.168.100.7`。
- 端口固定为 `50010`，因为 Android 服务和 Windows 客户端都使用该端口。
- `MonitorMic/VERSION` 是 macOS 客户端版本来源；构建时同步到 `Info.plist`。Windows 客户端和 Android 显示器端分别使用各自组件目录下的版本文件。

### 3.4 macOS 构建

```sh
cd MonitorMic
./build_app.sh
./make_dmg.sh
```

产物：

- `MonitorMic/MonitorMic.app`
- `MonitorMic/MonitorMic-2.0.1.dmg`

构建脚本会把 adb 和 `micstreamer.apk` 放入 app 资源目录。构建产物、本地 SDK、APK、ADB 和 Windows 驱动均被 `.gitignore` 排除。

## 4. 当前 Android 源码结构

目录：`micstreamer/`

- `MicService.java`：打开麦克风、监听端口、启动/停止采集、向所有客户端广播。
- `ClientHandler.java`：单个 TCP 客户端的发送队列和写入线程。
- `MainActivity.java`：启动前台服务后立即退出界面，不覆盖显示器画面。
- `BootReceiver.java`：显示器启动后拉起服务。
- `AndroidManifest.xml`：录音、网络、前台麦克风服务、唤醒锁等权限。

Android 构建不依赖 Android Studio：

```sh
cd micstreamer
./build.sh
```

构建过程使用项目外部准备的 Android SDK、JDK、build-tools、`javac`、`d8`、`aapt2` 和 `apksigner`。这些工具不提交到 GitHub。

## 5. 当前 Windows 源码和重构目标

已有源码目录：`MonitorMicWin/`

当前基础技术栈：

- .NET 8 WinForms
- NAudio 2.2.1
- ADB 控制显示器
- VB-CABLE 作为 Windows 虚拟麦克风输出

已有模块包括 `AdbController.cs`、`AudioPipeline.cs`、`AppState.cs`、`MainForm.cs`、`TrayApp.cs`、`Log.cs` 等。Windows Codex 必须先阅读这些源码，不要假设它们与旧版“电脑 IP 传给 Android”的协议一致；当前协议是 Android 服务器、多客户端、客户端主动连接。

### 5.1 Windows 端必须实现的行为

1. 用户输入一台显示器的 IP；Windows 客户端主动连接 `IP:50010`。
2. 可以通过 ADB 连接显示器并安装/授权/启动 `MicStreamer`。
3. ADB 启动命令不得再依赖 `--es host` 或 `--ei port` 来配置 Android 推流目标。
4. 解析并校验 `PCM` 头，拒绝非法采样率、声道数和非 16 bit 数据。
5. 正确处理 TCP 分包和“头部后面紧跟 PCM 数据”的情况。
6. 使用有界抖动缓冲；缓冲过深时丢旧数据，不允许实时麦克风延迟持续增长。
7. 输出到 VB-CABLE 的播放端，不能把音频默认播到物理扬声器。
8. 显示器服务断开、重启或网络短暂中断后自动重连。
9. VB-CABLE 被重启、设备 ID 变化或播放线程停止后自动恢复。
10. 单实例、托盘常驻、日志、开机启动和清晰的状态提示必须保留或等价实现。
11. “一键修复”顺序应为：ADB 连接 → 检查/禁用可能占用麦克风的小爱服务 → 安装/授权 APK → 启动 Android 服务 → 启动 Windows 接收器。
12. Windows 客户端只服务一台显示器；Mac 客户端和 Windows 客户端之间不共享状态，也不通过 Windows 转发。

### 5.2 Windows 端建议分层

建议保留清晰的四层边界：

```text
UI / Tray
  └─ AppState：配置、状态轮询、一键修复
       ├─ AdbController：只负责 adb 命令和超时
       └─ AudioReceiver：只负责 TCP 协议、重连、PCM 缓冲
            └─ AudioOutput：只负责 NAudio/VB-CABLE 播放和设备重建
```

不要让 UI 线程执行 adb、网络 read、音频设备枚举或驱动安装。所有这些操作都应在后台线程/异步任务执行，再通过线程安全状态更新 UI。

### 5.3 Windows 音频实现要求

可继续使用 NAudio，但要明确：

- 网络接收和音频播放解耦。
- `BufferedWaveProvider` 必须设置最大缓冲时长。
- 检测 `PlaybackState`，播放停止时重建 `WasapiOut`。
- 根据友好名称和输出方向寻找 `CABLE Input (VB-Audio Virtual Cable)`。
- 找不到 VB-CABLE 时，状态应明确显示“未安装/未找到”，不要静默改用默认扬声器。
- 输出格式以 Android 发送的 PCM 头为准；如果 NAudio 输出设备只接受固定格式，必须显式转换或拒绝并提示，而不是把字节当成另一种格式播放。
- 测试音必须只进入 CABLE Input，并在 UI 中明确告知用户应在 Windows 声音设置中观察 CABLE Output 的输入电平。

## 6. ADB 验证命令

Windows PowerShell 示例：

```powershell
$adb = ".\\adb\\adb.exe"
$ip = "192.168.100.7"

& $adb connect "$ip`:5555"
& $adb devices -l
& $adb -s "$ip`:5555" shell "pidof com.example.micstreamer"
& $adb -s "$ip`:5555" shell "toybox netstat -lnt | grep 50010"
& $adb -s "$ip`:5555" shell "dumpsys package com.example.micstreamer | grep -E 'versionName|versionCode|RECORD_AUDIO'"
& $adb -s "$ip`:5555" logcat -d -v time -s "MicStreamer:*" "*:S"
```

预期结果：

- `devices` 中显示 `192.168.100.7:5555 device`。
- `pidof` 返回 MicStreamer PID。
- `50010` 为 `LISTEN`。
- `RECORD_AUDIO` 为 `granted=true`。
- 客户端接入后 logcat 出现 `client connected` 和 `mic opened`。

## 7. 测试计划和验收标准

### 7.1 协议单元测试

Windows Codex 应增加不依赖真实 Android 设备的测试：

- 分片发送 `PCM 48000 2 16\n`，确认能解析。
- 一次发送头部加 PCM，确认剩余数据不丢失。
- 头部中间插入无效内容，确认安全拒绝并重置连接。
- 发送 1 声道、2 声道和非法 0/9 声道头，确认行为符合设计。
- 发送奇数长度 PCM，确认尾部不完整帧会等待下一块。
- 模拟服务端关闭、连接失败和 2 秒后恢复。
- 模拟慢输出，确认缓冲有上限且延迟不会无限增长。

### 7.2 实机链路测试

1. 只有 Mac 客户端连接：Android `total 1`，Mac 收到音频。
2. 只有 Windows 客户端连接：Android `total 1`，Windows 收到音频。
3. Mac 和 Windows 同时连接：Android `total 2`，两边都持续收到 PCM。
4. 关闭 Mac：Windows 仍继续收音频，Android 只剩 `total 1`。
5. 关闭 Windows：Mac 仍继续收音频。
6. 重启 Android 服务：两边客户端都能自动恢复。
7. 断开并恢复 Wi-Fi：两边都能自动重连。
8. 拔出/重启虚拟声卡：对应客户端能恢复，且不会误播到物理扬声器。
9. 长时间运行至少 30 分钟：内存、缓冲深度和音频延迟稳定。

### 7.3 当前已完成的实测

在 macOS 开发机连接 `192.168.100.7` 的实际显示器已验证：

```text
MicStreamer 进程：19431
50010：LISTEN
录音权限：granted=true
协议头：PCM 48000 2 16
单客户端：持续收到 4096 字节 PCM
双客户端：两个客户端都收到 PCM 头和后续 4096 字节数据
```

macOS 客户端日志已出现：

```text
已连接显示器 192.168.100.7:50010
流参数: 48000 Hz / 2 声道 / 16 bit
缓冲完成，开始输出到 BlackHole
```

Windows 端 Codex 需要在 Windows 本地重复上述实机测试，特别是 VB-CABLE 的真实输出和同时连接场景。

## 8. Git 和版本约定

当前本地仓库：

- 分支：`main`
- `v2.0.0`：macOS 客户端重构基线
- `v2.0.1`：BlackHole 引擎误重建修复和双客户端实测基线

建议 Windows 端开发：

1. 在 Windows 本地从 `main` 或 `v2.0.1` 开始。
2. Windows 代码只修改 `MonitorMicWin/`，协议和公共说明修改 `README.md`/本文档时保持跨平台一致。
3. 每个可验证阶段创建一个小提交，例如 `refactor windows tcp receiver`、`add vb-cable recovery`。
4. 不提交 `bin/`、`obj/`、`publish/`、ADB、APK、VB-CABLE 驱动和安装包等产物。
5. 完成后至少提供构建命令、测试结果、Windows 版本号和提交哈希。
6. 发布版本使用语义化版本号；Windows 与 macOS 客户端可共享主版本，但平台安装包可以有独立构建号。

## 9. Windows Codex 的明确任务

请在 Windows 本地完成以下任务：

1. 阅读 `PROJECT_SPEC_WINDOWS.md`、`README.md`、`micstreamer/src/` 和 `MonitorMicWin/` 全部源码。
2. 不修改 Android 的服务器协议为单客户端，也不要恢复旧的“Android 主动连接电脑”协议。
3. 基于当前 Android 多客户端服务器协议，重构或重写 Windows 客户端，使其稳定连接 `IP:50010` 并输出到 VB-CABLE。
4. 添加协议解析、断线重连、输出设备恢复、缓冲上限和错误日志测试。
5. 在 Windows 本地构建并运行，不使用 Mac 端作为中转或测试依赖。
6. 用一台 Android 显示器同时连接 Mac 和 Windows，验证两个客户端互不影响。
7. 最终报告：改动文件、构建命令、测试命令、实测日志摘要、已知限制和 Git 提交哈希。
