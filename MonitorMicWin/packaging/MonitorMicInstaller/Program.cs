using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using Microsoft.Win32;

namespace MonitorMicInstaller;

internal static class Program
{
    private const string ProductName = "MonitorMic";
    private const string Version = "1.2.1";
    private const string InstallFolder = "1.2.1-output-fix";

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
            ProductName,
            InstallFolder);
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
        ExtractPayload(installDir);
        if (!File.Exists(exe))
        {
            throw new InvalidDataException("安装包中缺少 MonitorMic.exe，安装已停止。 ");
        }
        WriteLog("主程序已复制：" + exe);

        CreateShortcuts(installDir, exe);
        ConfigureAutoStart(exe);

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
