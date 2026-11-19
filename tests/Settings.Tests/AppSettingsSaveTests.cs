using Koromogae;
using Xunit;

namespace Settings.Tests;

[Collection("AppLog")] // AppLog.LogPath は静的なので、このコレクション内は直列で実行する
public class AppSettingsSaveTests
{
    [Fact]
    public void Save_creates_missing_directories_and_leaves_no_temp_file()
    {
        using var dir = new TempDir();
        string path = Path.Combine(dir.Path, "a", "b", "settings.json");

        Assert.True(new AppSettings().Save(path));

        Assert.True(File.Exists(path));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void Save_replaces_an_existing_file()
    {
        using var dir = new TempDir();
        string path = dir.File("settings.json");
        File.WriteAllText(path, "old content");

        Assert.True(new AppSettings { SampleRate = 96000 }.Save(path));

        Assert.Equal(96000, AppSettings.Load(path).SampleRate);
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void Save_does_not_use_a_utf8_bom()
    {
        using var dir = new TempDir();
        string path = dir.File("settings.json");

        new AppSettings { InputDevice = "マイク" }.Save(path);

        byte[] head = File.ReadAllBytes(path).Take(3).ToArray();
        Assert.NotEqual(new byte[] { 0xEF, 0xBB, 0xBF }, head);
    }

    [Fact]
    public void Failed_save_returns_false_logs_and_keeps_the_original_file()
    {
        using var dir = new TempDir();
        string path = dir.File("settings.json");
        new AppSettings { SampleRate = 44100 }.Save(path);
        string before = File.ReadAllText(path);
        File.SetAttributes(path, FileAttributes.ReadOnly); // 置き換えが失敗する状況を作る

        bool ok = new AppSettings { SampleRate = 192000 }.Save(path);

        Assert.False(ok);
        Assert.Equal(before, File.ReadAllText(path));          // 元のファイルは無傷
        Assert.False(File.Exists(path + ".tmp"));              // 一時ファイルも残さない
        Assert.Contains("設定を保存できません", TestSupport.ReadLog());
        Assert.Contains(dir.Path, TestSupport.ReadLog());
    }

    [Fact]
    public void Save_is_atomic_a_failure_while_writing_leaves_the_original_untouched()
    {
        using var dir = new TempDir();
        string path = dir.File("settings.json");
        new AppSettings { SampleRate = 44100 }.Save(path);
        string before = File.ReadAllText(path);

        // 一時ファイルの場所をディレクトリで塞ぐ。直接上書きする実装なら、ここで元のファイルが書き換わってしまう
        Directory.CreateDirectory(path + ".tmp");

        bool ok = new AppSettings { SampleRate = 192000 }.Save(path);

        Assert.False(ok);
        Assert.Equal(before, File.ReadAllText(path));
        Assert.Equal(44100, AppSettings.Load(path).SampleRate);
    }

    [Fact]
    public void Save_into_an_impossible_location_does_not_throw()
    {
        using var dir = new TempDir();
        string blocker = dir.File("file-not-a-directory");
        File.WriteAllText(blocker, "x");

        // ディレクトリがあるべき場所にファイルがある
        bool ok = new AppSettings().Save(Path.Combine(blocker, "settings.json"));

        Assert.False(ok);
    }
}
