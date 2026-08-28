using System.Diagnostics;

namespace MonitorMicWin;

/// <summary>
/// 主面板：显示器连接控制（adb）+ 音频接收状态 + VB-CABLE 管理 + 日志。
/// 所有 adb/网络操作都在后台线程，UI 定时器只读缓存值（高刷屏友好）。
/// </summary>
sealed class MainForm : Form
{
    public bool ReallyQuit; // 托盘退出时置 true，否则关闭按钮只隐藏到托盘

    readonly AppState state;
    readonly AudioPipeline pipeline;

    readonly TextBox ipBox;
    readonly Label connLabel;
    readonly Button connectBtn;
    readonly Button healBtn;

    readonly Label wakeupStatus; readonly Button wakeupBtn;
    readonly Label appStatus; readonly Button appBtn;
    readonly Label svcStatus; readonly Button svcBtn;
    readonly Label rxStatus; readonly Button rxBtn;

    readonly LevelMeter levelMeter;
    readonly Label liveLabel;
    readonly Label deviceLabel;
    readonly Button cableButton;
    readonly Button toneButton;
    readonly CheckBox autoStartCheck;
    readonly CheckBox autoHealCheck;
    readonly TextBox logBox;
    readonly System.Windows.Forms.Timer uiTimer;
    float currentLevel;

    public MainForm(AppState state)
    {
        this.state = state;
        pipeline = state.Pipeline;

        Text = "MonitorMic";
        Icon = TrayApp.LoadAppIcon();
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(560, 720);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        Font = new Font("Microsoft YaHei UI", 9F);

        int y = 12;
        Controls.Add(new Label
        {
            Text = $"MonitorMic for Windows  v{Program.Version}",
            Font = new Font("Microsoft YaHei UI", 12F, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(16, y)
        });
        y += 36;

        // ---- 连接区 ----
        Controls.Add(new Label { Text = "显示器 IP", AutoSize = true, Location = new Point(16, y + 4) });
        ipBox = new TextBox { Text = state.MonitorIP, Location = new Point(86, y), Width = 130 };
        Controls.Add(ipBox);
        connectBtn = new Button { Text = "连接", Location = new Point(224, y - 1), Width = 64, Height = 27 };
        connectBtn.Click += async (_, _) => { state.MonitorIP = ipBox.Text.Trim(); state.SaveConfig(); await state.Connect(); };
        Controls.Add(connectBtn);
        healBtn = new Button
        {
            Text = "一键修复并启动",
            Location = new Point(296, y - 1),
            Width = 110,
            Height = 27,
            BackColor = Color.FromArgb(0xED, 0x7D, 0x31),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat
        };
        healBtn.Click += async (_, _) => { state.MonitorIP = ipBox.Text.Trim(); state.SaveConfig(); await state.HealAll(); };
        Controls.Add(healBtn);
        connLabel = new Label { Text = "未连接", AutoSize = true, Location = new Point(416, y + 4), ForeColor = Color.Gray };
        Controls.Add(connLabel);
        y += 40;

        // ---- 状态行 ----
        MakeRow("小爱远场唤醒", "mic_off", y, out wakeupStatus, out wakeupBtn);
        wakeupBtn.Click += async (_, _) => await state.ToggleWakeup();
        y += 42;
        MakeRow("MicStreamer App", "apk", y, out appStatus, out appBtn);
        appBtn.Click += async (_, _) => await state.InstallApp();
        y += 42;
        MakeRow("串流服务", "svc", y, out svcStatus, out svcBtn);
        svcBtn.Click += async (_, _) =>
        {
            if (state.ServiceRunning) await state.StopStreaming();
            else await state.StartStreaming();
        };
        y += 42;
        MakeRow("音频接收器（→ CABLE Input）", "rx", y, out rxStatus, out rxBtn);
        rxBtn.Click += (_, _) => state.ToggleReceiver();
        y += 46;

        // ---- 电平表 ----
        Controls.Add(new Label { Text = "麦克风电平", AutoSize = true, Location = new Point(16, y) });
        levelMeter = new LevelMeter { Location = new Point(104, y - 2), Width = 396 };
        Controls.Add(levelMeter);
        liveLabel = new Label
        {
            Text = "LIVE",
            ForeColor = Color.White,
            BackColor = Color.Firebrick,
            AutoSize = true,
            Location = new Point(508, y - 1),
            Visible = false
        };
        Controls.Add(liveLabel);
        y += 30;

        // ---- 虚拟声卡 ----
        deviceLabel = new Label { Text = "虚拟声卡: 检测中…", AutoSize = true, Location = new Point(16, y + 5) };
        Controls.Add(deviceLabel);
        cableButton = new Button { Text = "安装 VB-CABLE", Location = new Point(310, y), Width = 110, Height = 27 };
        cableButton.Click += (_, _) => InstallCable();
        Controls.Add(cableButton);
        toneButton = new Button { Text = "🔔 播放测试音", Location = new Point(428, y), Width = 116, Height = 27 };
        toneButton.Click += (_, _) => Task.Run(() => pipeline.PlayTestTone());
        Controls.Add(toneButton);
        y += 40;

        // ---- 开关 ----
        autoStartCheck = new CheckBox
        {
            Text = "开机自动启动（登录后常驻托盘）",
            AutoSize = true,
            Location = new Point(16, y),
            Checked = AutoStart.IsEnabled
        };
        autoStartCheck.CheckedChanged += (_, _) =>
        {
            if (autoStartCheck.Checked != AutoStart.IsEnabled)
                AutoStart.SetEnabled(autoStartCheck.Checked);
        };
        Controls.Add(autoStartCheck);
        autoHealCheck = new CheckBox
        {
            Text = "断链自动修复",
            AutoSize = true,
            Location = new Point(280, y),
            Checked = state.AutoHeal
        };
        autoHealCheck.CheckedChanged += (_, _) => { state.AutoHeal = autoHealCheck.Checked; state.SaveConfig(); };
        Controls.Add(autoHealCheck);
        y += 32;

        // ---- 日志 ----
        Controls.Add(new Label { Text = "运行日志", AutoSize = true, Location = new Point(16, y) });
        y += 22;
        logBox = new TextBox
        {
            Location = new Point(16, y),
            Width = 528,
            Height = 720 - y - 14,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Font = new Font("Consolas", 8.5F),
            BackColor = Color.FromArgb(0x12, 0x12, 0x1e),
            ForeColor = Color.FromArgb(0xd0, 0xd0, 0xe0)
        };
        Controls.Add(logBox);

        // ---- 事件 ----
        Log.OnLine += line =>
        {
            try
            {
                BeginInvoke(() =>
                {
                    logBox.AppendText(line + Environment.NewLine);
                    if (logBox.TextLength > 20000) logBox.Text = logBox.Text[^10000..];
                });
            }
            catch { }
        };
        pipeline.OnLevel += lv => currentLevel = lv;
        pipeline.OnState += (_, _, _) => SafeRefreshUi();
        state.Changed += SafeRefreshUi;

        // UI 刷新定时器：只读缓存值，不做 IO / COM 枚举（高刷屏流畅的关键）
        uiTimer = new System.Windows.Forms.Timer { Interval = 100 };
        uiTimer.Tick += (_, _) =>
        {
            levelMeter.Level = currentLevel;
            currentLevel *= 0.92f;
            liveLabel.Visible = pipeline.Streaming;
            deviceLabel.Text = "虚拟声卡: " + (pipeline.DeviceName
                ?? (pipeline.CableInstalledNow ? "已安装" : "❌ 未安装 VB-CABLE"));
            cableButton.Visible = !pipeline.CableInstalledNow;
        };
        uiTimer.Start();

        RefreshUi();
    }

    void MakeRow(string title, string tag, int y, out Label status, out Button btn)
    {
        var panel = new Panel
        {
            Location = new Point(16, y),
            Width = 528,
            Height = 36,
            BackColor = Color.FromArgb(0xF2, 0xF2, 0xF5)
        };
        var titleLabel = new Label
        {
            Text = title,
            Font = new Font(Font, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(10, 9)
        };
        status = new Label { Text = "…", AutoSize = true, Location = new Point(220, 10), ForeColor = Color.Gray };
        btn = new Button { Location = new Point(436, 5), Width = 84, Height = 26, Tag = tag };
        panel.Controls.Add(titleLabel);
        panel.Controls.Add(status);
        panel.Controls.Add(btn);
        Controls.Add(panel);
    }

    void SafeRefreshUi()
    {
        try { BeginInvoke(RefreshUi); } catch { }
    }

    void RefreshUi()
    {
        var adbOk = state.AdbConnected;
        connLabel.Text = adbOk ? $"已连接 {state.DeviceModel}" : "未连接";
        connLabel.ForeColor = adbOk ? Color.ForestGreen : Color.Firebrick;

        wakeupStatus.Text = state.WakeupDisabled ? "已禁用（麦克风可用）" : "运行中（占用麦克风）";
        wakeupStatus.ForeColor = state.WakeupDisabled ? Color.ForestGreen : Color.Firebrick;
        wakeupBtn.Text = state.WakeupDisabled ? "恢复" : "禁用";

        appStatus.Text = state.AppInstalled ? "已安装" : "未安装";
        appStatus.ForeColor = state.AppInstalled ? Color.ForestGreen : Color.Gray;
        appBtn.Text = state.AppInstalled ? "重新安装" : "安装";

        svcStatus.Text = state.ServiceRunning ? "运行中（端口 50010）" : "已停止";
        svcStatus.ForeColor = state.ServiceRunning ? Color.ForestGreen : Color.Gray;
        svcBtn.Text = state.ServiceRunning ? "停止" : "启动";

        var streaming = pipeline.Streaming;
        rxStatus.Text = !pipeline.Running ? "已停止"
            : streaming ? $"接收中 {pipeline.StreamInfo}"
            : "连接中/等待数据";
        rxStatus.ForeColor = streaming ? Color.ForestGreen : (pipeline.Running ? Color.DarkOrange : Color.Gray);
        rxBtn.Text = pipeline.Running ? "停止" : "启动";

        var busy = state.Busy;
        connectBtn.Enabled = healBtn.Enabled = !busy;
        wakeupBtn.Enabled = appBtn.Enabled = svcBtn.Enabled = !busy && adbOk;
    }

    void InstallCable()
    {
        // 优先用安装包内置的完整驱动包（含 .inf/.sys/.cat，单独运行 setup 会报缺少 inf）
        var bundled = Path.Combine(AdbController.InstallBaseDirectory, "driver", "VBCABLE_Setup_x64.exe");
        try
        {
            if (File.Exists(bundled))
            {
                Log.Info("启动内置 VB-CABLE 安装程序（会弹 UAC，请允许；装完需重启电脑）…");
                Process.Start(new ProcessStartInfo(bundled) { UseShellExecute = true, Verb = "runas" });
            }
            else
            {
                Log.Info("未找到内置驱动安装包，打开官网下载页…");
                Process.Start(new ProcessStartInfo("https://vb-audio.com/Cable/") { UseShellExecute = true });
            }
        }
        catch (Exception ex)
        {
            Log.Info("启动安装程序失败: " + ex.Message);
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!ReallyQuit && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;   // 关闭按钮 = 隐藏到托盘
            Hide();
            return;
        }
        base.OnFormClosing(e);
    }
}
