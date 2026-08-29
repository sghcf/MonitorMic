import SwiftUI

struct ContentView: View {
    @EnvironmentObject private var state: AppState

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 14) {
                headerSection
                outputSection
                apkSection
                deviceSection
                logSection
            }
            .padding(18)
        }
        .frame(minWidth: 560, minHeight: 620)
        .onAppear { Task { await state.bootstrap() } }
    }

    private var headerSection: some View {
        HStack(spacing: 10) {
            Image(systemName: "mic.and.signal.meter")
                .font(.title2)
                .foregroundColor(.accentColor)
            VStack(alignment: .leading, spacing: 2) {
                Text("MonitorMic")
                    .font(.title2.bold())
                Text("macOS 客户端 · 独立连接一台显示器")
                    .font(.caption)
                    .foregroundColor(.secondary)
            }
            Spacer()
            Circle()
                .fill(state.monitor.adbConnected ? .green : .red)
                .frame(width: 10, height: 10)
            Text(state.monitor.adbConnected
                 ? "已连接 \(state.monitor.deviceModel)"
                 : "未连接显示器")
                .font(.callout)
                .foregroundColor(.secondary)
        }
    }

    private var outputSection: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack {
                Label("Mac 音频输出", systemImage: "speaker.wave.2")
                    .font(.headline)
                Spacer()
                Button("播放测试音") { state.playTestTone() }
                    .controlSize(.small)
                    .disabled(!state.blackHoleAvailable)
            }
            HStack(spacing: 8) {
                Image(systemName: state.blackHoleAvailable ? "checkmark.circle.fill" : "exclamationmark.triangle.fill")
                    .foregroundColor(state.blackHoleAvailable ? .green : .orange)
                Text(state.blackHoleAvailable
                     ? "输出到 \(state.blackHoleName)。请在微信 / Zoom / Discord 中选择 BlackHole 2ch。"
                     : "未找到 BlackHole 2ch。请先安装虚拟声卡，再重新打开本程序。")
                    .font(.caption)
                    .foregroundColor(.secondary)
            }
        }
        .padding(12)
        .background(Color(nsColor: .controlBackgroundColor))
        .cornerRadius(10)
    }

    private var deviceSection: some View {
        VStack(alignment: .leading, spacing: 8) {
            Text("显示器连接")
                .font(.headline)
            MonitorCard(state: state)
        }
    }

    private var apkSection: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack {
                Label("显示器端 APK", systemImage: "shippingbox")
                    .font(.headline)
                Spacer()
                Text("独立版本：\(state.displayAPKVersion)")
                    .font(.caption)
                    .foregroundColor(.secondary)
            }
            Text("macOS 客户端 v\(state.clientVersion)；显示器端 APK 单独维护，已有服务无需重复安装。")
                .font(.caption)
                .foregroundColor(.secondary)
            Text(state.selectedAPKPath.isEmpty ? "尚未选择 APK" : state.selectedAPKPath)
                .font(.system(.caption, design: .monospaced))
                .foregroundColor(state.selectedAPKPath.isEmpty ? .secondary : .primary)
                .lineLimit(2)
                .textSelection(.enabled)
            HStack(spacing: 8) {
                Button("选择 APK") { state.chooseAPK() }
                    .controlSize(.small)
                Button("安装到显示器") {
                    Task { await state.installApp() }
                }
                .controlSize(.small)
                .buttonStyle(.borderedProminent)
                .disabled(state.selectedAPKPath.isEmpty || state.busy || !state.monitor.adbConnected)
            }
        }
        .padding(12)
        .background(Color(nsColor: .controlBackgroundColor))
        .cornerRadius(10)
    }

    private var logSection: some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack {
                Text("运行日志").font(.headline)
                Spacer()
                Toggle("开机自启", isOn: $state.launchAtLogin)
                    .toggleStyle(.checkbox)
                    .controlSize(.small)
                Toggle("断链自动修复", isOn: $state.autoHeal)
                    .toggleStyle(.checkbox)
                    .controlSize(.small)
                Button("清空") { state.logText = "" }
                    .controlSize(.small)
            }
            ScrollViewReader { proxy in
                ScrollView {
                    Text(state.logText.isEmpty ? "暂无日志" : state.logText)
                        .font(.system(.caption, design: .monospaced))
                        .foregroundColor(state.logText.isEmpty ? .secondary : .primary)
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .textSelection(.enabled)
                        .padding(8)
                        .id("log-bottom")
                }
                .frame(minHeight: 180, maxHeight: 260)
                .background(Color(nsColor: .textBackgroundColor).opacity(0.55))
                .cornerRadius(8)
                .onChange(of: state.logText) { _ in
                    proxy.scrollTo("log-bottom", anchor: .bottom)
                }
            }
        }
    }
}

private struct MonitorCard: View {
    @ObservedObject var state: AppState
    @ObservedObject var device: MonitorDevice

    init(state: AppState) {
        self.state = state
        self.device = state.monitor
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack {
                Label(device.title, systemImage: "display")
                    .font(.headline)
                Spacer()
                statusPill
            }

            HStack(spacing: 8) {
                TextField("显示器 IP", text: $device.ip)
                    .textFieldStyle(.roundedBorder)
                    .onSubmit { Task { await state.connect() } }
                Button(device.adbConnected ? "重新连接" : "连接") {
                    Task { await state.connect() }
                }
                .disabled(state.busy)
                Button("一键修复并启动") {
                    Task { await state.healAll() }
                }
                .buttonStyle(.borderedProminent)
                .tint(.orange)
                .disabled(state.busy)
            }

            statusRow(icon: "mic.slash", title: "小爱远场唤醒",
                      value: device.wakeupDisabled ? "已禁用（麦克风可用）" : "运行中（可能占用麦克风）",
                      ok: device.wakeupDisabled,
                      actionTitle: device.wakeupDisabled ? "恢复" : "禁用") {
                Task { await state.toggleWakeup() }
            }
            statusRow(icon: "shippingbox", title: "MicStreamer App",
                      value: device.appInstalled
                        ? "已安装 · APK \(device.apkVersion.isEmpty ? "版本未知" : device.apkVersion)"
                        : "未安装（不影响普通连接）",
                      ok: device.appInstalled,
                      actionTitle: "选择 APK") {
                state.chooseAPK()
            }
            statusRow(icon: "antenna.radiowaves.left.and.right", title: "显示器串流服务",
                      value: device.serviceRunning ? "运行中（端口 50010，可供 Mac / Windows 同时连接）" : "已停止",
                      ok: device.serviceRunning,
                      actionTitle: device.serviceRunning ? "停止" : "启动") {
                Task {
                    if device.serviceRunning { await state.stopStreaming() }
                    else { await state.startStreaming() }
                }
            }
            statusRow(icon: "waveform", title: "Mac 音频接收器",
                      value: device.receiverRunning
                        ? (device.streamingActive ? "接收中 \(device.sampleInfo)" : "已启动，等待显示器数据")
                        : "已停止",
                      ok: device.receiverRunning && device.streamingActive,
                      actionTitle: device.receiverRunning ? "停止" : "启动") {
                state.toggleReceiver()
            }

            HStack(spacing: 10) {
                Text("麦克风电平")
                    .font(.callout)
                    .frame(width: 72, alignment: .leading)
                LevelMeterView(level: device.level)
                    .frame(height: 14)
                if device.streamingActive {
                    Text("LIVE")
                        .font(.caption2.bold())
                        .foregroundColor(.white)
                        .padding(.horizontal, 6)
                        .padding(.vertical, 2)
                        .background(.red)
                        .cornerRadius(4)
                }
            }
        }
        .padding(14)
        .background(Color(nsColor: .controlBackgroundColor))
        .cornerRadius(10)
    }

    private var statusPill: some View {
        Text(device.adbConnected ? "ADB 已连接" : "ADB 未连接")
            .font(.caption.bold())
            .foregroundColor(.white)
            .padding(.horizontal, 8)
            .padding(.vertical, 4)
            .background(device.adbConnected ? .green : .gray)
            .cornerRadius(6)
    }

    private func statusRow(icon: String, title: String, value: String, ok: Bool,
                           actionTitle: String, action: @escaping () -> Void) -> some View {
        HStack(spacing: 8) {
            Image(systemName: icon)
                .frame(width: 22)
                .foregroundColor(ok ? .green : .secondary)
            VStack(alignment: .leading, spacing: 2) {
                Text(title).font(.callout.bold())
                Text(value)
                    .font(.caption)
                    .foregroundColor(ok ? .green : .secondary)
                    .lineLimit(2)
            }
            Spacer()
            Button(actionTitle, action: action)
                .controlSize(.small)
                .disabled(state.busy || !device.adbConnected)
        }
    }
}

private struct LevelMeterView: View {
    let level: Float

    var body: some View {
        GeometryReader { geometry in
            ZStack(alignment: .leading) {
                RoundedRectangle(cornerRadius: 7)
                    .fill(Color(nsColor: .separatorColor).opacity(0.3))
                RoundedRectangle(cornerRadius: 7)
                    .fill(LinearGradient(colors: [.green, .yellow, .red],
                                         startPoint: .leading, endPoint: .trailing))
                    .frame(width: geometry.size.width * CGFloat(min(1, max(0, level))))
                    .animation(.easeOut(duration: 0.1), value: level)
            }
        }
    }
}
