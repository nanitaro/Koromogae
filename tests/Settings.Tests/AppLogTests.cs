using Koromogae;
using Xunit;

namespace Settings.Tests;

[Collection("AppLog")] // AppLog.LogPath は静的なので、このコレクション内は直列で実行する
public class AppLogTests
{
    [Fact]
    public void Entries_have_a_level_a_message_and_the_exception()
    {
        string marker = Guid.NewGuid().ToString("N");

        AppLog.Error("failure " + marker, new InvalidOperationException("boom " + marker));
        AppLog.Warn("warning " + marker);

        string log = TestSupport.ReadLog();
        Assert.Contains("[ERROR] failure " + marker, log);
        Assert.Contains("InvalidOperationException: boom " + marker, log);
        Assert.Contains("[WARN] warning " + marker, log);
    }

    [Fact]
    public void Very_long_messages_are_truncated()
    {
        string marker = Guid.NewGuid().ToString("N");

        AppLog.Info(marker + new string('x', 100_000));

        string log = TestSupport.ReadLog();
        int start = log.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0);
        Assert.Contains("(省略)", log[start..]);
        Assert.DoesNotContain(new string('x', 20_000), log);
    }

    [Fact]
    public void An_unwritable_log_never_throws()
    {
        string saved = AppLog.LogPath;
        using var dir = new TempDir();
        string blocker = dir.File("not-a-dir");
        File.WriteAllText(blocker, "x");
        try
        {
            AppLog.LogPath = Path.Combine(blocker, "app.log"); // ディレクトリがあるべき場所にファイル

            var ex = Record.Exception(() => AppLog.Error("ignored", new Exception("x")));

            Assert.Null(ex);
        }
        finally
        {
            AppLog.LogPath = saved;
        }
    }

    [Fact]
    public void Large_logs_rotate_to_old()
    {
        string saved = AppLog.LogPath;
        using var dir = new TempDir();
        try
        {
            AppLog.LogPath = dir.File("app.log");
            string chunk = new('y', 7000);
            for (int i = 0; i < 200; i++) AppLog.Info(chunk); // 約 1.4MB

            Assert.True(File.Exists(dir.File("app.log.old")));
            Assert.True(new FileInfo(dir.File("app.log")).Length < 1_100_000);
        }
        finally
        {
            AppLog.LogPath = saved;
        }
    }
}
