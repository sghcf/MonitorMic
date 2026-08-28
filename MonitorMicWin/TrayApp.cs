using System.Reflection;

namespace MonitorMicWin;

/// <summary>托盘宿主：图标、右键菜单、主面板显隐、管线与应用状态生命周期。</summary>
sealed class TrayApp : ApplicationContext
{
    readonly NotifyIcon tray;
    readonly MainForm form;
    readonly AppState state = new();
    readonly ToolStripMenuItem statusItem;
    readonly ToolStripMenuItem autoStartItem;

    public TrayApp(bool startMinimized)
    {
        form = new MainForm(state);
        _ = form.Handle; // 提前创建句柄，保证后台线程能 BeginInvoke

        statusItem = new ToolStripMenuItem("MonitorMic 未连接") { Enabled = false };
        autoStartItem = new ToolStripMenuItem("开机自动启动")
        {
            Checked = AutoStart.IsEnabled,
            CheckOnClick = true
        };
        autoStartItem.CheckedChanged += (_, _) =>
        {
            if (autoStartItem.Checked != AutoStart.IsEnabled)
                AutoStart.SetEnabled(autoStartItem.Checked);
        };

        var healItem = new ToolStripMenuItem("一键修复并启动串流");
        healItem.Click += async (_, _) => await state.HealAll();

        var menu = new ContextMenuStrip();
        menu.Items.Add(statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(healItem);
        menu.Items.Add("打开主面板", null, (_, _) => ShowForm());
        menu.Items.Add(autoStartItem);
        menu.Items.Add(new ToolStripSeparator());
        var verItem = new ToolStripMenuItem($"版本 v{Program.Version}") { Enabled = false };
        menu.Items.Add(verItem);
        menu.Items.Add("退出", null, (_, _) => Quit());

        tray = new NotifyIcon
        {
            Icon = LoadAppIcon(),
            Text = $"MonitorMic v{Program.Version}",
            ContextMenuStrip = menu,
            Visible = true
        };
        tray.DoubleClick += (_, _) => ShowForm();

        state.Pipeline.OnState += (running, streaming, info) =>
        {
            try
            {
                form.BeginInvoke(() =>
                {
                    statusItem.Text = streaming ? $"串流中  {info}"
                        : running ? "接收器运行中，等待连接"
                        : "接收器未启动";
                    tray.Text = Truncate($"MonitorMic — {(streaming ? "串流中" : "空闲")}", 63);
                });
            }
            catch { }
        };

        state.StartPolling();
        Log.Info($"MonitorMic for Windows v{Program.Version} 已启动");

        if (!startMinimized)
        {
            ShowForm();
        }
        else
        {
            // 托盘气泡提示，避免用户以为"没反应"
            tray.ShowBalloonTip(4000, "MonitorMic 已在后台运行",
                "双击此图标打开主面板。", ToolTipIcon.Info);
        }

        // 启动后自动恢复串流（配合开机自启，实现"登录即可用"）
        Task.Run(async () =>
        {
            await state.Connect();
            for (int i = 0; i < 10 && !state.AdbConnected; i++)
                await Task.Delay(500);
            if (state.AutoHeal && state.AdbConnected && !state.Pipeline.Streaming)
                await state.HealAll();
        });
    }

    static string Truncate(string s, int n) => s.Length <= n ? s : s[..n];

    void ShowForm()
    {
        form.Show();
        form.WindowState = FormWindowState.Normal;
        form.Activate();
    }

    /// <summary>供"第二实例"信号线程调用：安全弹到 UI 线程显示主窗口。</summary>
    public void RequestShow()
    {
        try
        {
            if (form.IsHandleCreated && form.InvokeRequired)
                form.BeginInvoke(() => ShowForm());
            else
                ShowForm();
        }
        catch { }
    }

    void Quit()
    {
        form.ReallyQuit = true;
        state.Pipeline.Dispose();
        tray.Visible = false;
        tray.Dispose();
        Application.Exit();
    }

    /// <summary>从嵌入资源加载应用图标（单文件发布也有效）。</summary>
    public static Icon LoadAppIcon()
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            using var stream = asm.GetManifestResourceStream("MonitorMicWin.app.ico");
            if (stream != null) return new Icon(stream);
        }
        catch { }
        return SystemIcons.Application;
    }
}
