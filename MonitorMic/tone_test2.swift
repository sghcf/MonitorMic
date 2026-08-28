import AVFoundation
import AudioToolbox

// 精确复刻 App 的调度模式：小缓冲 + 完成回调计数 + 延迟 play()
func findOutputDevice(matching name: String) -> AudioDeviceID? {
    var addr = AudioObjectPropertyAddress(mSelector: kAudioHardwarePropertyDevices,
                                          mScope: kAudioObjectPropertyScopeGlobal,
                                          mElement: kAudioObjectPropertyElementMain)
    var size: UInt32 = 0
    guard AudioObjectGetPropertyDataSize(AudioObjectID(kAudioObjectSystemObject), &addr, 0, nil, &size) == noErr else { return nil }
    var devices = [AudioDeviceID](repeating: 0, count: Int(size) / MemoryLayout<AudioDeviceID>.size)
    guard AudioObjectGetPropertyData(AudioObjectID(kAudioObjectSystemObject), &addr, 0, nil, &size, &devices) == noErr else { return nil }
    for dev in devices {
        var nameAddr = AudioObjectPropertyAddress(mSelector: kAudioDevicePropertyDeviceNameCFString,
                                                  mScope: kAudioObjectPropertyScopeGlobal,
                                                  mElement: kAudioObjectPropertyElementMain)
        var cfName: Unmanaged<CFString>?
        var nsz = UInt32(MemoryLayout<Unmanaged<CFString>?>.size)
        if AudioObjectGetPropertyData(dev, &nameAddr, 0, nil, &nsz, &cfName) == noErr,
           let cf = cfName?.takeUnretainedValue(), (cf as String).contains(name) {
            return dev
        }
    }
    return nil
}

let engine = AVAudioEngine()
let player = AVAudioPlayerNode()
engine.attach(player)
let fmt = AVAudioFormat(commonFormat: .pcmFormatFloat32, sampleRate: 48000, channels: 2, interleaved: false)!
engine.connect(player, to: engine.mainMixerNode, format: fmt)

if let bh = findOutputDevice(matching: "BlackHole") {
    var dev = bh
    let st = AudioUnitSetProperty(engine.outputNode.audioUnit!,
                                  kAudioOutputUnitProperty_CurrentDevice,
                                  kAudioUnitScope_Global, 0, &dev,
                                  UInt32(MemoryLayout<AudioDeviceID>.size))
    print("设置输出设备: \(st == noErr ? "OK" : "失败")")
}

engine.prepare()
try engine.start()
print("引擎已启动，isRunning=\(engine.isRunning)")

// 模拟 App：后台线程以小缓冲实时喂数据，攒够 5 个再 play()
let q = DispatchQueue(label: "test.feed", qos: .userInteractive)
let lock = NSLock()
var pending = 0
var playing = false
var phase = 0.0

q.async {
    let framesPerBuf = 1024
    while true {
        lock.lock()
        let p = pending
        lock.unlock()
        if playing && p > 10 {
            Thread.sleep(forTimeInterval: 0.005)
            continue
        }
        guard let buf = AVAudioPCMBuffer(pcmFormat: fmt, frameCapacity: AVAudioFrameCount(framesPerBuf)) else { break }
        buf.frameLength = AVAudioFrameCount(framesPerBuf)
        for i in 0..<framesPerBuf {
            phase += 2.0 * Double.pi * 440.0 / 48000.0
            let v = Float(sin(phase)) * 0.5
            buf.floatChannelData![0][i] = v
            buf.floatChannelData![1][i] = v
        }
        lock.lock(); pending += 1; lock.unlock()
        player.scheduleBuffer(buf) {
            lock.lock(); pending -= 1; lock.unlock()
        }
        if !playing && pending >= 5 {
            player.play()
            playing = true
            print("player.play() 已调用，player isPlaying=\(player.isPlaying)")
        }
        Thread.sleep(forTimeInterval: 0.021) // 模拟实时流
    }
}

// 3 秒后打印状态
Thread.sleep(forTimeInterval: 3)
print("3秒后: engine.isRunning=\(engine.isRunning) player.isPlaying=\(player.isPlaying) pending=\(pending)")
print("输出节点格式: \(engine.outputNode.outputFormat(forBus: 0))")
Thread.sleep(forTimeInterval: 7)
print("完成")
