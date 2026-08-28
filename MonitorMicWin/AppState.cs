using Microsoft.Win32;
using System.Net;

namespace MonitorMicWin;

/// <summary>用户配置持久化（HKCU\Software\MonitorMic）。</summary>
static class Config
{
    const string KeyPath = @"Software\MonitorMic";

    public static string MonitorIP
    {
        get => Registry.GetValue(@"HKEY_CURRENT_USER\" + KeyPath, "MonitorIP", null) as string ?? "";
        set { using var k = Registry.CurrentUser.CreateSubKey(KeyPath); k.SetValue("MonitorIP", value); }
    }

    public static bool AutoHeal
    {
        get => (Registry.GetValue(@"HKEY_CURRENT_USER\" + KeyPath, "AutoHeal", 1) as int?) == 1;
        set { using var k = Registry.CurrentUser.CreateSubKey(KeyPath); k.SetValue("AutoHeal", value ? 1 : 0); }
    }
}

/// <summary>全局应用状态（对应 Mac 版 AppState.swift）：连接/服务/接收器/一键修复/轮询。</summary>
sealed class AppState
{
    public readonly AudioPipeline Pipeline = new();

    public string MonitorIP { get; set; }

    public volatile bool AdbConnected;
    public volatile bool WakeupDisabled;
    public volatile bool AppInstalled;
    public volatile bool ServiceRunning;
    public volatile bool Busy;
    public string DeviceModel = "";
    public bool AutoHeal;

    /// <summary>状态变化（任意线程触发，UI 需 BeginInvoke）。</summary>
    public event Action? Changed;

    System.Threading.Timer? pollTimer;
    bool refreshing;

    public AppState()
    {
        MonitorIP = Config.MonitorIP;
        if (string.IsNullOrWhiteSpace(MonitorIP)) MonitorIP = "192.168.100.7";
        AutoHeal = Config.AutoHeal;
    }

    public void SaveConfig()
    {
        Config.MonitorIP = MonitorIP;
        Config.AutoHeal = AutoHeal;
    }

    public void StartPolling()
    {
        pollTimer = new System.Threading.Timer(async _ =>
        {
            if (Busy || refreshing) return;
            refreshing = true;
            try { await Refresh(); }
            catch { }
            finally { refreshing = false; }
        }, null, 3000, 3000);
    }

    public async Task Refresh()
    {
        var connected = await AdbController.IsConnected(MonitorIP);
        AdbConnected = connected;
        if (connected)
        {
            if (DeviceModel.Length == 0)
                DeviceModel = (await AdbController.Shell("getprop ro.product.model")).Trim();
            WakeupDisabled = await AdbController.IsWakeupDisabled();
            AppInstalled = await AdbController.IsAppInstalled();
            var was = ServiceRunning;
            ServiceRunning = await AdbController.IsServiceRunning();
            // 自动修复：接收器开着但服务掉了 → 自动重启
            if (AutoHeal && Pipeline.Running && was && !ServiceRunning && AppInstalled)
            {
                Log.Info("⚠️ 检测到串流服务中断，自动重启…");
                await StartStreaming();
            }
        }
        else
        {
            DeviceModel = "";
            ServiceRunning = false;
        }
        Changed?.Invoke();
    }

    // MARK: - 高层动作

    public async Task Connect()
    {
        Busy = true;
        try
        {
            if (!IPAddress.TryParse(MonitorIP, out _))
            {
                Log.Info($"❌ 显示器 IP 无效: {MonitorIP}");
                return;
            }
            Log.Info($"连接显示器 {MonitorIP}:5555 …");
            var outp = await AdbController.Connect(MonitorIP);
            Log.Info(outp.Length > 0 ? outp : "已发送连接命令");
            await Refresh();
        }
        finally { Busy = false; }
    }

    public async Task ToggleWakeup()
    {
        Busy = true;
        try
        {
            if (WakeupDisabled)
            {
                Log.Info("恢复小爱远场唤醒 …");
                Log.Info(await AdbController.SetWakeupEnabled(true));
            }
            else
            {
                Log.Info("禁用小爱远场唤醒（释放麦克风）…");
                Log.Info(await AdbController.SetWakeupEnabled(false));
            }
            await Task.Delay(800);
            await Refresh();
        }
        finally { Busy = false; }
    }

    public async Task InstallApp()
    {
        Busy = true;
        try
        {
            await InstallAppInner();
            await Refresh();
        }
        finally { Busy = false; }
    }

    async Task InstallAppInner()
    {
        var apk = AdbController.BundledApkPath;
        if (!File.Exists(apk)) { Log.Info("❌ 找不到内置 micstreamer.apk"); return; }
        Log.Info("安装 MicStreamer 到显示器 …");
        Log.Info(await AdbController.InstallApk(apk));
        Log.Info("授予录音权限 …");
        Log.Info(await AdbController.GrantRecordPermission());
    }

    public async Task StartStreaming()
    {
        Log.Info("启动串流服务（服务器模式 :50010）…");
        Log.Info(await AdbController.StartService());
        await Task.Delay(1500);
        await Refresh();
    }

    public async Task StopStreaming()
    {
        Log.Info("停止串流服务 …");
        Log.Info(await AdbController.StopService());
        await Refresh();
    }

    public void ToggleReceiver()
    {
        if (Pipeline.Running)
        {
            Pipeline.Stop();
            Log.Info("音频接收器已停止");
        }
        else
        {
            Pipeline.Start(MonitorIP);
        }
        Changed?.Invoke();
    }

    /// <summary>一键修复：连接 → 释放麦克风 → 安装/授权 → 启动 Android 服务 → 启动 Windows 接收器</summary>
    public async Task HealAll()
    {
        Busy = true;
        try
        {
            Log.Info("——— 一键修复开始 ———");
            if (!IPAddress.TryParse(MonitorIP, out _))
            {
                Log.Info($"❌ 显示器 IP 无效: {MonitorIP}");
                return;
            }
            if (!AdbConnected)
            {
                await AdbController.Connect(MonitorIP);
                await Refresh();
                if (!AdbConnected) { Log.Info("❌ 无法连接显示器，请检查 IP 和网络"); return; }
            }
            if (!WakeupDisabled)
            {
                Log.Info(await AdbController.SetWakeupEnabled(false));
                await Task.Delay(800);
                WakeupDisabled = await AdbController.IsWakeupDisabled();
            }
            if (!AppInstalled) await InstallAppInner();
            else
            {
                Log.Info("校验 MicStreamer 录音权限 …");
                Log.Info(await AdbController.GrantRecordPermission());
            }
            await StartStreaming();
            if (!Pipeline.Running) Pipeline.Start(MonitorIP);
            Log.Info("——— 一键修复完成 ———");
        }
        finally
        {
            Busy = false;
            Changed?.Invoke();
        }
    }
}
