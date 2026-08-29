using System.Reflection;
using Forms = System.Windows.Forms;
using System.Windows;

namespace MonitorMicWin;

/// <summary>WPF application host with a lightweight Win32 notification-area icon.</summary>
sealed class TrayApp : System.Windows.Application
{
    const string ShowEventName = @"Local\MonitorMicWin.ShowWindow";
    readonly Forms.NotifyIcon tray;
    readonly MainWindow window;
    readonly AppState state = new();
    readonly Forms.ToolStripMenuItem statusItem;
    bool quitting;

    public TrayApp(bool startMinimized)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        DispatcherUnhandledException += (_, e) =>
        {
            Log.Info("UI 异常: " + e.Exception);
            e.Handled = true;
        };

        window = new MainWindow(state);
        window.Closing += (_, e) =>
        {
            if (!quitting)
            {
                e.Cancel = true;
                window.Hide();
            }
        };

        statusItem = new Forms.ToolStripMenuItem("MonitorMic 未连接") { Enabled = false };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(statusItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        var healItem = new Forms.ToolStripMenuItem("一键修复并启动串流");
        healItem.Click += async (_, _) => await state.HealAll();
        menu.Items.Add(healItem);
        menu.Items.Add("打开主面板", null, (_, _) => ShowForm());
        var autoStartItem = new Forms.ToolStripMenuItem("登录后自动启动")
        {
            Checked = AutoStart.IsEnabled,
            CheckOnClick = true
        };
        autoStartItem.CheckedChanged += (_, _) => AutoStart.SetEnabled(autoStartItem.Checked);
        menu.Items.Add(autoStartItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(new Forms.ToolStripMenuItem($"版本 v{Program.Version}") { Enabled = false });
        menu.Items.Add("退出", null, async (_, _) => await QuitAsync());

        tray = new Forms.NotifyIcon
        {
            Icon = LoadAppIcon(),
            Text = $"MonitorMic v{Program.Version}",
            ContextMenuStrip = menu,
            Visible = true
        };
        tray.DoubleClick += (_, _) => ShowForm();

        state.Pipeline.OnState += (running, streaming, info) =>
        {
            Dispatcher.BeginInvoke(() =>
            {
                statusItem.Text = streaming ? $"串流中  {info}"
                    : running ? "接收器运行中，等待连接" : "接收器未启动";
                tray.Text = Truncate($"MonitorMic — {(streaming ? "串流中" : "空闲")}", 63);
            });
        };

        state.StartPolling();
        Log.Info($"MonitorMic for Windows v{Program.Version} 已启动");
        if (!startMinimized) ShowForm();
        else tray.ShowBalloonTip(4000, "MonitorMic 已在后台运行", "双击托盘图标打开主面板。", Forms.ToolTipIcon.Info);

        var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        var signalThread = new Thread(() =>
        {
            while (!quitting)
            {
                showSignal.WaitOne();
                if (!quitting) Dispatcher.BeginInvoke(ShowForm);
            }
        }) { IsBackground = true, Name = "show-signal" };
        signalThread.Start();

        Task.Run(async () =>
        {
            await state.RefreshDependencies();
            await state.Connect();
            for (var i = 0; i < 10 && !state.AdbConnected; i++) await Task.Delay(500);
            if (state.AutoHeal && state.AdbConnected && !state.Pipeline.Streaming) await state.HealAll();
        });
    }

    static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    void ShowForm()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(ShowForm); return; }
        window.Show();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
    }

    async Task QuitAsync()
    {
        if (quitting) return;
        quitting = true;
        window.AllowClose = true;
        try
        {
            await state.ShutdownAsync();
        }
        catch (Exception ex)
        {
            Log.Info("退出清理失败: " + ex.Message);
            AdbController.ForceKillOwnedProcesses();
        }
        tray.Visible = false;
        tray.Dispose();
        window.Close();
        Shutdown();
    }

    public static System.Drawing.Icon LoadAppIcon()
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("MonitorMicWin.app.ico");
            if (stream != null) return new System.Drawing.Icon(stream);
        }
        catch { }
        return System.Drawing.SystemIcons.Application;
    }
}
