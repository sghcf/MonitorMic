using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace MonitorMicWin;

/// <summary>
/// ADB 控制层（对应 Mac 版 ADBController.swift）：
/// 用安装包内置的 platform-tools adb.exe 控制显示器。
/// </summary>
sealed class AdbController
{
    public const string Pkg = "com.example.micstreamer";

    /// <summary>
    /// 内置资源所在目录。单文件发布时 AppContext.BaseDirectory 指向临时解压目录
    /// （例如 %TEMP%\.net\MonitorMic\xxx=），而内置的 adb\ 与 micstreamer.apk 是被
    /// 安装在 exe 旁边的松散文件，所以必须用 exe 本身所在的目录。
    /// </summary>
    static string BaseDir
    {
        get
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe))
            {
                var dir = Path.GetDirectoryName(exe);
                if (!string.IsNullOrEmpty(dir)) return dir;
            }
            return AppContext.BaseDirectory;
        }
    }

    static string AdbPath => Path.Combine(BaseDir, "adb", "adb.exe");

    /// <summary>运行 adb 并收集输出，带超时强杀。</summary>
    public static async Task<string> Run(string args, int timeoutMs = 15000)
    {
        try
        {
            var psi = new ProcessStartInfo(AdbPath, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = BaseDir
            };
            using var p = Process.Start(psi)!;
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            var exited = await Task.WhenAny(p.WaitForExitAsync(), Task.Delay(timeoutMs)) != null;
            if (!p.HasExited) { try { p.Kill(); } catch { } }
            return (await stdout + await stderr).Trim();
        }
        catch (Exception ex)
        {
            return "执行失败: " + ex.Message;
        }
    }

    public static Task<string> Shell(string cmd) => Run($"shell {cmd}");

    // MARK: - 连接

    public static Task<string> Connect(string ip) => Run($"connect {ip}:5555");

    public static async Task<bool> IsConnected(string ip)
    {
        var outp = await Run("devices", 8000);
        return outp.Contains($"{ip}:5555\tdevice");
    }

    // MARK: - 小爱唤醒服务

    public static async Task<bool> IsWakeupDisabled()
    {
        var outp = await Shell("pm list packages -d");
        return outp.Contains("com.xiaomi.wakeupservice");
    }

    public static Task<string> SetWakeupEnabled(bool enabled) =>
        Shell(enabled
            ? "pm enable com.xiaomi.wakeupservice"
            : "pm disable-user --user 0 com.xiaomi.wakeupservice");

    // MARK: - MicStreamer App

    public static async Task<bool> IsAppInstalled()
    {
        var outp = await Shell($"pm list packages {Pkg}");
        return outp.Contains(Pkg);
    }

    public static Task<string> InstallApk(string apkPath) => Run($"install -r \"{apkPath}\"", 90000);

    public static Task<string> GrantRecordPermission() =>
        Shell($"pm grant {Pkg} android.permission.RECORD_AUDIO");

    public static async Task<bool> IsServiceRunning()
    {
        var outp = await Shell($"pidof {Pkg}");
        return !string.IsNullOrWhiteSpace(outp);
    }

    /// <summary>v1.2.0 服务器模式：无需目标参数，客户端自行连接显示器的 50010 端口。</summary>
    public static Task<string> StartService() =>
        Shell($"am start-foreground-service -n {Pkg}/.MicService");

    public static Task<string> StopService() => Shell($"am force-stop {Pkg}");

    // MARK: - 工具

    /// <summary>本机到达指定远程 IP 所用的局域网地址（UDP 路由探测，不真的发包）。</summary>
    public static string? LocalIpTowards(string remoteIp)
    {
        try
        {
            using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            s.Connect(remoteIp, 5555);
            return ((IPEndPoint)s.LocalEndPoint!).Address.ToString();
        }
        catch { return null; }
    }

    /// <summary>安装包内置的 micstreamer.apk 路径。</summary>
    public static string BundledApkPath =>
        Path.Combine(BaseDir, "micstreamer.apk");
}
