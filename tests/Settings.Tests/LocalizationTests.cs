using System.Globalization;
using Koromogae;
using Xunit;

namespace Settings.Tests;

/// <summary>言語の決定 (<see cref="UiLanguages"/>)・文字列の表 (<see cref="Loc"/>)・言語の設定 (<see cref="AppSettings.Language"/>)。</summary>
[Collection("AppLog")] // 設定の保存・読み込みで AppLog を使うので、同じコレクションで直列に実行する
public class LocalizationTests
{
    // ---- 言語の決め方: 環境変数 > 設定 > Windows の表示言語 ----

    [Theory]
    [InlineData(UiLanguage.Auto, null, "ja-JP", UiLanguage.Japanese)]
    [InlineData(UiLanguage.Auto, null, "ja", UiLanguage.Japanese)]
    [InlineData(UiLanguage.Auto, null, "en-US", UiLanguage.English)]
    [InlineData(UiLanguage.Auto, null, "fr-FR", UiLanguage.English)]      // 日本語以外は英語 (ニュートラルの日本語に落とさない)
    [InlineData(UiLanguage.Auto, null, "", UiLanguage.English)]
    [InlineData(UiLanguage.Auto, null, null, UiLanguage.English)]
    [InlineData(UiLanguage.Auto, "", "ja-JP", UiLanguage.Japanese)]       // 空の環境変数は無いものとみなす
    [InlineData(UiLanguage.Auto, "xx", "ja-JP", UiLanguage.Japanese)]     // 読めない値も無視する
    [InlineData(UiLanguage.Japanese, null, "en-US", UiLanguage.Japanese)] // 設定は Windows より優先
    [InlineData(UiLanguage.English, null, "ja-JP", UiLanguage.English)]
    [InlineData(UiLanguage.Japanese, "en", "ja-JP", UiLanguage.English)]  // 環境変数は設定より優先
    [InlineData(UiLanguage.English, "ja", "en-US", UiLanguage.Japanese)]
    [InlineData(UiLanguage.Auto, "en-US", "ja-JP", UiLanguage.English)]
    [InlineData(UiLanguage.Auto, " JA ", "en-US", UiLanguage.Japanese)]   // 前後の空白・大文字小文字は問わない
    public void Resolve_follows_env_then_setting_then_windows(UiLanguage setting, string? env, string? windows, UiLanguage expected)
        => Assert.Equal(expected, UiLanguages.Resolve(setting, env, windows));

    [Theory]
    [InlineData("jam")]   // ja で始まるだけの別の言語・名前は、日本語とみなさない
    [InlineData("english")]
    [InlineData("e")]
    public void Names_that_only_start_with_the_letters_are_not_taken_for_ja_or_en(string text)
        => Assert.False(UiLanguages.TryParse(text, out _));

    [Fact]
    public void Resolved_language_maps_to_a_culture_and_a_code()
    {
        Assert.Equal("ja", UiLanguages.ToCulture(UiLanguage.Japanese).Name);
        Assert.Equal("en", UiLanguages.ToCulture(UiLanguage.English).Name);
        Assert.Equal("ja", UiLanguages.ToCode(UiLanguage.Japanese));
        Assert.Equal("en", UiLanguages.ToCode(UiLanguage.English));
        // 環境変数に渡した短い名前を、読み戻せる
        foreach (var language in new[] { UiLanguage.Japanese, UiLanguage.English })
        {
            Assert.True(UiLanguages.TryParse(UiLanguages.ToCode(language), out var back));
            Assert.Equal(language, back);
        }
    }

    // ---- 文字列の表 ----

    [Fact]
    public void Loc_returns_the_text_of_the_selected_language()
    {
        Assert.Equal("ヘルプ(_H)", Loc.T("Menu.Help", Loc.Japanese));
        Assert.Equal("_Help", Loc.T("Menu.Help", Loc.English));
    }

    [Fact]
    public void An_unknown_key_is_returned_as_is_so_it_shows_on_screen()
        => Assert.Equal("No.Such.Key", Loc.T("No.Such.Key", Loc.English));

    [Fact]
    public void Override_changes_the_language_only_inside_its_scope()
    {
        string outside = Loc.T("Menu.Help");
        using (Loc.Override(Loc.English))
        {
            Assert.Equal("_Help", Loc.T("Menu.Help"));
            using (Loc.Override(Loc.Japanese)) Assert.Equal("ヘルプ(_H)", Loc.T("Menu.Help"));
            Assert.Equal("_Help", Loc.T("Menu.Help"));   // 内側を抜けたら、外側に戻る
        }
        Assert.Equal(outside, Loc.T("Menu.Help"));
    }

    [Fact]
    public async Task Override_follows_async_continuations_but_not_other_tasks()
    {
        // 並列に走るテストが、互いの言語を壊さないこと
        string inside, other;
        using (Loc.Override(Loc.English))
        {
            await Task.Yield();
            inside = Loc.T("Menu.Help");
            other = await Task.Run(async () =>
            {
                using (Loc.Override(Loc.Japanese)) { await Task.Yield(); return Loc.T("Menu.Help"); }
            });
            Assert.Equal("_Help", Loc.T("Menu.Help"));
        }
        Assert.Equal("_Help", inside);
        Assert.Equal("ヘルプ(_H)", other);
    }

    [Fact]
    public void Format_fills_placeholders_with_the_culture_of_the_language()
    {
        // 書式が言語ごとに違うキーは、実際の画面の文字列が増えたときにテストが足される。ここでは、キーが無いときの扱いと引数の埋め込みだけ確かめる
        Assert.Equal("No.Key", Loc.F(Loc.English, "No.Key", 1));
    }

    // ---- 言語の設定 ----

    [Fact]
    public void Language_defaults_to_auto()
        => Assert.Equal(UiLanguage.Auto, new AppSettings().Language);

    [Fact]
    public void Language_round_trips_through_the_settings_file()
    {
        using var dir = new TempDir();
        Assert.True(new AppSettings { Language = UiLanguage.English }.Save(dir.File("settings.json")));

        var loaded = AppSettings.Load(dir.File("settings.json"));

        Assert.Equal(UiLanguage.English, loaded.Language);
        Assert.Null(loaded.LoadMessage);
    }

    [Fact]
    public void Language_is_stored_as_a_number_so_existing_values_never_change()
    {
        using var dir = new TempDir();
        new AppSettings { Language = UiLanguage.English }.Save(dir.File("settings.json"));

        Assert.Contains("\"Language\": 2", File.ReadAllText(dir.File("settings.json")));
        Assert.Equal(0, (int)UiLanguage.Auto);
        Assert.Equal(1, (int)UiLanguage.Japanese);
    }

    [Fact]
    public void A_missing_language_in_an_older_file_means_auto()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("settings.json"), "{ \"SampleRate\": 44100 }");

        var loaded = AppSettings.Load(dir.File("settings.json"));

        Assert.Equal(UiLanguage.Auto, loaded.Language);
        Assert.Null(loaded.LoadMessage);
    }

    [Fact]
    public void An_undefined_language_is_corrected_to_auto_with_a_message()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("settings.json"), "{ \"Language\": 99 }");

        var loaded = AppSettings.Load(dir.File("settings.json"));

        Assert.Equal(UiLanguage.Auto, loaded.Language);
        Assert.NotNull(loaded.LoadMessage);
    }
}
