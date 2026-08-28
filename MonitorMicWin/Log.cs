namespace MonitorMicWin;

/// <summary>简单日志：写文件（%LocalAppData%\MonitorMic\monitor-mic.log）+ 回调给 UI。</summary>
static class Log
{
    public static event Action<string>? OnLine;

    static readonly object Gate = new();
    static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonitorMic");
    static readonly string FilePath = Path.Combine(Dir, "monitor-mic.log");

    public static string LogFile => FilePath;

    public static void Info(string msg) => Write(msg);

    static void Write(string msg)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {msg}";
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Dir);
                var fi = new FileInfo(FilePath);
                if (fi.Exists && fi.Length > 2 * 1024 * 1024) fi.Delete(); // 超 2MB 重来
                File.AppendAllText(FilePath, line + Environment.NewLine);
            }
        }
        catch { /* 日志失败不影响主流程 */ }
        try { OnLine?.Invoke(line); } catch { }
    }
}
