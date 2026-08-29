using MonitorMicWin;
using Xunit;

using System.IO;

namespace MonitorMicWin.Tests;

public sealed class WindowsDependencyProbeTests
{
    [Fact]
    public void FindsAdbInPathBeforeCommonLocations()
    {
        var root = Directory.CreateTempSubdirectory("MonitorMic-adb-");
        try
        {
            var adb = Path.Combine(root.FullName, "adb.exe");
            File.WriteAllBytes(adb, new byte[] { 0x4D, 0x5A });

            Assert.Equal(adb, WindowsDependencyProbe.FindAdb(
                new[] { root.FullName }, Array.Empty<string>()));
        }
        finally { root.Delete(recursive: true); }
    }

    [Fact]
    public void ReportsMissingAdbWhenCandidatesDoNotExist()
    {
        Assert.Null(WindowsDependencyProbe.FindAdb(
            new[] { Path.Combine(Path.GetTempPath(), "MonitorMic-no-adb") },
            Array.Empty<string>()));
    }

    [Theory]
    [InlineData("Android Debug Bridge version 35.0.2", true)]
    [InlineData("adb: unknown command", false)]
    public void ValidatesAdbVersionOutput(string output, bool expected)
    {
        Assert.Equal(expected, WindowsDependencyProbe.IsAdbVersionOutputValid(output));
    }

    [Fact]
    public void RejectsNoApkSelectionAndUnreadableFiles()
    {
        Assert.False(WindowsDependencyProbe.IsApkPathValid(null));
        Assert.False(WindowsDependencyProbe.IsApkPathValid(""));

        var root = Directory.CreateTempSubdirectory("MonitorMic-apk-");
        try
        {
            var wrongExtension = Path.Combine(root.FullName, "micstreamer.zip");
            File.WriteAllBytes(wrongExtension, new byte[] { 0x50, 0x4B, 0x03, 0x04 });
            var invalidApk = Path.Combine(root.FullName, "invalid.apk");
            File.WriteAllBytes(invalidApk, new byte[] { 0x4D, 0x5A });
            Assert.False(WindowsDependencyProbe.IsApkPathValid(wrongExtension));
            Assert.False(WindowsDependencyProbe.IsApkPathValid(invalidApk));
        }
        finally { root.Delete(recursive: true); }
    }

    [Fact]
    public void AcceptsReadableZipBasedApk()
    {
        var root = Directory.CreateTempSubdirectory("MonitorMic-apk-");
        try
        {
            var apk = Path.Combine(root.FullName, "micstreamer.apk");
            File.WriteAllBytes(apk, new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x00 });
            Assert.True(WindowsDependencyProbe.IsApkPathValid(apk));
        }
        finally { root.Delete(recursive: true); }
    }
}
