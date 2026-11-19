using System.Globalization;
using Koromogae.Ui;
using Xunit;

namespace Ui.Tests;

/// <summary>「ヘルプ > マニュアルを開く」が開く HTML を探す規則 (画面の言語を優先、無ければもう一方、どちらも無ければ null)。ファイルの有無は引数で渡すので、ディスクに触れない。</summary>
public class ManualLocatorTests
{
    const string Dir = @"C:\app";
    static string Path(string name) => System.IO.Path.Combine(Dir, name);

    static string? Resolve(string culture, params string[] existing)
        => ManualLocator.Resolve(Dir, CultureInfo.GetCultureInfo(culture), p => existing.Select(Path).Contains(p));

    [Theory]
    [InlineData("ja", "manual.ja.html")]
    [InlineData("ja-JP", "manual.ja.html")]
    [InlineData("en", "manual.en.html")]
    [InlineData("en-US", "manual.en.html")]
    [InlineData("fr", "manual.en.html")]       // 翻訳のない言語は、画面も英語なので、英語のマニュアル
    [InlineData("de-DE", "manual.en.html")]
    public void The_manual_of_the_screen_language_is_preferred_when_both_exist(string culture, string expected)
        => Assert.Equal(Path(expected), Resolve(culture, "manual.ja.html", "manual.en.html"));

    [Fact]
    public void An_english_screen_falls_back_to_the_japanese_manual_when_there_is_no_english_one()
        => Assert.Equal(Path("manual.ja.html"), Resolve("en", "manual.ja.html"));

    [Fact]
    public void A_japanese_screen_falls_back_to_the_english_manual_when_there_is_no_japanese_one()
        => Assert.Equal(Path("manual.en.html"), Resolve("ja", "manual.en.html"));

    [Theory]
    [InlineData("ja")]
    [InlineData("en")]
    public void No_manual_gives_null_so_the_app_can_tell_the_user(string culture)
        => Assert.Null(Resolve(culture));

    [Fact]
    public void Only_the_manuals_next_to_the_executable_are_looked_for()
        => Assert.Null(ManualLocator.Resolve(Dir, CultureInfo.GetCultureInfo("ja"), p => System.IO.Path.GetDirectoryName(p) != Dir));   // 別のフォルダにあっても、使わない

    [Theory]
    [InlineData("ja", "manual.ja.html")]
    [InlineData("en", "manual.en.html")]
    [InlineData("xx", "manual.en.html")]
    public void File_names_match_what_the_build_produces(string language, string expected)
        => Assert.Equal(expected, ManualLocator.FileName(language));
}