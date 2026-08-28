import Foundation
import Network
import AVFoundation
import AudioToolbox

/// Receives the PCM stream published by one display and routes it to BlackHole.
///
/// The Android service is the broadcaster. A macOS client is intentionally a
/// single-display client: Windows can connect to the same Android server in
/// parallel without requiring any special coordination here.
final class AudioReceiver {
    var onLevel: ((Float) -> Void)?
    var onStateChange: ((_ running: Bool, _ streaming: Bool, _ info: String?) -> Void)?
    var onLog: ((String) -> Void)?

    private let queue = DispatchQueue(label: "com.example.monitormic.audio-receiver",
                                      qos: .userInteractive)
    private var host = ""
    private var port: UInt16 = 50010
    private var running = false
    private var connection: NWConnection?
    private var reconnectWork: DispatchWorkItem?
    private var generation = 0

    private var sampleRate = 48_000
    private var channels = 2
    private var headerData = Data()
    private var pcmData = Data()
    private var headerParsed = false
    private var lastDataTime = Date.distantPast
    private var lastLevelTime = Date.distantPast
    private var lastEngineRebuild = Date.distantPast

    private var engine: AVAudioEngine?
    private var player: AVAudioPlayerNode?
    private var playFormat: AVAudioFormat?
    private var pendingBuffers = 0
    private var playing = false
    private var lastCompletion = Date.distantPast
    private var watchdog: DispatchSourceTimer?

    private let framesPerBuffer = 1024
    private let targetPendingBuffers = 12       // about 256 ms at 48 kHz

    var isRunning: Bool { queue.sync { running } }
    var isStreaming: Bool {
        queue.sync { headerParsed && Date().timeIntervalSince(lastDataTime) < 2.5 }
    }
    var streamInfo: String? {
        queue.sync { headerParsed ? "\(sampleRate) Hz · \(channels) ch" : nil }
    }

    // MARK: - Lifecycle

    func start(host: String, port: UInt16) {
        stop()
        self.host = host.trimmingCharacters(in: .whitespacesAndNewlines)
        self.port = port
        queue.async { [weak self] in
            guard let self else { return }
            guard !self.host.isEmpty else {
                self.onLog?("❌ 显示器 IP 为空，无法启动接收器")
                self.onStateChange?(false, false, nil)
                return
            }
            self.running = true
            self.generation += 1
            self.startEngineIfPossible()
            self.startWatchdog()
            self.connectOnce()
            self.onStateChange?(true, false, nil)
        }
    }

    func stop() {
        queue.sync {
            running = false
            generation += 1
            reconnectWork?.cancel()
            reconnectWork = nil
            connection?.cancel()
            connection = nil
            watchdog?.cancel()
            watchdog = nil
            resetStreamState()
            teardownEngine()
        }
        onStateChange?(false, false, nil)
        onLevel?(0)
    }

    /// Sends a short 440 Hz tone to BlackHole for routing diagnostics.
    func playTestTone() {
        queue.async { [weak self] in
            guard let self else { return }
            self.startEngineIfPossible()
            guard self.engine?.isRunning == true, self.player != nil else {
                self.onLog?("❌ BlackHole 输出未就绪，无法播放测试音")
                return
            }
            self.sampleRate = 48_000
            self.channels = 2
            self.headerParsed = true
            self.pcmData.removeAll(keepingCapacity: true)
            var pcm = Data(capacity: 48_000 * 2 * 2)
            for frame in 0..<48_000 {
                let value = Int16(sin(2 * Double.pi * 440 * Double(frame) / 48_000) * 12_000)
                for _ in 0..<2 {
                    pcm.append(UInt8(truncatingIfNeeded: value))
                    pcm.append(UInt8(truncatingIfNeeded: value >> 8))
                }
            }
            self.ingestPCM(pcm)
            self.onLog?("🔔 已发送 440 Hz 测试音，请在 macOS 声音设置中选择 BlackHole 作为输入")
        }
    }

    // MARK: - TCP client

    private func connectOnce() {
        guard running, connection == nil else { return }
        guard let endpointPort = NWEndpoint.Port(rawValue: port) else {
            onLog?("❌ 无效端口: \(port)")
            return
        }

        let currentGeneration = generation
        let conn = NWConnection(host: NWEndpoint.Host(host), port: endpointPort, using: .tcp)
        connection = conn
        conn.stateUpdateHandler = { [weak self, weak conn] state in
            guard let self, let conn else { return }
            self.queue.async {
                guard self.running, currentGeneration == self.generation else { return }
                switch state {
                case .ready:
                    self.onLog?("已连接显示器 \(self.host):\(self.port)")
                    self.resetStreamState()
                    self.receiveLoop(conn, generation: currentGeneration)
                case .failed(let error):
                    self.onLog?("连接失败: \(error.localizedDescription)，2 秒后重试")
                    self.connectionFailed(conn, generation: currentGeneration)
                case .cancelled:
                    break
                default:
                    break
                }
            }
        }
        conn.start(queue: queue)
    }

    private func receiveLoop(_ conn: NWConnection, generation: Int) {
        conn.receive(minimumIncompleteLength: 1, maximumLength: 32_768) {
            [weak self, weak conn] data, _, isComplete, error in
            guard let self, let conn else { return }
            self.queue.async {
                guard self.running, generation == self.generation, self.connection === conn else { return }

                if let data, !data.isEmpty {
                    self.lastDataTime = Date()
                    if self.headerParsed {
                        self.ingestPCM(data)
                    } else {
                        self.headerData.append(data)
                        self.tryParseHeader()
                    }
                }

                if isComplete || error != nil {
                    let suffix = error.map { ": \($0.localizedDescription)" } ?? ""
                    self.onLog?("连接断开\(suffix)，2 秒后重试")
                    self.connectionFailed(conn, generation: generation)
                } else {
                    self.receiveLoop(conn, generation: generation)
                }
            }
        }
    }

    private func connectionFailed(_ conn: NWConnection, generation: Int) {
        guard self.generation == generation else { return }
        if connection === conn { connection = nil }
        conn.cancel()
        resetStreamState()
        onStateChange?(running, false, nil)
        guard running, reconnectWork == nil else { return }
        let work = DispatchWorkItem { [weak self] in
            guard let self else { return }
            self.queue.async {
                self.reconnectWork = nil
                self.connectOnce()
            }
        }
        reconnectWork = work
        queue.asyncAfter(deadline: .now() + 2, execute: work)
    }

    // MARK: - Protocol

    private func resetStreamState() {
        headerData.removeAll(keepingCapacity: true)
        pcmData.removeAll(keepingCapacity: true)
        headerParsed = false
        playing = false
        pendingBuffers = 0
        lastCompletion = Date.distantPast
        player?.stop()
        player?.reset()
    }

    private func tryParseHeader() {
        guard let newline = headerData.firstIndex(of: 0x0A) else {
            if headerData.count > 256 { headerData.removeAll(keepingCapacity: true) }
            return
        }

        let line = String(data: headerData[..<newline], encoding: .ascii)?
            .trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        let parts = line.split(whereSeparator: { $0 == " " || $0 == "\t" })
        guard parts.count == 4, parts[0] == "PCM",
              let rate = Int(parts[1]), rate >= 8_000, rate <= 192_000,
              let channelCount = Int(parts[2]), (1...8).contains(channelCount),
              let bits = Int(parts[3]), bits == 16 else {
            onLog?("⚠️ 无效 PCM 头: \(line.isEmpty ? "<空>" : line)")
            headerData.removeSubrange(...newline)
            return
        }

        sampleRate = rate
        channels = channelCount
        headerParsed = true
        lastDataTime = Date()
        onLog?("流参数: \(rate) Hz / \(channelCount) 声道 / \(bits) bit")
        onStateChange?(true, true, "\(rate) Hz · \(channelCount) ch")

        let restStart = headerData.index(after: newline)
        let rest = headerData[restStart...]
        headerData.removeAll(keepingCapacity: true)
        if !rest.isEmpty { ingestPCM(Data(rest)) }
    }

    // MARK: - PCM to BlackHole

    private func ingestPCM(_ data: Data) {
        guard !data.isEmpty else { return }
        lastDataTime = Date()
        updateLevel(data)

        let bytesPerFrame = channels * MemoryLayout<Int16>.size
        pcmData.append(data)
        let bufferBytes = framesPerBuffer * bytesPerFrame
        while pcmData.count >= bufferBytes {
            if playing && pendingBuffers >= targetPendingBuffers {
                // Drop old audio when the output falls behind. Keeping latency bounded
                // is more useful for a live microphone than preserving every sample.
                pcmData.removeFirst(bufferBytes)
                continue
            }
            let chunk = Data(pcmData.prefix(bufferBytes))
            pcmData.removeFirst(bufferBytes)
            guard let audioBuffer = makeAudioBuffer(from: chunk) else { continue }
            guard let player else { continue }
            pendingBuffers += 1
            player.scheduleBuffer(audioBuffer) { [weak self] in
                guard let self else { return }
                self.queue.async {
                    self.pendingBuffers = max(0, self.pendingBuffers - 1)
                    self.lastCompletion = Date()
                }
            }
        }

        if !playing, pendingBuffers >= targetPendingBuffers / 2,
           let player, engine?.isRunning == true {
            player.play()
            playing = true
            lastCompletion = Date()
            onLog?("缓冲完成，开始输出到 BlackHole")
        }

        // A malformed or stalled sender must not grow memory without bound.
        let maxBytes = bufferBytes * (targetPendingBuffers + 4)
        if pcmData.count > maxBytes {
            pcmData.removeFirst(pcmData.count - bufferBytes * 4)
        }
    }

    private func makeAudioBuffer(from data: Data) -> AVAudioPCMBuffer? {
        guard let format = playFormat,
              let sourceFormat = AVAudioFormat(commonFormat: .pcmFormatInt16,
                                                sampleRate: Double(sampleRate),
                                                channels: AVAudioChannelCount(channels),
                                                interleaved: true) else { return nil }
        let frames = data.count / (channels * MemoryLayout<Int16>.size)
        guard let input = AVAudioPCMBuffer(pcmFormat: sourceFormat,
                                           frameCapacity: AVAudioFrameCount(frames)),
              let inputData = input.mutableAudioBufferList.pointee.mBuffers.mData else { return nil }
        input.frameLength = AVAudioFrameCount(frames)
        data.withUnsafeBytes { raw in
            if let base = raw.baseAddress { inputData.copyMemory(from: base, byteCount: data.count) }
        }

        guard let output = AVAudioPCMBuffer(pcmFormat: format,
                                            frameCapacity: AVAudioFrameCount(ceil(Double(frames) * 48_000.0 / Double(sampleRate)) + 2)) else {
            return nil
        }
        let converter = AVAudioConverter(from: sourceFormat, to: format)
        var supplied = false
        var conversionError: NSError?
        let status = converter?.convert(to: output, error: &conversionError) { _, inputStatus in
            if supplied {
                inputStatus.pointee = .noDataNow
                return nil
            }
            supplied = true
            inputStatus.pointee = .haveData
            return input
        }
        guard status == .haveData || status == .inputRanDry, output.frameLength > 0 else {
            if let conversionError { onLog?("⚠️ PCM 转换失败: \(conversionError.localizedDescription)") }
            return nil
        }
        return output
    }

    private func updateLevel(_ data: Data) {
        let now = Date()
        guard now.timeIntervalSince(lastLevelTime) >= 0.1 else { return }
        lastLevelTime = now
        data.withUnsafeBytes { raw in
            let samples = raw.bindMemory(to: Int16.self)
            guard !samples.isEmpty else { return }
            let stride = max(1, samples.count / 480)
            var sum: Float = 0
            var count = 0
            var index = 0
            while index < samples.count {
                sum += abs(Float(samples[index]))
                count += 1
                index += stride
            }
            onLevel?(min(1, (sum / Float(max(count, 1)) / 32768.0) * 8))
        }
    }

    // MARK: - Engine self-healing

    private func startEngineIfPossible() {
        guard engine == nil else { return }
        guard let blackHole = Self.findOutputDevice(matching: "BlackHole") else {
            onLog?("⚠️ 未找到 BlackHole 输出设备，请安装 BlackHole 2ch")
            return
        }
        let newEngine = AVAudioEngine()
        let newPlayer = AVAudioPlayerNode()
        guard let format = AVAudioFormat(commonFormat: .pcmFormatFloat32,
                                         sampleRate: 48_000,
                                         channels: 2,
                                         interleaved: false) else { return }
        newEngine.attach(newPlayer)
        newEngine.connect(newPlayer, to: newEngine.mainMixerNode, format: format)

        guard let audioUnit = newEngine.outputNode.audioUnit else {
            onLog?("❌ 无法访问 macOS 音频输出单元，未启动音频引擎")
            return
        }
        var device = blackHole
        let status = AudioUnitSetProperty(audioUnit,
                                          kAudioOutputUnitProperty_CurrentDevice,
                                          kAudioUnitScope_Global, 0,
                                          &device,
                                          UInt32(MemoryLayout<AudioDeviceID>.size))
        guard status == noErr else {
            onLog?("❌ 无法路由到 BlackHole（错误 \(status)）")
            return
        }

        newEngine.prepare()
        do {
            try newEngine.start()
            engine = newEngine
            player = newPlayer
            playFormat = format
            pendingBuffers = 0
            playing = false
            onLog?("✅ BlackHole 音频引擎已启动")
        } catch {
            onLog?("❌ BlackHole 音频引擎启动失败: \(error.localizedDescription)")
        }
    }

    private func teardownEngine() {
        player?.stop()
        engine?.stop()
        engine = nil
        player = nil
        playFormat = nil
        pendingBuffers = 0
        playing = false
    }

    private func engineNeedsRebuild() -> Bool {
        guard let engine else { return true }
        guard engine.isRunning else { return true }
        // Do not compare device IDs here. Core Audio may report a transient
        // zero/different ID while BlackHole is being re-enumerated even though
        // the running engine is still correctly routed. A false positive would
        // repeatedly tear down a healthy live stream. Missing devices and a
        // stopped engine are sufficient signals; the route is set explicitly
        // when the engine is created.
        return Self.findOutputDevice(matching: "BlackHole") == nil
    }

    private func rebuildEngine(reason: String) {
        guard Date().timeIntervalSince(lastEngineRebuild) > 5 else { return }
        lastEngineRebuild = Date()
        onLog?("⚠️ \(reason)，重建 BlackHole 音频引擎…")
        teardownEngine()
        startEngineIfPossible()
    }

    private func startWatchdog() {
        let timer = DispatchSource.makeTimerSource(queue: queue)
        timer.schedule(deadline: .now() + 2, repeating: 2)
        timer.setEventHandler { [weak self] in
            guard let self, self.running else { return }
            let active = self.headerParsed && Date().timeIntervalSince(self.lastDataTime) < 2.5
            self.onStateChange?(true, active, active ? "\(self.sampleRate) Hz · \(self.channels) ch" : nil)
            if !active { self.onLevel?(0) }
            if active && self.engineNeedsRebuild() { self.rebuildEngine(reason: "音频引擎异常停止或输出设备变化") }
            if active && self.playing && Date().timeIntervalSince(self.lastCompletion) > 3 {
                self.rebuildEngine(reason: "播放管线停滞")
            }
        }
        timer.resume()
        watchdog = timer
    }

    // MARK: - Audio device discovery

    static func findOutputDevice(matching name: String) -> AudioDeviceID? {
        var address = AudioObjectPropertyAddress(mSelector: kAudioHardwarePropertyDevices,
                                                  mScope: kAudioObjectPropertyScopeGlobal,
                                                  mElement: kAudioObjectPropertyElementMain)
        var size: UInt32 = 0
        guard AudioObjectGetPropertyDataSize(AudioObjectID(kAudioObjectSystemObject),
                                             &address, 0, nil, &size) == noErr else { return nil }
        let count = Int(size) / MemoryLayout<AudioDeviceID>.size
        var devices = [AudioDeviceID](repeating: 0, count: count)
        guard AudioObjectGetPropertyData(AudioObjectID(kAudioObjectSystemObject),
                                          &address, 0, nil, &size, &devices) == noErr else { return nil }
        for device in devices {
            guard let deviceName = deviceName(device),
                  deviceName.localizedCaseInsensitiveContains(name),
                  hasOutputChannels(device) else { continue }
            return device
        }
        return nil
    }

    private static func deviceName(_ device: AudioDeviceID) -> String? {
        var address = AudioObjectPropertyAddress(mSelector: kAudioDevicePropertyDeviceNameCFString,
                                                  mScope: kAudioObjectPropertyScopeGlobal,
                                                  mElement: kAudioObjectPropertyElementMain)
        var name: Unmanaged<CFString>?
        var size = UInt32(MemoryLayout<Unmanaged<CFString>?>.size)
        guard AudioObjectGetPropertyData(device, &address, 0, nil, &size, &name) == noErr else { return nil }
        return name?.takeUnretainedValue() as String?
    }

    private static func hasOutputChannels(_ device: AudioDeviceID) -> Bool {
        var address = AudioObjectPropertyAddress(mSelector: kAudioDevicePropertyStreamConfiguration,
                                                  mScope: kAudioObjectPropertyScopeOutput,
                                                  mElement: kAudioObjectPropertyElementMain)
        var size: UInt32 = 0
        guard AudioObjectGetPropertyDataSize(device, &address, 0, nil, &size) == noErr else { return false }
        let pointer = UnsafeMutableRawPointer.allocate(byteCount: Int(size), alignment: 8)
        defer { pointer.deallocate() }
        guard AudioObjectGetPropertyData(device, &address, 0, nil, &size, pointer) == noErr else { return false }
        let list = pointer.assumingMemoryBound(to: AudioBufferList.self)
        return Int(list.pointee.mNumberBuffers) > 0 && list.pointee.mBuffers.mNumberChannels > 0
    }
}
