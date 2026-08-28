import Foundation
import Network
import AVFoundation
import AudioToolbox

/// A bounded, thread-safe stereo Float32 ring buffer. The network queue writes
/// samples while AVAudioSourceNode's real-time render thread reads them.
private final class StereoFloatRingBuffer {
    private let capacityFrames: Int
    private var samples: [Float]
    private var readFrame = 0
    private var writeFrame = 0
    private var storedFrames = 0
    private let lock = NSLock()

    init(capacityFrames: Int) {
        self.capacityFrames = max(1, capacityFrames)
        self.samples = Array(repeating: 0, count: max(1, capacityFrames) * 2)
    }

    var availableFrames: Int {
        lock.lock()
        defer { lock.unlock() }
        return storedFrames
    }

    func clear() {
        lock.lock()
        readFrame = 0
        writeFrame = 0
        storedFrames = 0
        lock.unlock()
    }

    func append(left: Float, right: Float) {
        lock.lock()
        if storedFrames == capacityFrames {
            readFrame = (readFrame + 1) % capacityFrames
            storedFrames -= 1
        }
        let offset = writeFrame * 2
        samples[offset] = left
        samples[offset + 1] = right
        writeFrame = (writeFrame + 1) % capacityFrames
        storedFrames += 1
        lock.unlock()
    }

    func append(_ buffer: AVAudioPCMBuffer) {
        guard let channels = buffer.floatChannelData else { return }
        let frames = Int(buffer.frameLength)
        guard frames > 0 else { return }
        let left = channels[0]
        let right = buffer.format.channelCount > 1 ? channels[1] : channels[0]
        for frame in 0..<frames {
            append(left: left[frame], right: right[frame])
        }
    }

    func render(to audioBufferList: UnsafeMutablePointer<AudioBufferList>, frameCount: Int) {
        let buffers = UnsafeMutableAudioBufferListPointer(audioBufferList)
        guard frameCount > 0 else { return }

        lock.lock()
        let framesToRead = min(frameCount, storedFrames)
        for frame in 0..<frameCount {
            let offset = frame * 2
            let left: Float
            let right: Float
            if frame < framesToRead {
                let ringOffset = readFrame * 2
                left = samples[ringOffset]
                right = samples[ringOffset + 1]
                readFrame = (readFrame + 1) % capacityFrames
                storedFrames -= 1
            } else {
                left = 0
                right = 0
            }

            if buffers.count >= 2 {
                buffers[0].mData?.assumingMemoryBound(to: Float.self)[frame] = left
                buffers[1].mData?.assumingMemoryBound(to: Float.self)[frame] = right
            } else if buffers.count == 1 {
                buffers[0].mData?.assumingMemoryBound(to: Float.self)[offset] = left
                buffers[0].mData?.assumingMemoryBound(to: Float.self)[offset + 1] = right
            }
        }
        lock.unlock()
    }
}

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
    private var pcmReadOffset = 0
    private var headerParsed = false
    private var lastDataTime = Date.distantPast
    private var lastLevelTime = Date.distantPast
    private var lastEngineRebuild = Date.distantPast

    private var outputUnit: AudioUnit?
    private var playFormat: AVAudioFormat?
    private var converter: AVAudioConverter?
    private var converterSourceFormat: AVAudioFormat?
    private var converterSourceKey: String?
    private var didAnnounceOutput = false
    private var watchdog: DispatchSourceTimer?
    private let audioRing = StereoFloatRingBuffer(capacityFrames: 48_000 * 2)

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
            guard self.isOutputUnitRunning else {
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
        pcmReadOffset = 0
        headerParsed = false
        didAnnounceOutput = false
        audioRing.clear()
        converter = nil
        converterSourceFormat = nil
        converterSourceKey = nil
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
        // Wait until the display has sent a valid stream header before creating
        // the Core Audio graph. Starting it before the Android service is ready
        // can leave AVAudioEngine stopped during device startup and trigger a
        // needless rebuild loop.
        startEngineIfPossible()

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
        while pcmData.count - pcmReadOffset >= bufferBytes {
            let end = pcmReadOffset + bufferBytes
            let chunk = Data(pcmData[pcmReadOffset..<end])
            pcmReadOffset = end
            appendAudioChunk(chunk)
        }

        if !didAnnounceOutput,
           audioRing.availableFrames >= framesPerBuffer * (targetPendingBuffers / 2),
           isOutputUnitRunning {
            didAnnounceOutput = true
            onLog?("缓冲完成，开始输出到 BlackHole")
        }

        // A malformed or stalled sender must not grow memory without bound.
        let maxBytes = bufferBytes * (targetPendingBuffers + 4)
        if pcmData.count - pcmReadOffset > maxBytes {
            let keepStart = max(0, pcmData.count - bufferBytes * 4)
            pcmData = Data(pcmData[keepStart...])
            pcmReadOffset = 0
        } else if pcmReadOffset >= 64 * 1024 || pcmReadOffset * 2 >= pcmData.count {
            // Avoid Data.removeFirst on every 21 ms block. Compact only after a
            // meaningful prefix has been consumed, which keeps allocation/copy
            // pressure low during a long-running stream.
            pcmData.removeSubrange(0..<pcmReadOffset)
            pcmReadOffset = 0
        }
    }

    private func appendAudioChunk(_ data: Data) {
        let frames = data.count / (channels * MemoryLayout<Int16>.size)
        guard frames > 0 else { return }

        // The Android service currently publishes 48 kHz, 16-bit mono/stereo
        // PCM. Convert that common path directly into the bounded ring buffer.
        if sampleRate == 48_000, (channels == 1 || channels == 2) {
            data.withUnsafeBytes { raw in
                let bytes = raw.bindMemory(to: UInt8.self)
                for frame in 0..<frames {
                    let offset = frame * channels * MemoryLayout<Int16>.size
                    let leftBits = UInt16(bytes[offset]) | (UInt16(bytes[offset + 1]) << 8)
                    let left = Float(Int16(bitPattern: leftBits)) / 32_768.0
                    let right: Float
                    if channels == 1 {
                        right = left
                    } else {
                        let rightBits = UInt16(bytes[offset + 2]) | (UInt16(bytes[offset + 3]) << 8)
                        right = Float(Int16(bitPattern: rightBits)) / 32_768.0
                    }
                    audioRing.append(left: left, right: right)
                }
            }
            return
        }

        guard let format = playFormat,
              let sourceFormat = AVAudioFormat(commonFormat: .pcmFormatInt16,
                                                sampleRate: Double(sampleRate),
                                                channels: AVAudioChannelCount(channels),
                                                interleaved: true) else { return }
        guard let input = AVAudioPCMBuffer(pcmFormat: sourceFormat,
                                           frameCapacity: AVAudioFrameCount(frames)),
              let inputData = input.mutableAudioBufferList.pointee.mBuffers.mData else { return }
        input.frameLength = AVAudioFrameCount(frames)
        data.withUnsafeBytes { raw in
            if let base = raw.baseAddress { inputData.copyMemory(from: base, byteCount: data.count) }
        }

        guard let output = AVAudioPCMBuffer(pcmFormat: format,
                                            frameCapacity: AVAudioFrameCount(ceil(Double(frames) * 48_000.0 / Double(sampleRate)) + 2)) else {
            return
        }
        let sourceKey = "\(sampleRate):\(channels)"
        if converterSourceKey != sourceKey {
            converterSourceFormat = sourceFormat
            converterSourceKey = sourceKey
            converter = AVAudioConverter(from: sourceFormat, to: format)
        }
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
            return
        }
        audioRing.append(output)
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
        guard outputUnit == nil else { return }
        guard let blackHole = Self.findOutputDevice(matching: "BlackHole") else {
            onLog?("⚠️ 未找到 BlackHole 输出设备，请安装 BlackHole 2ch")
            return
        }
        var description = AudioComponentDescription(componentType: kAudioUnitType_Output,
                                                    componentSubType: kAudioUnitSubType_HALOutput,
                                                    componentManufacturer: kAudioUnitManufacturer_Apple,
                                                    componentFlags: 0,
                                                    componentFlagsMask: 0)
        guard let component = AudioComponentFindNext(nil, &description) else {
            onLog?("❌ 无法创建 macOS 音频输出单元")
            return
        }
        var unit: AudioUnit?
        guard AudioComponentInstanceNew(component, &unit) == noErr, let unit else {
            onLog?("❌ 无法实例化 macOS 音频输出单元")
            return
        }

        var disableInput: UInt32 = 0
        _ = AudioUnitSetProperty(unit, kAudioOutputUnitProperty_EnableIO,
                                 kAudioUnitScope_Input, 1,
                                 &disableInput, UInt32(MemoryLayout<UInt32>.size))
        var device = blackHole
        let deviceStatus = AudioUnitSetProperty(unit,
                                                 kAudioOutputUnitProperty_CurrentDevice,
                                                 kAudioUnitScope_Global, 0,
                                                 &device,
                                                 UInt32(MemoryLayout<AudioDeviceID>.size))
        guard deviceStatus == noErr else {
            onLog?("❌ 无法路由到 BlackHole（错误 \(deviceStatus)）")
            AudioComponentInstanceDispose(unit)
            return
        }

        guard let format = AVAudioFormat(commonFormat: .pcmFormatFloat32,
                                         sampleRate: 48_000,
                                         channels: 2,
                                         interleaved: false) else {
            AudioComponentInstanceDispose(unit)
            return
        }
        var streamDescription = format.streamDescription.pointee
        let formatStatus = AudioUnitSetProperty(unit,
                                                 kAudioUnitProperty_StreamFormat,
                                                 kAudioUnitScope_Input, 0,
                                                 &streamDescription,
                                                 UInt32(MemoryLayout<AudioStreamBasicDescription>.size))
        guard formatStatus == noErr else {
            onLog?("❌ 无法设置 BlackHole 音频格式（错误 \(formatStatus)）")
            AudioComponentInstanceDispose(unit)
            return
        }

        var callback = AURenderCallbackStruct(inputProc: Self.renderCallback,
                                               inputProcRefCon: Unmanaged.passUnretained(self).toOpaque())
        let callbackStatus = AudioUnitSetProperty(unit,
                                                  kAudioUnitProperty_SetRenderCallback,
                                                  kAudioUnitScope_Input, 0,
                                                  &callback,
                                                  UInt32(MemoryLayout<AURenderCallbackStruct>.size))
        guard callbackStatus == noErr else {
            onLog?("❌ 无法设置 BlackHole 音频回调（错误 \(callbackStatus)）")
            AudioComponentInstanceDispose(unit)
            return
        }

        let initializeStatus = AudioUnitInitialize(unit)
        guard initializeStatus == noErr else {
            onLog?("❌ BlackHole 音频输出初始化失败（错误 \(initializeStatus)）")
            AudioComponentInstanceDispose(unit)
            return
        }
        outputUnit = unit
        playFormat = format
        let startStatus = AudioOutputUnitStart(unit)
        guard startStatus == noErr else {
            onLog?("❌ BlackHole 音频输出启动失败（错误 \(startStatus)）")
            teardownEngine()
            return
        }
        onLog?("✅ BlackHole 音频引擎已启动")
    }

    private func teardownEngine() {
        if let outputUnit {
            _ = AudioOutputUnitStop(outputUnit)
            _ = AudioUnitUninitialize(outputUnit)
            AudioComponentInstanceDispose(outputUnit)
        }
        outputUnit = nil
        playFormat = nil
        converter = nil
        converterSourceFormat = nil
        converterSourceKey = nil
        audioRing.clear()
    }

    private var isOutputUnitRunning: Bool {
        guard let outputUnit else { return false }
        var running: UInt32 = 0
        var size = UInt32(MemoryLayout<UInt32>.size)
        let status = AudioUnitGetProperty(outputUnit,
                                          kAudioOutputUnitProperty_IsRunning,
                                          kAudioUnitScope_Global, 0,
                                          &running, &size)
        return status == noErr && running != 0
    }

    private static let renderCallback: AURenderCallback = { refCon, _, _, _, frameCount, audioBufferList in
        guard let audioBufferList else { return noErr }
        let receiver = Unmanaged<AudioReceiver>.fromOpaque(refCon).takeUnretainedValue()
        receiver.audioRing.render(to: audioBufferList, frameCount: Int(frameCount))
        return noErr
    }

    private func engineNeedsRebuild() -> Bool {
        guard outputUnit != nil else { return true }
        guard isOutputUnitRunning else { return true }
        // Do not query the Core Audio device list on every watchdog tick. During
        // normal device enumeration that list can briefly be empty, which used
        // to make a healthy engine look broken and caused a teardown/rebuild
        // loop every few seconds. The route is set explicitly at startup; a
        // stopped engine is the reliable signal that it needs rebuilding.
        return false
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
