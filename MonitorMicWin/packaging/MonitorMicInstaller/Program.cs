using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using Microsoft.Win32;
using NAudio.CoreAudioApi;

namespace MonitorMicInstaller;

internal static class Program
{
    private const string ProductName = "MonitorMic";
    private const string AdbServerPort = "5038";
    private static string Version => Assembly.GetExecutingAssembly()
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "未注入版本";

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Any(arg => string.Equals(arg, "--verify", StringComparison.OrdinalIgnoreCase)))
            {
                VerifyPayload();
                return 0;
            }

            if (args.Any(arg => string.Equals(arg, "--uninstall", StringComparison.OrdinalIgnoreCase)))
            {
                ApplicationConfiguration.Initialize();
                Uninstall();
                return 0;
            }

            ApplicationConfiguration.Initialize();
            Install();
            return 0;
        }
        catch (Exception ex)
        {
            WriteLog("安装失败：" + ex);
            try
            {
                MessageBox.Show(
                    "MonitorMic 安装失败：" + ex.Message + Environment.NewLine +
                    "详细日志：" + Path.Combine(Path.GetTempPath(), "MonitorMic-install.log"),
                    ProductName,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            catch
            {
                // The installer must still return a failure code if the UI cannot start.
            }
            return 1;
        }
    }

    private static void Install()
    {
        var installDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs",
            ProductName);
        var exe = Path.Combine(installDir, "MonitorMic.exe");

        var answer = MessageBox.Show(
            $"现在安装 {ProductName} Windows {Version} 到：\n{installDir}\n\n继续吗？",
            ProductName,
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Information);
        if (answer != DialogResult.Yes)
        {
            WriteLog("用户取消安装。 ");
            return;
        }

        WriteLog("开始安装到：" + installDir);
        ShowDependencyNotice();
        StopExistingInstallation(installDir);
        StopLegacyInstallations(installDir);
        RemoveLegacyInstallations(installDir);
        ExtractPayload(installDir);
        if (!File.Exists(exe))
        {
            throw new InvalidDataException("安装包中缺少 MonitorMic.exe，安装已停止。 ");
        }
        WriteLog("主程序已复制：" + exe);

        CreateShortcuts(installDir, exe);
        ConfigureAutoStart(exe);
        CopyUninstaller(installDir);
        RegisterUninstall(installDir, exe);
        CreateUninstallShortcut(installDir);

        WriteLog("文件、快捷方式和启动项安装完成。 ");
        MessageBox.Show(
            "MonitorMic 安装完成。\n首次运行时请按界面提示检查 ADB 和 VB-CABLE。缺少依赖时可从官方页面手动安装。",
            ProductName,
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
        Process.Start(new ProcessStartInfo(exe)
        {
            WorkingDirectory = installDir,
            UseShellExecute = true
        });
    }

    private static string InstallDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs",
        ProductName);

    private static void StopExistingInstallation(string installDir)
    {
        var oldExe = Path.Combine(installDir, "MonitorMic.exe");
        foreach (var process in Process.GetProcessesByName("MonitorMic"))
        {
            try
            {
                var path = process.MainModule?.FileName;
                if (path != null && string.Equals(Path.GetFullPath(path), Path.GetFullPath(oldExe), StringComparison.OrdinalIgnoreCase))
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(5000);
                }
            }
            catch { }
            finally { process.Dispose(); }
        }

        StopBundledAdb(Path.Combine(installDir, "adb", "adb.exe"));
    }

    private static void StopLegacyInstallations(string installDir)
    {
        // v2.0.3 installed into ...\MonitorMic\2.0.3. Stop its processes too,
        // otherwise its old adb.exe can remain locked while the new fixed-root
        // installation is being created.
        if (!Directory.Exists(installDir)) return;
        foreach (var legacyDir in Directory.GetDirectories(installDir))
        {
            try { StopExistingInstallation(legacyDir); } catch { }
        }
    }

    private static void RemoveLegacyInstallations(string installDir)
    {
        if (!Directory.Exists(installDir)) return;
        foreach (var legacyDir in Directory.GetDirectories(installDir))
        {
            var name = Path.GetFileName(legacyDir);
            if (!System.Text.RegularExpressions.Regex.IsMatch(name, @"^\d+\.\d+\.\d+$")) continue;
            try
            {
                Directory.Delete(legacyDir, recursive: true);
                WriteLog("已清理旧版本目录：" + legacyDir);
            }
            catch (Exception ex)
            {
                WriteLog("旧版本目录暂未清理（可能仍被占用）: " + ex.Message);
            }
        }
    }

    private static void CopyUninstaller(string installDir)
    {
        var source = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(source)) throw new InvalidOperationException("无法定位当前安装器。");
        File.Copy(source, Path.Combine(installDir, "Uninstall.exe"), overwrite: true);
    }

    private static void RegisterUninstall(string installDir, string exe)
    {
        var uninstaller = Path.Combine(installDir, "Uninstall.exe");
        using var key = Registry.CurrentUser.CreateSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Uninstall\MonitorMic");
        key?.SetValue("DisplayName", ProductName);
        key?.SetValue("DisplayVersion", Version);
        key?.SetValue("Publisher", "MonitorMic");
        key?.SetValue("InstallLocation", installDir);
        key?.SetValue("DisplayIcon", exe);
        key?.SetValue("UninstallString", $"\"{uninstaller}\" --uninstall");
        key?.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key?.SetValue("NoRepair", 1, RegistryValueKind.DWord);
    }

    private static void Uninstall()
    {
        var installDir = InstallDirectory;
        var answer = MessageBox.Show(
            $"确定卸载 {ProductName}？\n\n将删除：\n{installDir}\n\n不会自动卸载 VB-CABLE 虚拟声卡。",
            ProductName,
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning);
        if (answer != DialogResult.Yes)
        {
            WriteLog("用户取消卸载。 ");
            return;
        }

        RemoveAutoStart();
        StopExistingInstallation(installDir);
        StopLegacyInstallations(installDir);
        DeleteShortcuts();
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(
                @"Software\Microsoft\Windows\CurrentVersion\Uninstall\MonitorMic", throwOnMissingSubKey: false);
        }
        catch (Exception ex) { WriteLog("删除卸载注册信息失败: " + ex.Message); }

        // The uninstaller runs from inside installDir. Remove the directory
        // after this process exits through a detached hidden PowerShell task.
        var script = $"Start-Sleep -Milliseconds 700; Remove-Item -LiteralPath {QuotePowerShell(installDir)} -Recurse -Force -ErrorAction SilentlyContinue";
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -WindowStyle Hidden -EncodedCommand {encoded}")
        {
            UseShellExecute = false,
            CreateNoWindow = true
        });
        MessageBox.Show("MonitorMic 已开始卸载。", ProductName, MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private static string QuotePowerShell(string value) => "'" + value.Replace("'", "''") + "'";

    private static void RemoveAutoStart()
    {
        using var key = Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
        key?.DeleteValue(ProductName, throwOnMissingValue: false);
    }

    private static void DeleteShortcuts()
    {
        var startMenuDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs");
        var startMenu = Path.Combine(startMenuDir, "MonitorMic.lnk");
        var uninstallShortcut = Path.Combine(startMenuDir, "卸载 MonitorMic.lnk");
        var desktop = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "MonitorMic.lnk");
        foreach (var path in new[] { startMenu, uninstallShortcut, desktop })
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }

    private static void CreateUninstallShortcut(string installDir)
    {
        var startMenuDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            "Programs");
        CreateShortcut(
            Path.Combine(startMenuDir, "卸载 MonitorMic.lnk"),
            installDir,
            Path.Combine(installDir, "Uninstall.exe"));
    }

    private static void StopBundledAdb(string adbPath)
    {
        if (!File.Exists(adbPath)) return;
        try
        {
            using var process = Process.Start(new ProcessStartInfo(adbPath, "kill-server")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(adbPath)!,
                Environment = { ["ADB_SERVER_PORT"] = AdbServerPort }
            });
            process?.WaitForExit(5000);
        }
        catch { }

        foreach (var process in Process.GetProcessesByName("adb"))
        {
            try
            {
                var path = process.MainModule?.FileName;
                if (path != null && string.Equals(Path.GetFullPath(path), Path.GetFullPath(adbPath), StringComparison.OrdinalIgnoreCase))
                    process.Kill(entireProcessTree: true);
            }
            catch { }
            finally { process.Dispose(); }
        }
    }

    private static void ShowDependencyNotice()
    {
        var adb = ProbeAdb();
        var cable = ProbeCable();
        var message = $"电脑依赖预检：\nADB：{adb.Description}\nVB-CABLE：{(cable ? "已检测到" : "尚未安装或未检测到")}\n\n安装程序不会自动安装这些第三方组件。安装完成后可在 MonitorMic 中打开官方来源并重新检测。";
        if (!adb.Available)
        {
            var answer = MessageBox.Show(
                message + "\n\n是否打开 ADB 官方安装页面？",
                ProductName,
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Information);
            if (answer == DialogResult.Yes)
                Process.Start(new ProcessStartInfo("https://developer.android.com/tools/releases/platform-tools") { UseShellExecute = true });
        }
        if (!cable)
        {
            var answer = MessageBox.Show(
                message + "\n\n是否打开 VB-CABLE 官方安装页面？",
                ProductName,
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Information);
            if (answer == DialogResult.Yes)
                Process.Start(new ProcessStartInfo("https://vb-audio.com/Cable/") { UseShellExecute = true });
        }
        if (adb.Available && cable)
            MessageBox.Show(message, ProductName, MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private static AdbDependency ProbeAdb()
    {
        var candidates = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(path => Path.Combine(path.Trim(), "adb.exe"))
            .Concat(new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Android", "Sdk", "platform-tools", "adb.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Android", "platform-tools", "adb.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Android", "platform-tools", "adb.exe")
            });
        foreach (var path in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(path)) continue;
            try
            {
                using var process = Process.Start(new ProcessStartInfo(path, "version")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetDirectoryName(path)!
                });
                if (process == null) continue;
                var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
                process.WaitForExit(5000);
                if (process.ExitCode == 0 && output.Contains("Android Debug Bridge version", StringComparison.OrdinalIgnoreCase))
                    return new AdbDependency(true, output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "可用");
            }
            catch { }
        }
        return new AdbDependency(false, "尚未安装或版本检查失败");
    }

    private static bool ProbeCable()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                try
                {
                    if (device.FriendlyName.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                finally { device.Dispose(); }
            }
            return false;
        }
        catch { return false; }
    }

    private readonly record struct AdbDependency(bool Available, string Description);

    private static void VerifyPayload()
    {
        var verifyDir = Path.Combine(Path.GetTempPath(), "MonitorMicVerify-" + Guid.NewGuid().ToString("N"));
        try
        {
            ExtractPayload(verifyDir);
            var required = new[]
            {
                Path.Combine(verifyDir, "MonitorMic.exe"),
                Path.Combine(verifyDir, "THIRD_PARTY_NOTICES.txt")
            };
            foreach (var path in required)
            {
                if (!File.Exists(path))
                {
                    throw new InvalidDataException("payload 校验失败，缺少：" + path);
                }
            }
        }
        finally
        {
            if (Directory.Exists(verifyDir))
            {
                Directory.Delete(verifyDir, recursive: true);
            }
        }
    }

    private static void ExtractPayload(string destination)
    {
        Directory.CreateDirectory(destination);
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("MonitorMicPayload.zip")
            ?? throw new InvalidDataException("安装包内找不到 MonitorMicPayload.zip。 ");
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        var root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        foreach (var entry in archive.Entries)
        {
            var relative = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
            var target = Path.GetFullPath(Path.Combine(destination, relative));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("payload 路径非法：" + entry.FullName);
            }

            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(target);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var input = entry.Open();
            using var output = File.Create(target);
            input.CopyTo(output);
        }
    }

    private static void CreateShortcuts(string installDir, string exe)
    {
        var startMenuDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            "Programs");
        Directory.CreateDirectory(startMenuDir);
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        CreateShortcut(Path.Combine(startMenuDir, "MonitorMic.lnk"), installDir, exe);
        CreateShortcut(Path.Combine(desktop, "MonitorMic.lnk"), installDir, exe);
    }

    private static void CreateShortcut(string shortcutPath, string workingDirectory, string exe)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("Windows 快捷方式组件不可用。 ");
        dynamic shell = Activator.CreateInstance(shellType)
            ?? throw new InvalidOperationException("无法创建 Windows 快捷方式组件。 ");
        dynamic shortcut = shell.CreateShortcut(shortcutPath);
        shortcut.TargetPath = exe;
        shortcut.WorkingDirectory = workingDirectory;
        shortcut.IconLocation = exe;
        shortcut.Save();
    }

    private static void ConfigureAutoStart(string exe)
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        key?.SetValue(ProductName, $"\"{exe}\" --minimized");
    }

    private static void WriteLog(string message)
    {
        try
        {
            File.AppendAllText(
                Path.Combine(Path.GetTempPath(), "MonitorMic-install.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
        }
        catch
        {
            // Logging must never prevent installation.
        }
    }
}
