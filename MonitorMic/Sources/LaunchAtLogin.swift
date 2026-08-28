import Foundation

/// 开机自启：通过 ~/Library/LaunchAgents 下的 LaunchAgent 实现
/// （比 SMAppService 对未公证/Ad-hoc 签名的应用更可靠）
enum LaunchAtLogin {
    static let label = "com.example.monitormic.login"

    private static var plistURL: URL {
        FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent("Library/LaunchAgents/\(label).plist")
    }

    static var isEnabled: Bool {
        FileManager.default.fileExists(atPath: plistURL.path)
    }

    static func setEnabled(_ enabled: Bool) {
        let fm = FileManager.default
        let uid = getuid()
        let domain = "gui/\(uid)"

        // 先卸载旧配置（忽略"不存在"的错误）
        runLaunchctl(["bootout", "\(domain)/\(label)"])

        if enabled {
            guard let exe = Bundle.main.executablePath else { return }
            let plist: [String: Any] = [
                "Label": label,
                "ProgramArguments": [exe],
                "RunAtLoad": true,
                "ProcessType": "Interactive",
                "LimitLoadToSessionType": "Aqua",
            ]
            try? fm.createDirectory(at: plistURL.deletingLastPathComponent(),
                                    withIntermediateDirectories: true)
            (plist as NSDictionary).write(to: plistURL, atomically: true)
            runLaunchctl(["bootstrap", domain, plistURL.path])
        } else {
            try? fm.removeItem(at: plistURL)
        }
    }

    @discardableResult
    private static func runLaunchctl(_ args: [String]) -> Int32 {
        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/bin/launchctl")
        p.arguments = args
        p.standardOutput = FileHandle.nullDevice
        p.standardError = FileHandle.nullDevice
        do {
            try p.run()
            p.waitUntilExit()
            return p.terminationStatus
        } catch {
            return -1
        }
    }
}
