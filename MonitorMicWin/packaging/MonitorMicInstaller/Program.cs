using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using Microsoft.Win32;

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
        StopExistingInstallation(installDir);
        StopLegacyInstallations(installDir);
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

        var driver = Path.Combine(installDir, "driver", "VBCABLE_Setup_x64.exe");
        if (File.Exists(driver))
        {
            var driverAnswer = MessageBox.Show(
                "MonitorMic 需要 VB-CABLE 虚拟声卡才能作为系统麦克风使用。现在安装吗？\n随后 Windows 可能显示 UAC 提示。",
                ProductName,
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Information);
            if (driverAnswer == DialogResult.Yes)
            {
                InstallDriver(driver);
            }
        }

        WriteLog("文件、快捷方式和启动项安装完成。 ");
        MessageBox.Show(
            "MonitorMic 安装完成。\n如果刚安装 VB-CABLE 后 Windows 要求重启，请重启后再使用。",
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

    private static void VerifyPayload()
    {
        var verifyDir = Path.Combine(Path.GetTempPath(), "MonitorMicVerify-" + Guid.NewGuid().ToString("N"));
        try
        {
            ExtractPayload(verifyDir);
            var required = new[]
            {
                Path.Combine(verifyDir, "MonitorMic.exe"),
                Path.Combine(verifyDir, "micstreamer.apk"),
                Path.Combine(verifyDir, "adb", "adb.exe"),
                Path.Combine(verifyDir, "driver", "VBCABLE_Setup_x64.exe"),
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

    private static void InstallDriver(string driver)
    {
        try
        {
            WriteLog("启动 VB-CABLE 安装程序：" + driver);
            using var process = Process.Start(new ProcessStartInfo(driver)
            {
                WorkingDirectory = Path.GetDirectoryName(driver)!,
                UseShellExecute = true,
                Verb = "runas"
            });
            process?.WaitForExit();
            WriteLog("VB-CABLE 安装程序退出码：" + process?.ExitCode);
        }
        catch (Exception ex)
        {
            WriteLog("VB-CABLE 安装未完成：" + ex.Message);
            MessageBox.Show(
                "VB-CABLE 没有完成安装，MonitorMic 程序文件仍已安装。\n你可以稍后右键管理员运行 driver\\VBCABLE_Setup_x64.exe。",
                ProductName,
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
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
