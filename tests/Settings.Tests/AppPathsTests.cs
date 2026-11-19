using Koromogae;
using Xunit;

namespace Settings.Tests;

/// <summary>設定ファイル・ログの保存先。環境変数で変えられる (自動テストが利用者の設定を触らないため)。</summary>
public class AppPathsTests
{
    const string AppData = @"C:\Users\someone\AppData\Roaming";

    [Fact]
    public void Without_an_override_it_is_the_Koromogae_folder_under_AppData()
    {
        Assert.Equal(@"C:\Users\someone\AppData\Roaming\Koromogae", AppPaths.Resolve(null, AppData));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void An_empty_or_blank_override_counts_as_no_override(string value)
    {
        Assert.Equal(@"C:\Users\someone\AppData\Roaming\Koromogae", AppPaths.Resolve(value, AppData));
    }

    [Fact]
    public void An_override_replaces_the_folder_itself_not_a_parent_of_it()
    {
        Assert.Equal(@"D:\temp\test-run", AppPaths.Resolve(@"D:\temp\test-run", AppData));
    }

    [Fact]
    public void A_relative_override_becomes_an_absolute_path()
    {
        string resolved = AppPaths.Resolve(@"relative\dir", AppData);

        Assert.True(Path.IsPathRooted(resolved));
        Assert.EndsWith(@"relative\dir", resolved);
    }

    // ---- 標準の保存先の起点の差し替え (自動テスト用) ----

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Without_an_appdata_override_the_real_appdata_is_used(string? value)
    {
        Assert.Equal(AppData, AppPaths.ResolveAppData(value, AppData));
    }

    [Fact]
    public void An_appdata_override_replaces_the_base_of_the_standard_folder()
    {
        string appData = AppPaths.ResolveAppData(@"E:\fake-appdata", AppData);

        Assert.Equal(@"E:\fake-appdata", appData);
        Assert.Equal(@"E:\fake-appdata\Koromogae", AppPaths.Resolve(null, appData));
        // 戻り先も、本物の %APPDATA% ではなく差し替えた起点になる (ポータブルが効かなくても、本物の設定に届かない)
        var fallback = AppPaths.ResolveLocation(null, appData, AppFolder, p => p.EndsWith("portable"), _ => false);
        Assert.Equal(@"E:\fake-appdata\Koromogae", fallback.Directory);
    }

    [Fact]
    public void A_relative_appdata_override_becomes_an_absolute_path()
    {
        Assert.True(Path.IsPathRooted(AppPaths.ResolveAppData(@"relative\appdata", AppData)));
    }

    [Fact]
    public void The_data_directory_override_still_wins_over_the_appdata_override()
    {
        string appData = AppPaths.ResolveAppData(@"E:\fake-appdata", AppData);

        var location = AppPaths.ResolveLocation(@"F:\data", appData, AppFolder, _ => true, _ => true);

        Assert.Equal(@"F:\data", location.Directory);
        Assert.Equal(DataLocationMode.Override, location.Mode);
    }

    // ---- ポータブルモード (本体のフォルダの目印) ----

    const string AppFolder = @"D:\tools\Koromogae";
    static readonly string[] NoFiles = Array.Empty<string>();

    /// <summary>指定したファイルだけが「ある」ことにして、保存先を決める。</summary>
    static DataLocation Locate(string? overrideDir, string[] existingFiles, bool canWrite, string baseDirectory = AppFolder + "\\")
        => AppPaths.ResolveLocation(overrideDir, AppData, baseDirectory,
            path => existingFiles.Contains(path, StringComparer.OrdinalIgnoreCase), _ => canWrite);

    [Fact]
    public void Without_a_marker_the_standard_folder_is_used()
    {
        var location = Locate(null, NoFiles, canWrite: true);

        Assert.Equal(DataLocationMode.Standard, location.Mode);
        Assert.Equal(@"C:\Users\someone\AppData\Roaming\Koromogae", location.Directory);
        Assert.Null(location.FallbackReason);
    }

    [Theory]
    [InlineData("portable")]
    [InlineData("portable.txt")]
    public void A_marker_next_to_the_exe_selects_the_exe_folder(string marker)
    {
        var location = Locate(null, new[] { AppFolder + "\\" + marker }, canWrite: true);

        Assert.Equal(DataLocationMode.Portable, location.Mode);
        Assert.Equal(AppFolder, location.Directory);
        Assert.Null(location.FallbackReason);
    }

    [Theory]
    [InlineData(@"D:\tools\Koromogae")]
    [InlineData(@"D:\tools\Koromogae\")]
    public void A_trailing_separator_on_the_exe_folder_makes_no_difference(string baseDirectory)
    {
        var location = Locate(null, new[] { @"D:\tools\Koromogae\portable" }, canWrite: true, baseDirectory);

        Assert.Equal(DataLocationMode.Portable, location.Mode);
        Assert.Equal(AppFolder, location.Directory);
    }

    [Fact]
    public void A_marker_in_another_folder_does_not_count()
    {
        var location = Locate(null, new[] { @"D:\tools\portable", @"D:\tools\Koromogae\sub\portable" }, canWrite: true);

        Assert.Equal(DataLocationMode.Standard, location.Mode);
    }

    [Fact]
    public void An_environment_override_wins_over_a_marker()
    {
        var location = Locate(@"E:\test-run", new[] { AppFolder + "\\portable" }, canWrite: true);

        Assert.Equal(DataLocationMode.Override, location.Mode);
        Assert.Equal(@"E:\test-run", location.Directory);
    }

    [Fact]
    public void A_marker_in_a_folder_that_cannot_be_written_falls_back_to_the_standard_folder_and_says_why()
    {
        var location = Locate(null, new[] { AppFolder + "\\portable" }, canWrite: false);

        Assert.Equal(DataLocationMode.Standard, location.Mode);
        Assert.Equal(@"C:\Users\someone\AppData\Roaming\Koromogae", location.Directory);
        Assert.NotNull(location.FallbackReason);
        Assert.Contains("portable", location.FallbackReason);
        Assert.Contains(AppFolder, location.FallbackReason);
        Assert.Contains(location.Directory, location.FallbackReason);
    }

    [Fact]
    public void Writability_is_only_asked_when_there_is_a_marker()
    {
        int asked = 0;

        AppPaths.ResolveLocation(null, AppData, AppFolder, _ => false, _ => { asked++; return true; });

        Assert.Equal(0, asked);     // 目印がなければ、本体のフォルダに一時ファイルを作らない
    }

    [Theory]
    [InlineData(DataLocationMode.Portable, "データの保存先 (ポータブル): D:\\tools\\Koromogae")]
    [InlineData(DataLocationMode.Standard, "データの保存先 (標準): D:\\tools\\Koromogae")]
    [InlineData(DataLocationMode.Override, "データの保存先 (環境変数の指定): D:\\tools\\Koromogae")]
    public void The_log_line_names_the_mode_and_the_folder(DataLocationMode mode, string expected)
    {
        Assert.Equal(expected, new DataLocation(AppFolder, mode).Describe());
    }

    [Fact]
    public void The_log_line_after_a_fallback_says_that_portable_was_not_used()
    {
        string line = new DataLocation(@"C:\x", DataLocationMode.Standard, "理由").Describe();

        Assert.Contains("ポータブルは使えませんでした", line);
    }

    [Fact]
    public void The_settings_file_and_the_log_live_in_the_data_directory()
    {
        // 環境変数を設定していない通常の実行では、既定の場所 (AppData) になる。既存の利用者の保存先は変わらない
        string? previous = Environment.GetEnvironmentVariable(AppPaths.DataDirEnvVar);
        if (!string.IsNullOrWhiteSpace(previous)) return;   // 呼び出し側が設定済みの環境では判定しない

        Assert.Equal(Path.Combine(AppPaths.DataDirectory, "settings.json"), AppSettings.DefaultFilePath);
        Assert.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppSettings.DefaultFilePath);
    }
}