using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace MonitorMicWin;

/// <summary>
/// ADB 控制层（对应 Mac 版 ADBController.swift）：
/// 使用系统已安装的 platform-tools adb.exe 控制显示器。
/// </summary>
sealed class AdbController
{
    public const string Pkg = "com.example.micstreamer";
    // Keep MonitorMic's ADB daemon isolated from other ADB users and stop it
    // when the app exits so adb.exe is not left locking the install directory.
    const string AdbServerPort = "5038";

    /// <summary>
    /// 程序目录仅用于定位自身文件。ADB 由用户安装；程序始终通过独立端口
    /// 运行自己的 ADB server，不接管用户的默认 ADB 会话。
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

    static string? verifiedAdbPath;
    static string? AdbPath => Volatile.Read(ref verifiedAdbPath) ?? WindowsDependencyProbe.FindAdb();

    public static string InstallBaseDirectory => BaseDir;
    public static string? ResolvedAdbPath => AdbPath;

    public static async Task<AdbDependencyStatus> ProbeAsync()
    {
        string? lastError = null;
        foreach (var path in WindowsDependencyProbe.FindAdbCandidates())
        {
            var result = await RunAtPath(path, "version", 8000).ConfigureAwait(false);
            if (result.ExitCode == 0 && WindowsDependencyProbe.IsAdbVersionOutputValid(result.Output))
            {
                Volatile.Write(ref verifiedAdbPath, path);
                var version = result.Output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault(line => line.Contains("Android Debug Bridge version", StringComparison.OrdinalIgnoreCase))
                    ?.Trim() ?? "ADB 可用";
                return new AdbDependencyStatus(true, path, version, "");
            }
            if (!string.IsNullOrWhiteSpace(result.Output)) lastError = result.Output;
        }
        Volatile.Write(ref verifiedAdbPath, null);
        return AdbDependencyStatus.Missing(string.IsNullOrWhiteSpace(lastError)
            ? "未找到可用 adb.exe"
            : "已找到 adb.exe，但版本检查失败：" + lastError);
    }

    /// <summary>运行 adb 并收集输出，带超时强杀。</summary>
    public static async Task<string> Run(string args, int timeoutMs = 15000)
    {
        try
        {
            var adbPath = AdbPath;
            if (adbPath == null)
                return "执行失败: 尚未安装 ADB（请安装 Android Platform-Tools 后点击重新检测）";
            return (await RunAtPath(adbPath, args, timeoutMs).ConfigureAwait(false)).Output;
        }
        catch (Exception ex)
        {
            return "执行失败: " + ex.Message;
        }
    }

    static async Task<(string Output, int ExitCode)> RunAtPath(string adbPath, string args, int timeoutMs)
    {
        try
        {
            var psi = new ProcessStartInfo(adbPath, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(adbPath) ?? BaseDir
            };
            psi.Environment["ADB_SERVER_PORT"] = AdbServerPort;
            var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(userHome))
            {
                psi.Environment["HOME"] = userHome;
                psi.Environment["USERPROFILE"] = userHome;
                psi.Environment["ANDROID_USER_HOME"] = Path.Combine(userHome, ".android");
            }
            using var p = Process.Start(psi);
            if (p == null) return ("执行失败: 无法启动 adb.exe", -1);
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            var wait = p.WaitForExitAsync();
            if (await Task.WhenAny(wait, Task.Delay(timeoutMs)).ConfigureAwait(false) != wait)
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                await Task.WhenAny(wait, Task.Delay(1000)).ConfigureAwait(false);
                return ($"执行超时（{timeoutMs}ms）: {args}", -1);
            }
            await wait.ConfigureAwait(false);
            return ((await stdout.ConfigureAwait(false) + await stderr.ConfigureAwait(false)).Trim(), p.ExitCode);
        }
        catch (Exception ex)
        {
            return ("执行失败: " + ex.Message, -1);
        }
    }

    public static Task<string> Shell(string cmd) => Run($"shell {cmd}");

    // MARK: - 连接

    public static Task<string> Connect(string ip) => Run($"connect {ip}:5555");

    public static async Task<bool> IsConnected(string ip)
    {
        var outp = await Run("devices", 8000);
        return outp.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries).Any(line =>
            line.TrimStart().StartsWith($"{ip}:5555", StringComparison.Ordinal)
            && line.Contains("\tdevice", StringComparison.Ordinal));
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

    /// <summary>服务器模式：无需目标参数，客户端自行连接显示器的 50010 端口。</summary>
    public static Task<string> StartService() =>
        Shell($"am start-foreground-service -n {Pkg}/.MicService");

    public static Task<string> StopService() => Shell($"am force-stop {Pkg}");

    /// <summary>Stop the ADB daemon owned by this application.</summary>
    public static async Task ShutdownAsync()
    {
        var adbPath = AdbPath;
        if (adbPath == null) return;
        try
        {
            var result = await Run("kill-server", 8000).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(result)) Log.Info("ADB 清理: " + result);
        }
        catch (Exception ex)
        {
            Log.Info("ADB 服务清理失败: " + ex.Message);
        }
        ForceKillOwnedProcesses();
    }

    /// <summary>Crash/process-exit fallback; does not wait on async work.</summary>
    public static void ForceKillOwnedProcesses()
    {
        var adbPath = AdbPath;
        if (adbPath == null) return;
        var path = Path.GetFullPath(adbPath);
        foreach (var process in Process.GetProcessesByName("adb"))
        {
            try
            {
                var executable = process.MainModule?.FileName;
                if (executable != null && string.Equals(Path.GetFullPath(executable), path, StringComparison.OrdinalIgnoreCase))
                    process.Kill(entireProcessTree: true);
            }
            catch
            {
                // The process may exit between enumeration and inspection.
            }
            finally
            {
                process.Dispose();
            }
        }
    }

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

}
