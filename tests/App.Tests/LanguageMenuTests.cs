using System.Windows.Automation;
using Xunit;

namespace App.Tests;

/// <summary>
/// メニューバーの「Language」: 項目・チェック・ポップアップの位置・選んだ言語の保存。
/// 再起動まで進める確認 (新しい本体のプロセスが増える) は自動テストにせず、手動で確認した。ここでは、いつも「いいえ」で閉じる。
/// </summary>
[Collection("App")]
public class LanguageMenuTests
{
    const string AutoName = "自動 (Windows の設定) / Auto";

    static void ExpandLanguageMenu(AppHarness app)
        => ((ExpandCollapsePattern)app.Find("LanguageMenu").GetCurrentPattern(ExpandCollapsePattern.Pattern)).Expand();

    /// <summary>チェックの状態。ポップアップが閉じていると項目は見えないので、メニューを開いて読む。</summary>
    static bool Checked(AppHarness app, string id)
    {
        ExpandLanguageMenu(app);
        return ((TogglePattern)WaitFind(app, id).GetCurrentPattern(TogglePattern.Pattern)).Current.ToggleState == ToggleState.On;
    }

    /// <summary>メニューを開いた直後は、ポップアップの項目がまだ UI オートメーションに現れないことがあるので、少し待って探す。</summary>
    static AutomationElement WaitFind(AppHarness app, string id)
    {
        for (int i = 0; i < 20; i++)
        {
            try { return app.Find(id); }
            catch (InvalidOperationException) { Thread.Sleep(150); }
        }
        return app.Find(id);
    }

    static void Choose(AppHarness app, string id)
    {
        ExpandLanguageMenu(app);
        ((InvokePattern)WaitFind(app, id).GetCurrentPattern(InvokePattern.Pattern)).Invoke();
    }

    [SkippableTheory]
    [InlineData("ja", "Language(L)")]
    [InlineData("en", "Language")]
    public void The_language_menu_title_has_the_word_Language_and_the_items_are_fixed_in_every_language(string language, string title)
    {
        // 見出しは「Language」の語を含み (日本語 Language(_L) / 英語 _Language。UI オートメーションの名前は _ を除いたもの)、
        // 項目は、どの言語の画面でも見つけられるよう翻訳しない
        using var app = AppHarness.StartOrSkip(language: language);

        Assert.Equal(title, app.Find("LanguageMenu").Current.Name);
        ExpandLanguageMenu(app);
        Assert.Equal(AutoName, WaitFind(app, "LanguageAutoItem").Current.Name);
        Assert.Equal("日本語", WaitFind(app, "LanguageJapaneseItem").Current.Name);
        Assert.Equal("English", WaitFind(app, "LanguageEnglishItem").Current.Name);
    }

    [SkippableTheory]
    [InlineData("ja")]
    [InlineData("en")]
    public void Alt_L_is_the_access_key_of_the_language_menu_and_Alt_H_of_the_help_menu(string language)
    {
        using var app = AppHarness.StartOrSkip(language: language);

        Assert.Contains("Alt+L", app.Find("LanguageMenu").Current.AccessKey);
        Assert.Contains("Alt+H", app.Find("HelpMenu").Current.AccessKey);
    }

    [SkippableFact]
    public void The_language_menu_is_between_options_and_help_at_the_left_and_opens_inside_the_window()
    {
        using var app = AppHarness.StartOrSkip();
        var window = app.WindowBounds;

        var options = app.Bounds("OptionsMenu");
        var help = app.Bounds("HelpMenu");
        var language = app.Bounds("LanguageMenu");
        ExpandLanguageMenu(app);

        Assert.True(options.Right <= language.Left + 1, "「Language」は、「オプション」の右隣にあるはずです");
        Assert.True(language.Right <= help.Left + 1, "「Language」は、ヘルプの左隣にあるはずです");
        Assert.True(language.Right < window.Left + window.Width / 2, "「Language」が右半分にあります (左寄せのはずです)");
        foreach (string id in new[] { "LanguageAutoItem", "LanguageJapaneseItem", "LanguageEnglishItem" })
        {
            var item = WaitFind(app, id).Current.BoundingRectangle;
            Assert.True(item.Left >= window.Left && item.Right <= window.Right, $"{id} がウィンドウの外にはみ出しています ({item} / {window})");
        }
    }

    [SkippableTheory]
    [InlineData(null, "LanguageAutoItem")]
    [InlineData("""{"Language":1}""", "LanguageJapaneseItem")]
    [InlineData("""{"Language":2}""", "LanguageEnglishItem")]
    public void Only_the_saved_language_is_checked(string? settings, string checkedId)
    {
        using var app = AppHarness.StartOrSkip(settings);
        ExpandLanguageMenu(app);

        foreach (string id in new[] { "LanguageAutoItem", "LanguageJapaneseItem", "LanguageEnglishItem" })
            Assert.Equal(id == checkedId, Checked(app, id));
    }

    [SkippableFact]
    public void Choosing_a_language_saves_it_at_once_and_offers_a_restart_in_both_languages()
    {
        using var app = AppHarness.StartOrSkip();

        Choose(app, "LanguageEnglishItem");
        var box = app.TryFindMessageBox();
        Assert.NotNull(box);
        string text = AppHarness.MessageBoxText(box!);
        // 画面の言語が何であっても読めるよう、日本語と英語を併記する
        Assert.Contains("言語を変更しました。再起動すると反映されます。今すぐ再起動しますか?", text);
        Assert.Contains("Language changed. It takes effect after a restart. Restart now?", text);
        Assert.Equal(2, AppHarness.MessageBoxButtons(box!).Count);   // はい / いいえ

        AppHarness.ClickMessageBoxButton(box!, "いいえ", "No");   // 再起動しない

        Assert.Equal(2, app.SavedSettings().GetProperty("Language").GetInt32());   // 閉じる前に、すでに保存されている
        Assert.True(Checked(app, "LanguageEnglishItem"));
        Assert.False(Checked(app, "LanguageAutoItem"));
        Assert.Equal("開始", app.Find("StartButton").Current.Name);   // 画面の言語は、再起動するまで変わらない
    }

    [SkippableFact]
    public void Choosing_the_current_language_again_shows_no_dialog_and_keeps_the_check()
    {
        using var app = AppHarness.StartOrSkip("""{"Language":1}""");

        Choose(app, "LanguageJapaneseItem");

        Assert.Null(app.TryFindMessageBox(seconds: 1.5));
        Assert.True(Checked(app, "LanguageJapaneseItem"));   // クリックでチェックが外れない
        Assert.Equal(1, app.SavedSettings().GetProperty("Language").GetInt32());
    }

    [SkippableFact]
    public void The_chosen_language_is_still_saved_when_the_window_closes()
    {
        using var app = AppHarness.StartOrSkip();
        Choose(app, "LanguageEnglishItem");
        var box = app.TryFindMessageBox();
        AppHarness.ClickMessageBoxButton(box!, "いいえ", "No");
        Assert.Null(app.TryFindMessageBox(seconds: 2));   // 「いいえ」でダイアログが閉じた

        app.Close();

        Assert.Equal(2, app.SavedSettings().GetProperty("Language").GetInt32());
    }
}