using Koromogae.Ui;
using Xunit;

namespace Ui.Tests;

/// <summary>バージョン情報の文言 (版の整形・起動ログ・ホストの版の一致・x86 版 .NET の診断)。</summary>
public class AppVersionInfoTests
{
    // ---- 版の整形 ----

    [Theory]
    [InlineData("1.0.0", "1.0.0")]
    [InlineData("1.2.3+abcdef0", "1.2.3")]          // ビルドメタデータ (コミット ID など) は出さない
    [InlineData("1.2.3-beta.1+abc", "1.2.3-beta.1")]  // プレリリースの印は残す
    [InlineData("  1.0.0  ", "1.0.0")]
    [InlineData("", "不明")]
    [InlineData(null, "不明")]
    public void The_version_is_shown_without_build_metadata(string? raw, string expected)
    {
        Assert.Equal(expected, AppVersionInfo.CleanVersion(raw));
    }

    [Fact]
    public void The_startup_line_has_the_product_version_runtime_and_os()
    {
        string line = AppVersionInfo.StartupLine("1.2.3+abc", ".NET 10.0.15", "Microsoft Windows 10.0.26200", "X64");

        Assert.Equal("起動: Koromogae 1.2.3 / .NET 10.0.15 X64 / Microsoft Windows 10.0.26200", line);
    }

    // ---- プラグインホストの版 ----

    [Fact]
    public void A_host_with_the_same_version_is_reported_as_matching()
    {
        Assert.Equal("32bit: 1.0.0 (本体と一致)", AppVersionInfo.HostStatus("32bit", "1.0.0", "1.0.0"));
    }

    [Fact]
    public void Build_metadata_does_not_cause_a_false_mismatch()
    {
        Assert.Equal("64bit: 1.0.0 (本体と一致)", AppVersionInfo.HostStatus("64bit", "1.0.0+abc", "1.0.0"));
    }

    [Fact]
    public void A_host_with_a_different_version_is_reported_with_both_versions()
    {
        string text = AppVersionInfo.HostStatus("64bit", "0.9.0", "1.0.0");

        Assert.Contains("0.9.0", text);
        Assert.Contains("本体 1.0.0", text);
        Assert.Contains("一致しません", text);
    }

    [Fact]
    public void A_missing_host_is_reported()
    {
        Assert.Equal("32bit: 見つかりません", AppVersionInfo.HostStatus("32bit", null, "1.0.0"));
    }

    // ---- x86 版 .NET ----

    [Fact]
    public void No_x86_runtime_tells_the_user_what_is_needed()
    {
        string text = AppVersionInfo.X86RuntimeStatus(Array.Empty<string>());

        Assert.Contains("見つかりません", text);
        Assert.Contains("x86 版の .NET 10", text);
    }

    [Fact]
    public void The_newest_runtime_of_the_required_major_is_shown()
    {
        string text = AppVersionInfo.X86RuntimeStatus(new[] { "8.0.34", "10.0.9", "10.0.15", "11.0.0" });

        Assert.Equal("x86 版 .NET: 10.0.15 (利用できます)", text);   // 10.0.9 より 10.0.15 が新しい (文字列順ではなく数字の順)
    }

    [Fact]
    public void Other_majors_alone_do_not_count_because_the_host_does_not_roll_forward_to_them()
    {
        string text = AppVersionInfo.X86RuntimeStatus(new[] { "11.0.0", "8.0.34" });

        Assert.Contains("10.x がありません", text);
        Assert.Contains("8.0.34", text);
        Assert.Contains("11.0.0", text);
        Assert.DoesNotContain("利用できます", text);
    }

    [Fact]
    public void A_preview_runtime_folder_name_is_parsed_without_failing()
    {
        string text = AppVersionInfo.X86RuntimeStatus(new[] { "10.0.0-rc.2.25502.107", "garbage" });

        Assert.Equal("x86 版 .NET: 10.0.0-rc.2.25502.107 (利用できます)", text);
    }

    // ---- 画面とコピーのテキスト ----

    [Fact]
    public void The_report_lists_everything_needed_for_a_bug_report()
    {
        var report = new AppVersionReport("1.0.0+x", ".NET 10.0.15", "Microsoft Windows 10.0.26200", "X64",
            "PortAudio V19.7.0-devel", "1.0.0", "1.0.0", new[] { "10.0.15" });

        string text = AppVersionInfo.Report(report);

        Assert.StartsWith("Koromogae 1.0.0", text);
        Assert.Contains(".NET: .NET 10.0.15 (X64)", text);
        Assert.Contains("OS: Microsoft Windows 10.0.26200", text);
        Assert.Contains("PortAudio: PortAudio V19.7.0-devel", text);
        Assert.Contains("32bit: 1.0.0 (本体と一致)", text);
        Assert.Contains("64bit: 1.0.0 (本体と一致)", text);
        Assert.Contains("x86 版 .NET: 10.0.15 (利用できます)", text);
        Assert.DoesNotContain("+x", text);
        Assert.DoesNotContain("保存先", text);     // ログ・設定フォルダの情報は、この画面には出さない (メニューの「ログ・設定フォルダを開く」から開く)
    }
}