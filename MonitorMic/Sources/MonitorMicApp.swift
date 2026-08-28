import SwiftUI
import AppKit

@main
struct MonitorMicApp: App {
    @StateObject private var state = AppState()

    init() {
        let currentPID = ProcessInfo.processInfo.processIdentifier
        if let existing = NSRunningApplication.runningApplications(withBundleIdentifier: "com.example.monitormic")
            .first(where: { $0.processIdentifier != currentPID }) {
            existing.activate(options: [.activateAllWindows, .activateIgnoringOtherApps])
            exit(EXIT_SUCCESS)
        }
    }

    var body: some Scene {
        WindowGroup(id: "main") {
            ContentView()
                .environmentObject(state)
                .frame(minWidth: 560, minHeight: 640)
        }
        .windowResizability(.contentSize)
        .defaultSize(width: 600, height: 720)

        // 菜单栏常驻
        MenuBarExtra {
            MenuBarView()
                .environmentObject(state)
        } label: {
            Image(systemName: state.streamingActive
                  ? "mic.fill"
                  : (state.adbConnected ? "mic" : "mic.slash"))
        }
        .menuBarExtraStyle(.menu)
    }
}

/// 菜单栏下拉内容
struct MenuBarView: View {
    @EnvironmentObject var state: AppState
    @Environment(\.openWindow) private var openWindow

    var body: some View {
        // 状态摘要
        if state.streamingActive {
            Label("正在串流 \(state.sampleInfo)", systemImage: "dot.radiowaves.left.and.right")
        } else if state.adbConnected {
            Label("已连接 \(state.deviceModel) · 未串流", systemImage: "tv")
        } else {
            Label("未连接显示器", systemImage: "tv.slash")
        }

        Divider()

        Button {
            Task { await state.healAll() }
        } label: {
            Label("一键修复并启动串流", systemImage: "bolt.fill")
        }
        .disabled(state.busy)

        Button {
            if state.serviceRunning {
                Task { await state.stopStreaming() }
            } else {
                Task { await state.startStreaming() }
            }
        } label: {
            Label(state.serviceRunning ? "停止串流服务" : "启动串流服务",
                  systemImage: state.serviceRunning ? "stop.circle" : "play.circle")
        }
        .disabled(state.busy || !state.adbConnected)

        Button {
            state.toggleReceiver()
        } label: {
            Label(state.receiverRunning ? "停止音频接收器" : "启动音频接收器",
                  systemImage: state.receiverRunning ? "stop.circle" : "play.circle")
        }

        Divider()

        Toggle(isOn: $state.launchAtLogin) {
            Label("开机自动启动", systemImage: "power")
        }
        .toggleStyle(.checkbox)

        Divider()

        let ver = Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "?"
        Text("MonitorMic v\(ver)")
            .foregroundColor(.secondary)

        Divider()

        Button {
            openWindow(id: "main")
            NSApp.activate(ignoringOtherApps: true)
        } label: {
            Label("打开主面板", systemImage: "macwindow")
        }

        Button {
            NSApplication.shared.terminate(nil)
        } label: {
            Label("退出 MonitorMic", systemImage: "xmark.circle")
        }
        .keyboardShortcut("q")
    }
}
