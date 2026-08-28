import Foundation
import Darwin

/// ADB 控制层：封装所有对显示器的 adb 操作
final class ADBController {
    private static let pkg = "com.example.micstreamer"

    /// 优先使用 App 内置的 adb，其次用系统的
    private static var resolvedADBPath: String {
        if let bundled = Bundle.main.resourceURL?.appendingPathComponent("adb").path,
           FileManager.default.isExecutableFile(atPath: bundled) {
            return bundled
        }
        let candidates = [
            "/opt/homebrew/bin/adb",
            "/usr/local/bin/adb",
            "/usr/bin/adb"
        ]
        return candidates.first(where: { FileManager.default.isExecutableFile(atPath: $0) })
            ?? candidates[0]
    }

    // MARK: - 基础执行器
    @discardableResult
    func run(_ args: [String], timeout: TimeInterval = 15) async -> String {
        let executable = Self.resolvedADBPath
        return await withCheckedContinuation { (cont: CheckedContinuation<String, Never>) in
            DispatchQueue.global(qos: .userInitiated).async {
                let p = Process()
                let pipe = Pipe()
                p.executableURL = URL(fileURLWithPath: executable)
                p.arguments = args
                p.standardOutput = pipe
                p.standardError = pipe
                do {
                    try p.run()
                    let deadline = Date().addingTimeInterval(timeout)
                    while p.isRunning && Date() < deadline {
                        Thread.sleep(forTimeInterval: 0.02)
                    }
                    if p.isRunning {
                        p.terminate()
                        Thread.sleep(forTimeInterval: 0.1)
                        if p.isRunning { Darwin.kill(p.processIdentifier, SIGKILL) }
                    }
                    let data = pipe.fileHandleForReading.readDataToEndOfFile()
                    cont.resume(returning: String(data: data, encoding: .utf8) ?? "")
                } catch {
                    cont.resume(returning: "执行失败: \(error.localizedDescription)")
                }
            }
        }
    }

    @discardableResult
    func shell(_ cmd: String) async -> String {
        await run(["shell", cmd])
    }

    // MARK: - 连接
    func connect(ip: String) async -> String {
        let out = await run(["connect", "\(ip):5555"])
        return out.trimmingCharacters(in: .whitespacesAndNewlines)
    }

    func isConnected(ip: String) async -> Bool {
        let out = await run(["devices"], timeout: 8)
        return out.contains("\(ip):5555\tdevice")
    }

    // MARK: - 小爱唤醒服务
    func isWakeupDisabled() async -> Bool {
        let out = await shell("pm list packages -d")
        return out.contains("com.xiaomi.wakeupservice")
    }

    func setWakeupEnabled(_ enabled: Bool) async -> String {
        if enabled {
            return await shell("pm enable com.xiaomi.wakeupservice")
                .trimmingCharacters(in: .whitespacesAndNewlines)
        } else {
            return await shell("pm disable-user --user 0 com.xiaomi.wakeupservice")
                .trimmingCharacters(in: .whitespacesAndNewlines)
        }
    }

    // MARK: - MicStreamer App
    func isAppInstalled() async -> Bool {
        let out = await shell("pm list packages \(Self.pkg)")
        return out.contains(Self.pkg)
    }

    func install(apkPath: String) async -> String {
        let out = await run(["install", "-r", apkPath], timeout: 60)
        return out.trimmingCharacters(in: .whitespacesAndNewlines)
    }

    func isServiceRunning() async -> Bool {
        let out = await shell("pidof \(Self.pkg)")
        return !out.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
    }

    /// v1.2.0 服务器模式：无需目标参数，客户端（本机/其他设备）自行连接显示器的 50010 端口
    func startService() async -> String {
        let out = await shell("am start-foreground-service -n \(Self.pkg)/.MicService")
        let trimmed = out.trimmingCharacters(in: .whitespacesAndNewlines)
        return trimmed.isEmpty ? "已发送启动命令" : trimmed
    }

    func stopService() async -> String {
        let out = await shell("am force-stop \(Self.pkg)")
        let trimmed = out.trimmingCharacters(in: .whitespacesAndNewlines)
        return trimmed.isEmpty ? "已停止" : trimmed
    }

    // MARK: - 本机局域网 IP
    static func localIPAddress() -> String? {
        var result: String?
        var ifaddr: UnsafeMutablePointer<ifaddrs>?
        guard getifaddrs(&ifaddr) == 0, let first = ifaddr else { return nil }
        defer { freeifaddrs(ifaddr) }
        var ptr = first
        while true {
            let ifa = ptr.pointee
            let flags = Int32(ifa.ifa_flags)
            let isUp = (flags & IFF_UP) != 0 && (flags & IFF_LOOPBACK) == 0
            if isUp, let addr = ifa.ifa_addr, addr.pointee.sa_family == UInt8(AF_INET) {
                var host = [CChar](repeating: 0, count: Int(NI_MAXHOST))
                if getnameinfo(addr, socklen_t(addr.pointee.sa_len), &host, socklen_t(host.count),
                               nil, 0, NI_NUMERICHOST) == 0 {
                    let ip = String(cString: host)
                    if ip.hasPrefix("192.168.") || ip.hasPrefix("10.") || ip.hasPrefix("172.") {
                        result = ip
                        if String(cString: ifa.ifa_name) == "en0" { return ip }
                    }
                }
            }
            guard let next = ifa.ifa_next else { break }
            ptr = next
        }
        return result
    }
}
