using System.Reflection;

namespace MonitorMicWin;

static class Program
{
    const string MutexName = @"Local\MonitorMicWin.SingleInstance";
    const string ShowEventName = @"Local\MonitorMicWin.ShowWindow";

    public static string Version =>
        Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "1.2.2";

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

        ApplicationConfiguration.Initialize(); // 已含 HighDpiMode=SystemAware

        // 全局异常兜底：任何崩溃都写日志并弹窗，绝不"无声无息没反应"
        Application.ThreadException += (_, e) => Log.Info("UI 异常: " + e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            var msg = e.ExceptionObject?.ToString() ?? "未知错误";
            try { Log.Info("致命错误: " + msg); } catch { }
            try
            {
                MessageBox.Show(
                    "MonitorMic 遇到错误已退出。\n日志: %LocalAppData%\\MonitorMic\\monitor-mic.log\n\n" + msg,
                    "MonitorMic", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch { }
        };

        try
        {
            var minimized = args.Contains("--minimized");
            var app = new TrayApp(minimized);

            // 监听"第二个实例请求显示主窗口"信号
            var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
            var t = new Thread(() =>
            {
                while (true)
                {
                    showSignal.WaitOne();
                    try { app.RequestShow(); } catch { }
                }
            })
            { IsBackground = true, Name = "show-signal" };
            t.Start();

            Application.Run(app);
        }
        catch (Exception ex)
        {
            try { Log.Info("启动失败: " + ex); } catch { }
            MessageBox.Show("MonitorMic 启动失败:\n\n" + ex, "MonitorMic",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
