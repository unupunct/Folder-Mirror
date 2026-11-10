using FolderMirror.Core.Engine;
using Xunit;

namespace FolderMirror.Tests;

public class PlatformQuirkTests
{
    [Theory]
    [InlineData(".DS_Store", true)]
    [InlineData("._photo.jpg", true)]
    [InlineData(".localized", true)]
    [InlineData("photo.jpg", false)]
    [InlineData("._", false)]
    public void Junk_files(string name, bool junk) => Assert.Equal(junk, PathTools.IsSystemJunkFile(name));

    [Fact]
    public void Junk_folders() { Assert.True(PathTools.IsSystemJunkFolder(".Trashes")); Assert.False(PathTools.IsSystemJunkFolder("Photos")); }

    [Fact]
    public void Scanner_skips_junk()
    {
        using var sb = new Sandbox();
        sb.Write(sb.Src, "a.txt", "x"); sb.Write(sb.Src, ".DS_Store", "x"); sb.Write(sb.Src, "._a.txt", "x");
        sb.Write(sb.Src, ".Spotlight-V100/store", "x");
        var r = FileScanner.Scan(sb.Src, sb.Profile, CancellationToken.None);
        Assert.Equal(new[] { "a.txt" }, r.Entries.Keys.ToArray());
    }

    [Fact]
    public void Keys_ignore_case_and_on_mac_unicode_form()
    {
        Assert.True(PathTools.KeyComparer.Equals("Report.TXT", "report.txt"));
        if (OperatingSystem.IsMacOS())
            Assert.True(PathTools.KeyComparer.Equals("caf\u00e9", "cafe\u0301"));
    }

    [Fact]
    public void Backslash_is_only_a_separator_on_windows()
    {
        var t = PathTools.TrimSeparators("/tmp/x\\");
        Assert.Equal(OperatingSystem.IsWindows() ? "/tmp/x" : "/tmp/x\\", t);
    }

    [Fact]
    public void Posix_errno_recognised()
    {
        if (OperatingSystem.IsWindows()) return;
        Assert.True(ErrorTranslator.IsDiskFull(new IOException("x", 28)));
        Assert.True(ErrorTranslator.IsLocked(new IOException("x", 16)));
        Assert.Contains("free space", ErrorTranslator.Describe(new IOException("x", 28)));
    }
}
