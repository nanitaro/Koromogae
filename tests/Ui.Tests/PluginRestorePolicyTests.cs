using Koromogae;
using Koromogae.Ui;
using Xunit;

namespace Ui.Tests;

/// <summary>起動時のプラグイン復元: 失敗したものを設定に残すか消すかの判断と、利用者に見せる文面。</summary>
public class PluginRestorePolicyTests
{
    const string Path1 = @"C:\Program Files\Steinberg\VSTPlugins\Foo.dll";

    // ---- 残すか消すか ----

    [Fact]
    public void A_missing_file_is_deleted_at_once_even_on_the_first_failure()
    {
        var d = PluginRestorePolicy.OnFailure(Path1, "ロードできません", previousFailCount: 0, fileExists: false);

        Assert.Equal(RestoreAction.Delete, d.Action);
        Assert.Contains("ファイルが見つからないため設定から削除します", d.Message);
        Assert.NotNull(d.LogMessage);
        Assert.Contains(Path1, d.LogMessage);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 2)]
    public void An_existing_plugin_that_fails_is_kept_while_the_count_is_below_the_limit(int previous, int expectedCount)
    {
        var d = PluginRestorePolicy.OnFailure(Path1, "ロードできません", previous, fileExists: true);

        Assert.Equal(RestoreAction.Keep, d.Action);
        Assert.Equal(expectedCount, d.FailCount);
        Assert.Contains($"設定には残します: {expectedCount}/{AppSettings.MaxLoadFailures} 回目", d.Message);
        Assert.Null(d.LogMessage);   // 一時的な失敗かもしれないので、ログには警告を残さない
    }

    [Fact]
    public void An_existing_plugin_is_deleted_when_the_third_failure_in_a_row_is_reached()
    {
        Assert.Equal(3, AppSettings.MaxLoadFailures);   // 回数の仕様を変えたら、このテストの前提を見直す

        var d = PluginRestorePolicy.OnFailure(Path1, "ロードできません", previousFailCount: 2, fileExists: true);

        Assert.Equal(RestoreAction.Delete, d.Action);
        Assert.Equal(3, d.FailCount);
        Assert.Contains("3 回連続で失敗したため設定から削除します", d.Message);
        Assert.NotNull(d.LogMessage);
    }

    [Fact]
    public void The_limit_is_a_parameter_and_is_inclusive()
    {
        Assert.Equal(RestoreAction.Keep, PluginRestorePolicy.OnFailure(Path1, "e", 0, true, maxFailures: 2).Action);
        Assert.Equal(RestoreAction.Delete, PluginRestorePolicy.OnFailure(Path1, "e", 1, true, maxFailures: 2).Action);
    }

    [Fact]
    public void A_missing_file_that_has_already_failed_is_still_deleted()
    {
        var d = PluginRestorePolicy.OnFailure(Path1, "e", previousFailCount: 1, fileExists: false);

        Assert.Equal(RestoreAction.Delete, d.Action);
        Assert.Contains("ファイルが見つからない", d.Message);   // 回数ではなく「見つからない」が理由
    }

    [Fact]
    public void The_message_names_the_plugin_and_the_error()
    {
        var d = PluginRestorePolicy.OnFailure(Path1, "32bit のホストで開けません", 0, true);

        Assert.StartsWith("Foo.dll: 32bit のホストで開けません", d.Message);
    }

    // ---- 名前 ----

    [Theory]
    [InlineData(@"C:\VST\Foo.dll", "Foo.dll")]
    [InlineData(@"C:\VST3\Bar.vst3", "Bar.vst3")]
    [InlineData(@"C:\VST3\Bar.vst3\", "Bar.vst3")]     // バンドル (フォルダ) の末尾の区切り
    [InlineData("C:/VST3/Baz.vst3/", "Baz.vst3")]
    [InlineData("Qux.dll", "Qux.dll")]
    public void The_display_name_is_the_last_path_segment(string path, string expected)
    {
        Assert.Equal(expected, PluginRestorePolicy.DisplayName(path));
    }

    // ---- ダイアログの本文 ----

    [Fact]
    public void No_failures_means_no_dialog()
    {
        Assert.Null(PluginRestorePolicy.FormatFailures("見出し", new List<string>(), @"C:\log\app.log"));
    }

    [Fact]
    public void A_few_failures_are_listed_in_one_dialog_with_the_log_path()
    {
        var text = PluginRestorePolicy.FormatFailures("ロードできなかったプラグインがあります",
            new[] { "A.dll: エラー1", "B.dll: エラー2" }, @"C:\log\app.log");

        Assert.Equal("ロードできなかったプラグインがあります:\n\nA.dll: エラー1\nB.dll: エラー2\n\n詳細: C:\\log\\app.log", text);
    }

    [Fact]
    public void Exactly_the_maximum_number_of_failures_is_listed_without_a_summary_line()
    {
        var failures = Enumerable.Range(1, PluginRestorePolicy.MaxFailureLines).Select(i => $"P{i}.dll: e").ToList();

        var text = PluginRestorePolicy.FormatFailures("見出し", failures, "log")!;

        Assert.DoesNotContain("ほか", text);
        Assert.Contains($"P{PluginRestorePolicy.MaxFailureLines}.dll", text);
    }

    [Fact]
    public void More_failures_than_the_maximum_are_cut_and_counted()
    {
        var failures = Enumerable.Range(1, 20).Select(i => $"P{i}.dll: e").ToList();

        var text = PluginRestorePolicy.FormatFailures("見出し", failures, "log")!;

        Assert.Contains("P8.dll", text);
        Assert.DoesNotContain("P9.dll", text);
        Assert.Contains("...ほか 12 件", text);
    }

    // ---- 上限 ----

    [Fact]
    public void Adding_is_refused_when_loaded_and_kept_plugins_reach_the_maximum()
    {
        Assert.Null(PluginRestorePolicy.CapacityError(loaded: 60, keptInSettings: 3));
        Assert.NotNull(PluginRestorePolicy.CapacityError(loaded: 60, keptInSettings: 4));
        Assert.Contains($"{AppSettings.MaxPlugins}", PluginRestorePolicy.CapacityError(AppSettings.MaxPlugins, 0)!);
    }

    [Fact]
    public void Kept_plugins_count_toward_the_limit_so_the_settings_file_cannot_exceed_it()
    {
        // 起動時に残した (まだロードされていない) プラグインも保存し直すので、ロード済みだけで数えると設定が上限を超える
        Assert.NotNull(PluginRestorePolicy.CapacityError(loaded: 0, keptInSettings: AppSettings.MaxPlugins));
    }
}
