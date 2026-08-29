using System.Collections.ObjectModel;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using MediaBrushes = System.Windows.Media.Brushes;

namespace MonitorMicWin;

/// <summary>Responsive WPF control panel. I/O stays in AppState/AudioPipeline.</summary>
public partial class MainWindow : Window
{
    const int MaxDisplayedLogs = 500;
    const int MaxPendingLogs = 1000;
    static readonly float[] GainValues = { 1f, 2f, 4f, 8f, 12f, 16f };

    readonly AppState state;
    readonly AudioPipeline pipeline;
    readonly DispatcherTimer timer;
    readonly ConcurrentQueue<string> pendingLogs = new();
    readonly ObservableCollection<string> displayedLogs = new();
    bool initializing;
    float currentLevel;

    public bool AllowClose { get; set; }

    internal MainWindow(AppState state)
    {
        InitializeComponent();
        this.state = state;
        pipeline = state.Pipeline;
        VersionText.Text = $"v{Program.Version}";
        IpBox.Text = state.MonitorIP;
        ApkPathBox.Text = state.SelectedApkPath;
        LogList.ItemsSource = displayedLogs;

        initializing = true;
        foreach (var value in GainValues) GainCombo.Items.Add($"增益 {value:0.#}×");
        var gainIndex = Array.FindIndex(GainValues, value => Math.Abs(value - state.OutputGain) < 0.01f);
        GainCombo.SelectedIndex = gainIndex >= 0 ? gainIndex : 3;
        AutoStartCheck.IsChecked = AutoStart.IsEnabled;
        AutoHealCheck.IsChecked = state.AutoHeal;
        initializing = false;

        Log.OnLine += QueueLog;
        pipeline.OnLevel += level => Volatile.Write(ref currentLevel, level);
        pipeline.OnState += (_, _, _) => Dispatcher.BeginInvoke(RefreshUi);
        state.Changed += () => Dispatcher.BeginInvoke(RefreshUi);

        timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };
        timer.Tick += (_, _) =>
        {
            FlushLogs();
            var level = Volatile.Read(ref currentLevel);
            LevelBar.Value = level;
            Volatile.Write(ref currentLevel, level * 0.92f);
            LiveBadge.Visibility = pipeline.Streaming ? Visibility.Visible : Visibility.Collapsed;
            DeviceText.Text = pipeline.DeviceName
                ?? (pipeline.CableInstalledNow ? "VB-CABLE 已安装，等待输出" : "未找到 VB-CABLE CABLE Input");
            CableButton.Visibility = Visibility.Visible;
            AdbDependencyStatus.Text = state.AdbAvailable
                ? $"已安装 · {state.AdbVersion}"
                : "尚未安装或版本检查失败";
            AdbDependencyStatus.Foreground = state.AdbAvailable ? MediaBrushes.Green : MediaBrushes.Firebrick;
            CableDependencyStatus.Text = state.CableAvailable ? "已检测到 VB-CABLE" : "尚未检测到 VB-CABLE";
            CableDependencyStatus.Foreground = state.CableAvailable ? MediaBrushes.Green : MediaBrushes.Firebrick;
            ApkSelectionStatus.Text = string.IsNullOrWhiteSpace(state.SelectedApkPath)
                ? "尚未选择 APK"
                : state.SelectedApkValid ? "APK 文件可读，可以安装" : "所选文件已失效，请重新选择";
            ApkSelectionStatus.Foreground = state.SelectedApkValid ? MediaBrushes.Green : MediaBrushes.DarkOrange;
            ApkPathBox.Text = state.SelectedApkPath;
            RefreshUi();
        };
        timer.Start();
        RefreshUi();
    }

    void QueueLog(string line)
    {
        while (pendingLogs.Count >= MaxPendingLogs && pendingLogs.TryDequeue(out _)) { }
        pendingLogs.Enqueue(line);
    }

    void FlushLogs()
    {
        var added = 0;
        while (added++ < 200 && pendingLogs.TryDequeue(out var line))
            displayedLogs.Add(line);
        while (displayedLogs.Count > MaxDisplayedLogs)
            displayedLogs.RemoveAt(0);
        if (added > 1 && LogList.Items.Count > 0)
            LogList.ScrollIntoView(LogList.Items[^1]);
    }

    void RefreshUi()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(RefreshUi); return; }
        ConnectionText.Text = state.AdbConnected ? $"ADB 已连接 · {state.DeviceModel}" : "ADB 未连接";
        ConnectionText.Foreground = state.AdbConnected ? System.Windows.Media.Brushes.Green : System.Windows.Media.Brushes.Gray;

        SetStatus(WakeupStatus,
            state.WakeupDisabled ? "已禁用（可能导致串流静音）" : "已启用（阵列可用）",
            state.WakeupDisabled ? System.Windows.Media.Brushes.Firebrick : System.Windows.Media.Brushes.Green);
        WakeupButton.Content = state.WakeupDisabled ? "恢复阵列" : "禁用阵列";

        SetStatus(AppStatus, state.AppInstalled ? "已安装" : "未安装",
            state.AppInstalled ? System.Windows.Media.Brushes.Green : System.Windows.Media.Brushes.Gray);
        AppButton.Content = "重新检测";

        SetStatus(ServiceStatus, state.ServiceRunning ? "运行中 · 端口 50010" : "已停止",
            state.ServiceRunning ? System.Windows.Media.Brushes.Green : System.Windows.Media.Brushes.Gray);
        ServiceButton.Content = state.ServiceRunning ? "停止" : "启动";

        var streaming = pipeline.Streaming;
        SetStatus(ReceiverStatus,
            !pipeline.Running ? "已停止" : streaming ? $"接收中 · {pipeline.StreamInfo}" : "连接中 / 等待数据",
            streaming ? System.Windows.Media.Brushes.Green : pipeline.Running ? System.Windows.Media.Brushes.DarkOrange : System.Windows.Media.Brushes.Gray);
        ReceiverButton.Content = pipeline.Running ? "停止" : "启动";

        var busy = state.Busy;
        ConnectButton.IsEnabled = HealButton.IsEnabled = !busy;
        WakeupButton.IsEnabled = ServiceButton.IsEnabled = !busy && state.AdbConnected;
        AppButton.IsEnabled = !busy;
        InstallApkButton.IsEnabled = !busy && state.AdbConnected && state.SelectedApkValid;
        ReceiverButton.IsEnabled = ToneButton.IsEnabled = true;
    }

    static void SetStatus(TextBlock target, string text, System.Windows.Media.Brush color)
    {
        target.Text = text;
        target.Foreground = color;
    }

    async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        state.MonitorIP = IpBox.Text.Trim();
        state.SaveConfig();
        await state.Connect();
    }

    async void HealButton_Click(object sender, RoutedEventArgs e)
    {
        state.MonitorIP = IpBox.Text.Trim();
        state.SaveConfig();
        await state.HealAll();
    }

    async void WakeupButton_Click(object sender, RoutedEventArgs e) => await state.ToggleWakeup();
    async void AppButton_Click(object sender, RoutedEventArgs e)
    {
        await state.RefreshDependencies();
        await state.Refresh();
    }

    async void DependencyButton_Click(object sender, RoutedEventArgs e) => await state.RefreshDependencies();

    void ChooseApkButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择显示器端 MicStreamer APK",
            Filter = "Android APK (*.apk)|*.apk|所有文件 (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog() == true) state.SelectApk(dialog.FileName);
    }

    async void InstallApkButton_Click(object sender, RoutedEventArgs e) => await state.InstallSelectedApk();

    async void ServiceButton_Click(object sender, RoutedEventArgs e)
    {
        if (state.ServiceRunning) await state.StopStreaming();
        else await state.StartStreaming();
    }

    void ReceiverButton_Click(object sender, RoutedEventArgs e) => state.ToggleReceiver();

    void ToneButton_Click(object sender, RoutedEventArgs e) => Task.Run(() => pipeline.PlayTestTone());
    void CableButton_Click(object sender, RoutedEventArgs e) => OpenCableWebsite();

    void GainCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!initializing && GainCombo.SelectedIndex >= 0)
        {
            state.OutputGain = GainValues[GainCombo.SelectedIndex];
            state.SaveConfig();
        }
    }

    void AutoStartCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!initializing && AutoStartCheck.IsChecked is bool enabled)
            AutoStart.SetEnabled(enabled);
    }

    void AutoHealCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!initializing && AutoHealCheck.IsChecked is bool enabled)
        {
            state.AutoHeal = enabled;
            state.SaveConfig();
        }
    }

    static void OpenCableWebsite()
    {
        try
        {
            Log.Info("打开 VB-CABLE 官方下载页；安装完成后请点击重新检测…");
            Process.Start(new ProcessStartInfo("https://vb-audio.com/Cable/") { UseShellExecute = true });
        }
        catch (Exception ex) { Log.Info("启动安装程序失败: " + ex.Message); }
    }

    protected override void OnClosed(EventArgs e)
    {
        timer.Stop();
        base.OnClosed(e);
    }
}
