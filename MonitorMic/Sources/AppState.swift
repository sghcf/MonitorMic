import Foundation
import Combine

private struct MonitorConfig: Codable {
    var schemaVersion = 1
    var monitorIP = "192.168.100.7"
    var autoHeal = true
    var launchAtLogin = false
}

/// Stores user settings in Application Support instead of /tmp or a registry.
/// The JSON file is portable, inspectable, and easy to migrate for future releases.
private enum ConfigStore {
    static let directory = FileManager.default.urls(for: .applicationSupportDirectory,
                                                     in: .userDomainMask)[0]
        .appendingPathComponent("MonitorMic", isDirectory: true)
    static let url = directory.appendingPathComponent("config.json")

    static func load() -> MonitorConfig {
        guard let data = try? Data(contentsOf: url),
              let config = try? JSONDecoder().decode(MonitorConfig.self, from: data) else {
            // Migrate the old single-IP UserDefaults value once.
            let legacyIP = UserDefaults.standard.string(forKey: "monitorIP")
            var config = MonitorConfig()
            if let legacyIP, !legacyIP.isEmpty { config.monitorIP = legacyIP }
            return config
        }
        return config
    }

    static func save(_ config: MonitorConfig) {
        do {
            try FileManager.default.createDirectory(at: directory,
                                                     withIntermediateDirectories: true)
            let encoder = JSONEncoder()
            encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
            try encoder.encode(config).write(to: url, options: .atomic)
        } catch {
            NSLog("MonitorMic config save failed: %@", error.localizedDescription)
        }
    }
}

/// One macOS client session for one display.
@MainActor
final class MonitorDevice: ObservableObject, Identifiable {
    let id: String
    let title: String
    let receiver: AudioReceiver

    @Published var ip: String {
        didSet { onConfigChanged?() }
    }
    @Published var adbConnected = false
    @Published var deviceModel = ""
    @Published var wakeupDisabled = false
    @Published var appInstalled = false
    @Published var serviceRunning = false
    @Published var receiverRunning = false
    @Published var streamingActive = false
    @Published var sampleInfo = ""
    @Published var level: Float = 0

    var onConfigChanged: (() -> Void)?

    init(id: String, title: String, ip: String, receiver: AudioReceiver) {
        self.id = id
        self.title = title
        self.ip = ip
        self.receiver = receiver
    }
}

/// App-wide state for the macOS client.
///
/// This deliberately models one display. The Windows build is another client;
/// both can independently connect to the same Android MicStreamer server.
@MainActor
final class AppState: ObservableObject {
    static let streamPort: UInt16 = 50010

    @Published private(set) var monitor: MonitorDevice
    @Published private(set) var blackHoleAvailable = false
    @Published private(set) var blackHoleName = "BlackHole 2ch"
    @Published var autoHeal: Bool
    @Published var launchAtLogin: Bool {
        didSet {
            guard launchAtLogin != oldValue else { return }
            LaunchAtLogin.setEnabled(launchAtLogin)
            saveConfig()
            log(launchAtLogin ? "✅ 已开启开机自启（登录后自动运行）" : "已关闭开机自启")
        }
    }
    @Published var busy = false
    @Published var logText = ""

    let adb = ADBController()
    let receiver: AudioReceiver

    private var pollTimer: Timer?
    private var hasBootstrapped = false
    private var config = MonitorConfig()

    var receiverRunning: Bool { monitor.receiverRunning }
    var streamingActive: Bool { monitor.streamingActive }
    var sampleInfo: String { monitor.sampleInfo }
    var level: Float { monitor.level }
    var adbConnected: Bool { monitor.adbConnected }
    var deviceModel: String { monitor.deviceModel }
    var serviceRunning: Bool { monitor.serviceRunning }

    init() {
        config = ConfigStore.load()
        receiver = AudioReceiver()
        monitor = MonitorDevice(id: "display", title: "显示器", ip: config.monitorIP, receiver: receiver)
        autoHeal = config.autoHeal
        launchAtLogin = config.launchAtLogin || LaunchAtLogin.isEnabled

        monitor.onConfigChanged = { [weak self] in self?.saveConfig() }
        receiver.onLevel = { [weak self] level in
            Task { @MainActor in self?.monitor.level = level }
        }
        receiver.onStateChange = { [weak self] running, active, info in
            Task { @MainActor in
                guard let self else { return }
                self.monitor.receiverRunning = running
                self.monitor.streamingActive = active
                self.monitor.sampleInfo = info ?? ""
            }
        }
        receiver.onLog = { [weak self] message in
            Task { @MainActor in self?.log(message) }
        }
    }

    deinit {
        pollTimer?.invalidate()
        receiver.stop()
    }

    // MARK: - Persistence and logging

    func saveConfig() {
        config.monitorIP = monitor.ip
        config.autoHeal = autoHeal
        config.launchAtLogin = launchAtLogin
        ConfigStore.save(config)
    }

    func log(_ message: String) {
        let formatter = DateFormatter()
        formatter.dateFormat = "HH:mm:ss"
        let line = "[\(formatter.string(from: Date()))] \(message)\n"
        logText += line
        if logText.count > 20_000 { logText = String(logText.suffix(10_000)) }

        do {
            let directory = FileManager.default.urls(for: .libraryDirectory, in: .userDomainMask)[0]
                .appendingPathComponent("Logs/MonitorMic", isDirectory: true)
            try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
            let file = directory.appendingPathComponent("monitor-mic.log")
            if let data = line.data(using: .utf8) {
                if let handle = try? FileHandle(forWritingTo: file) {
                    try handle.seekToEnd()
                    try handle.write(contentsOf: data)
                    try handle.close()
                } else {
                    try data.write(to: file, options: .atomic)
                }
            }
        } catch {
            NSLog("MonitorMic log write failed: %@", error.localizedDescription)
        }
    }

    // MARK: - Startup and polling

    func bootstrap() async {
        guard !hasBootstrapped else { return }
        hasBootstrapped = true
        startPolling()
        await refreshStatus(force: true)
        updateBlackHoleStatus()
        if autoHeal, monitor.adbConnected { await healAll() }
    }

    func startPolling() {
        pollTimer?.invalidate()
        pollTimer = Timer.scheduledTimer(withTimeInterval: 3, repeats: true) { [weak self] _ in
            Task { @MainActor in await self?.refreshStatus() }
        }
    }

    func refreshStatus(force: Bool = false) async {
        guard force || !busy else { return }
        let device = monitor
        let ip = device.ip.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !ip.isEmpty else {
            clearDeviceStatus()
            return
        }

        let connected = await adb.isConnected(ip: ip)
        device.adbConnected = connected
        if connected {
            if device.deviceModel.isEmpty {
                device.deviceModel = await adb.shell("getprop ro.product.model")
                    .trimmingCharacters(in: .whitespacesAndNewlines)
            }
            device.wakeupDisabled = await adb.isWakeupDisabled()
            device.appInstalled = await adb.isAppInstalled()
            let wasRunning = device.serviceRunning
            device.serviceRunning = await adb.isServiceRunning()
            if autoHeal && device.receiverRunning && wasRunning && !device.serviceRunning && device.appInstalled {
                log("⚠️ 检测到串流服务中断，自动重启…")
                await startStreamingInner(device)
            }
        } else {
            device.deviceModel = ""
            device.serviceRunning = false
            device.wakeupDisabled = false
            device.appInstalled = false
        }
    }

    private func clearDeviceStatus() {
        monitor.adbConnected = false
        monitor.deviceModel = ""
        monitor.serviceRunning = false
        monitor.wakeupDisabled = false
        monitor.appInstalled = false
    }

    private func updateBlackHoleStatus() {
        if let name = AudioReceiver.findOutputDevice(matching: "BlackHole") {
            blackHoleAvailable = true
            blackHoleName = "BlackHole 2ch"
            _ = name
        } else {
            blackHoleAvailable = false
            blackHoleName = "未找到 BlackHole"
        }
    }

    // MARK: - Actions

    func connect() async {
        busy = true; defer { busy = false }
        let ip = monitor.ip.trimmingCharacters(in: .whitespacesAndNewlines)
        saveConfig()
        log("连接显示器 \(ip):5555 …")
        let output = await adb.connect(ip: ip)
        log(output.isEmpty ? "已发送连接命令" : output)
        await refreshStatus(force: true)
    }

    func toggleWakeup() async {
        busy = true; defer { busy = false }
        if monitor.wakeupDisabled {
            log("恢复小爱远场唤醒 …")
            log(await adb.setWakeupEnabled(true))
        } else {
            log("禁用小爱远场唤醒（释放麦克风）…")
            log(await adb.setWakeupEnabled(false))
        }
        try? await Task.sleep(nanoseconds: 800_000_000)
        await refreshStatus(force: true)
    }

    func installApp() async {
        busy = true; defer { busy = false }
        await installAppInner()
        await refreshStatus(force: true)
    }

    private func installAppInner() async {
        guard let apk = Bundle.main.resourceURL?.appendingPathComponent("micstreamer.apk"),
              FileManager.default.fileExists(atPath: apk.path) else {
            log("❌ 找不到内置 micstreamer.apk，请先运行 micstreamer/build.sh")
            return
        }
        log("安装 MicStreamer 到显示器 …")
        log(await adb.install(apkPath: apk.path))
        log("授予录音权限 …")
        log(await adb.shell("pm grant com.example.micstreamer android.permission.RECORD_AUDIO"))
    }

    func startStreaming() async {
        busy = true; defer { busy = false }
        await startStreamingInner(monitor)
        await refreshStatus(force: true)
    }

    private func startStreamingInner(_ device: MonitorDevice) async {
        log("启动显示器串流服务（服务器模式 :\(Self.streamPort)）…")
        log(await adb.startService())
        try? await Task.sleep(nanoseconds: 1_500_000_000)
        device.serviceRunning = await adb.isServiceRunning()
    }

    func stopStreaming() async {
        busy = true; defer { busy = false }
        log("停止显示器串流服务 …")
        log(await adb.stopService())
        await refreshStatus(force: true)
    }

    func toggleReceiver() {
        if monitor.receiverRunning {
            receiver.stop()
            monitor.receiverRunning = false
            log("音频接收器已停止")
        } else {
            let ip = monitor.ip.trimmingCharacters(in: .whitespacesAndNewlines)
            receiver.start(host: ip, port: Self.streamPort)
            monitor.receiverRunning = true
            log("音频接收器已启动，连接 \(ip):\(Self.streamPort)（断线自动重连）")
        }
    }

    func playTestTone() {
        guard blackHoleAvailable else {
            log("❌ 未找到 BlackHole，无法播放测试音")
            return
        }
        receiver.playTestTone()
    }

    /// Connect, prepare the display, start its server, then start this Mac client.
    func healAll() async {
        busy = true; defer { busy = false }
        log("——— 一键修复开始 ———")
        let ip = monitor.ip.trimmingCharacters(in: .whitespacesAndNewlines)
        if !monitor.adbConnected {
            log(await adb.connect(ip: ip))
            await refreshStatus(force: true)
            guard monitor.adbConnected else {
                log("❌ 无法连接显示器，请检查 IP 和网络")
                return
            }
        }
        if !monitor.wakeupDisabled {
            log(await adb.setWakeupEnabled(false))
            try? await Task.sleep(nanoseconds: 800_000_000)
            monitor.wakeupDisabled = await adb.isWakeupDisabled()
        }
        if !monitor.appInstalled {
            await installAppInner()
            monitor.appInstalled = await adb.isAppInstalled()
        }
        if !monitor.receiverRunning { toggleReceiver() }
        await startStreamingInner(monitor)
        log("——— 一键修复完成 ———")
    }
}
