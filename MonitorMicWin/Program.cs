using System.Reflection;
using Forms = System.Windows.Forms;

namespace MonitorMicWin;

static class Program
{
    const string MutexName = @"Local\MonitorMicWin.SingleInstance";
    const string ShowEventName = @"Local\MonitorMicWin.ShowWindow";

    public static string Version =>
        Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "未注入版本";

    [STAThread]
    static void Main(string[] args)
    {
        // 单实例：第二个实例 → 通知已运行实例弹出主窗口，然后退出
        using var mutex = new Mutex(true, MutexName, out var created);
        if (!created)
        {
            try { EventWaitHandle.OpenExisting(ShowEventName).Set(); } catch { }
            return;
        }

        // 全局异常兜底：任何崩溃都写日志并弹窗，绝不"无声无息没反应"
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            var msg = e.ExceptionObject?.ToString() ?? "未知错误";
            try { Log.Info("致命错误: " + msg); } catch { }
            try
            {
                Forms.MessageBox.Show(
                    "MonitorMic 遇到错误已退出。\n日志: %LocalAppData%\\MonitorMic\\monitor-mic.log\n\n" + msg,
                    "MonitorMic", Forms.MessageBoxButtons.OK, Forms.MessageBoxIcon.Error);
            }
            catch { }
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => AdbController.ForceKillOwnedProcesses();

        try
        {
            var minimized = args.Contains("--minimized");
            var app = new TrayApp(minimized);
            app.Run();
        }
        catch (Exception ex)
        {
            try { Log.Info("启动失败: " + ex); } catch { }
            Forms.MessageBox.Show("MonitorMic 启动失败:\n\n" + ex, "MonitorMic",
                Forms.MessageBoxButtons.OK, Forms.MessageBoxIcon.Error);
        }
    }
}
