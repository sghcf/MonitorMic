using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;

namespace MonitorMicWin;

/// <summary>
/// ADB 控制层（对应 Mac 版 ADBController.swift）：
/// 用安装包内置的 platform-tools adb.exe 控制显示器。
/// </summary>
sealed class AdbController
{
    public const string Pkg = "com.example.micstreamer";
    // Keep MonitorMic's ADB daemon isolated from other ADB users and stop it
    // when the app exits so adb.exe is not left locking the install directory.
    const string AdbServerPort = "5038";

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

    static string? embeddedResourceDir;

    static string ResourceDir
    {
        get
        {
            if (embeddedResourceDir != null) return embeddedResourceDir;
            if (File.Exists(Path.Combine(BaseDir, "adb", "adb.exe"))
                && File.Exists(Path.Combine(BaseDir, "micstreamer.apk"))
                && File.Exists(Path.Combine(BaseDir, "driver", "VBCABLE_Setup_x64.exe")))
                return BaseDir;

            var assembly = Assembly.GetExecutingAssembly();
            var resourceNames = assembly.GetManifestResourceNames();
            const string prefix = "MonitorMic.Payload.";
            if (!resourceNames.Any(name => name.StartsWith(prefix, StringComparison.Ordinal)))
                return BaseDir;

            var root = Path.Combine(Path.GetTempPath(), "MonitorMic", "embedded", Environment.ProcessId.ToString());
            Directory.CreateDirectory(root);
            foreach (var name in resourceNames)
            {
                string? relative = name switch
                {
                    "MonitorMic.Payload.Apk" => "micstreamer.apk",
                    _ when name.StartsWith(prefix + "Adb.", StringComparison.Ordinal) =>
                        Path.Combine("adb", name[(prefix + "Adb.").Length..]),
                    _ when name.StartsWith(prefix + "Driver.", StringComparison.Ordinal) =>
                        Path.Combine("driver", name[(prefix + "Driver.").Length..]),
                    _ => null
                };
                if (relative == null) continue;
                var target = Path.Combine(root, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using var input = assembly.GetManifestResourceStream(name)
                    ?? throw new InvalidDataException("内置资源无法读取: " + name);
                using var output = File.Create(target);
                input.CopyTo(output);
            }
            embeddedResourceDir = root;
            return root;
        }
    }

    static string AdbPath => Path.Combine(ResourceDir, "adb", "adb.exe");

    public static string InstallBaseDirectory => ResourceDir;
    public static string BundledAdbPath => AdbPath;

    /// <summary>运行 adb 并收集输出，带超时强杀。</summary>
    public static async Task<string> Run(string args, int timeoutMs = 15000)
    {
        try
        {
            if (!File.Exists(AdbPath))
                return $"执行失败: 找不到 adb.exe（{AdbPath}）";

            var psi = new ProcessStartInfo(AdbPath, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = BaseDir
            };
            psi.Environment["ADB_SERVER_PORT"] = AdbServerPort;
            using var p = Process.Start(psi);
            if (p == null) return "执行失败: 无法启动 adb.exe";
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            var wait = p.WaitForExitAsync();
            if (await Task.WhenAny(wait, Task.Delay(timeoutMs)) != wait)
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                await Task.WhenAny(wait, Task.Delay(1000));
                return $"执行超时（{timeoutMs}ms）: {args}";
            }
            await wait;
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
        if (!File.Exists(AdbPath)) return;
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
        CleanupEmbeddedResources();
    }

    /// <summary>Crash/process-exit fallback; does not wait on async work.</summary>
    public static void ForceKillOwnedProcesses()
    {
        var path = Path.GetFullPath(AdbPath);
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

    static void CleanupEmbeddedResources()
    {
        var root = embeddedResourceDir;
        embeddedResourceDir = null;
        if (root == null) return;
        try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
        catch { }
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

    /// <summary>安装包内置的 micstreamer.apk 路径。</summary>
    public static string BundledApkPath =>
        Path.Combine(ResourceDir, "micstreamer.apk");
}
