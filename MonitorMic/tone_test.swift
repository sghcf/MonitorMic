import AVFoundation
import AudioToolbox

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

let mode = CommandLine.arguments.count > 1 ? CommandLine.arguments[1] : "before"

if mode == "before", let bh = findOutputDevice(matching: "BlackHole") {
    var dev = bh
    let st = AudioUnitSetProperty(engine.outputNode.audioUnit!,
                                  kAudioOutputUnitProperty_CurrentDevice,
                                  kAudioUnitScope_Global, 0, &dev,
                                  UInt32(MemoryLayout<AudioDeviceID>.size))
    print("start 前设置设备: \(st == noErr ? "OK" : "失败 \(st)")")
}

engine.prepare()
try engine.start()

if mode == "after", let bh = findOutputDevice(matching: "BlackHole") {
    var dev = bh
    let st = AudioUnitSetProperty(engine.outputNode.audioUnit!,
                                  kAudioOutputUnitProperty_CurrentDevice,
                                  kAudioUnitScope_Global, 0, &dev,
                                  UInt32(MemoryLayout<AudioDeviceID>.size))
    print("start 后设置设备: \(st == noErr ? "OK" : "失败 \(st)")")
}

// 播放 440Hz 音调 8 秒
let frames = 48000 * 8
guard let buf = AVAudioPCMBuffer(pcmFormat: fmt, frameCapacity: AVAudioFrameCount(frames)) else { exit(1) }
buf.frameLength = AVAudioFrameCount(frames)
for i in 0..<frames {
    let v = Float(sin(2.0 * Double.pi * 440.0 * Double(i) / 48000.0)) * 0.5
    buf.floatChannelData![0][i] = v
    buf.floatChannelData![1][i] = v
}
player.play()
player.scheduleBuffer(buf)
print("播放中(\(mode) 模式)…")
Thread.sleep(forTimeInterval: 8)
print("完成")
