using System.IO;
using System.Security.Cryptography;
using Xunit;

namespace App.Tests;

/// <summary>
/// ポータブルモード: 本体と同じフォルダに目印 (portable / portable.txt) があると、設定とログを本体のフォルダに保存する。
/// 本体のビルド出力を一時フォルダへコピーして起動する。標準の保存先の起点 (%APPDATA% の部分) は別の一時フォルダに差し替える
/// (KOROMOGAE_APPDATA_DIR) ので、ポータブルが効かなくても利用者の本物の設定は触らない。
/// </summary>
[Collection("App")]
public class PortableModeTests
{
    static string RealSettings => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Koromogae", "settings.json");

    static (string? Hash, DateTime? Time) Snapshot()
        => File.Exists(RealSettings)
            ? (Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(RealSettings))), File.GetLastWriteTimeUtc(RealSettings))
            : (null, null);

    static void AssertSaved(string folder, string what)
    {
        Assert.True(File.Exists(Path.Combine(folder, "settings.json")), $"settings.json が {what} にありません");
        Assert.True(File.Exists(Path.Combine(folder, "app.log")), $"app.log が {what} にありません");
    }

    static void AssertNothingSaved(string folder, string what)
    {
        Assert.False(File.Exists(Path.Combine(folder, "settings.json")), $"settings.json が {what} にできています");
        Assert.False(File.Exists(Path.Combine(folder, "app.log")), $"app.log が {what} にできています");
    }

    [SkippableTheory]
    [InlineData("portable")]
    [InlineData("portable.txt")]
    public void A_marker_next_to_the_exe_keeps_the_settings_and_the_log_in_the_exe_folder(string marker)
    {
        var before = Snapshot();
        string appFolder;
        string standard;
        string log;
        using (var app = AppHarness.StartPortableOrSkip(marker))
        {
            app.Select("RateCombo", "96000");
            app.Close();
            appFolder = app.DataDir;
            standard = app.StandardDir!;

            AssertSaved(appFolder, "本体のフォルダ");
            Assert.Equal(96000, app.SavedSettings().GetProperty("SampleRate").GetInt32());   // 設定がそこに保存され、読み戻せる
            log = File.ReadAllText(app.LogPath);
        }

        Assert.Contains("データの保存先 (ポータブル)", log);
        Assert.Contains(appFolder, log);
        Assert.False(Directory.Exists(standard), "標準の保存先にフォルダができています");   // (一時フォルダは Dispose で消えるので、存在しないのが正常)
        Assert.Equal(before, Snapshot());                                                    // 利用者の本物の設定は変わらない
    }

    [SkippableFact]
    public void Without_a_marker_the_standard_folder_is_used_and_the_exe_folder_stays_clean()
    {
        var before = Snapshot();
        using (var app = AppHarness.StartPortableOrSkip(markerName: ""))
        {
            app.Close();

            AssertNothingSaved(app.DataDir, "本体のフォルダ");
            AssertSaved(app.StandardDir!, "標準の保存先");
            string log = File.ReadAllText(Path.Combine(app.StandardDir!, "app.log"));
            Assert.Contains("データの保存先 (標準)", log);
        }

        Assert.Equal(before, Snapshot());
    }

    [SkippableFact]
    public void The_data_directory_variable_wins_over_a_marker()
    {
        var before = Snapshot();
        using (var app = AppHarness.StartPortableOrSkip("portable", withDataDirOverride: true))
        {
            app.Close();

            AssertSaved(app.DataDir, "環境変数で指定したフォルダ");
            string exeFolder = Path.Combine(Path.GetDirectoryName(app.DataDir)!, "app");
            AssertNothingSaved(exeFolder, "本体のフォルダ");
            AssertNothingSaved(app.StandardDir!, "標準の保存先");
            Assert.Contains("データの保存先 (環境変数の指定)", File.ReadAllText(app.LogPath));
        }

        Assert.Equal(before, Snapshot());
    }
}