using System.Text.RegularExpressions;

namespace MonitorMicWin;

public sealed record AdbDependencyStatus(bool Available, string? Path, string Version, string Error)
{
    public static AdbDependencyStatus Missing(string error = "未找到 adb.exe") =>
        new(false, null, "", error);
}

public static class WindowsDependencyProbe
{
    static readonly string[] CommonAdbPaths =
    {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Android", "Sdk", "platform-tools", "adb.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Android", "platform-tools", "adb.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Android", "platform-tools", "adb.exe"),
        @"C:\platform-tools\adb.exe"
    };

    public static string? FindAdb(IEnumerable<string>? pathEntries = null, IEnumerable<string>? commonPaths = null, Func<string, bool>? fileExists = null)
        => FindAdbCandidates(pathEntries, commonPaths, fileExists).FirstOrDefault();

    public static IEnumerable<string> FindAdbCandidates(IEnumerable<string>? pathEntries = null, IEnumerable<string>? commonPaths = null, Func<string, bool>? fileExists = null)
    {
        fileExists ??= File.Exists;
        var candidates = new List<string>();
        if (pathEntries != null)
        {
            foreach (var entry in pathEntries)
            {
                if (!string.IsNullOrWhiteSpace(entry))
                    candidates.Add(Path.Combine(entry.Trim(), "adb.exe"));
            }
        }
        else
        {
            var path = Environment.GetEnvironmentVariable("PATH") ?? "";
            candidates.AddRange(path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Select(entry => Path.Combine(entry.Trim(), "adb.exe")));
        }

        candidates.AddRange(commonPaths ?? CommonAdbPaths);
        return candidates
            .Where(fileExists)
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    public static bool IsAdbVersionOutputValid(string output) =>
        Regex.IsMatch(output ?? "", @"Android Debug Bridge version\s+\d+(?:\.\d+){1,3}", RegexOptions.IgnoreCase);

    public static bool IsApkPathValid(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)
            || !path.EndsWith(".apk", StringComparison.OrdinalIgnoreCase)
            || !File.Exists(path)) return false;
        try
        {
            using var stream = File.OpenRead(path);
            if (stream.Length < 4) return false;
            Span<byte> magic = stackalloc byte[4];
            return stream.Read(magic) == 4
                && magic[0] == (byte)'P' && magic[1] == (byte)'K';
        }
        catch { return false; }
    }
}
