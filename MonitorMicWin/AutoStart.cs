using Microsoft.Win32;

namespace MonitorMicWin;

/// <summary>注册表开机自启（HKCU Run 键，无需管理员权限）。</summary>
static class AutoStart
{
    const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "MonitorMic";

    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false);
                return key?.GetValue(ValueName) is string s && s.Contains("MonitorMic", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }
    }

    public static void SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true);
            if (key == null) return;
            if (enabled)
                key.SetValue(ValueName, $"\"{Application.ExecutablePath}\" --minimized");
            else
                key.DeleteValue(ValueName, false);
            Log.Info(enabled ? "开机自启动：已开启" : "开机自启动：已关闭");
        }
        catch (Exception ex)
        {
            Log.Info("设置开机自启失败: " + ex.Message);
        }
    }
}
