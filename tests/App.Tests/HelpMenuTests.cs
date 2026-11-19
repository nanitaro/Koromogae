using System.Diagnostics;
using System.IO;
using System.Windows.Automation;
using Xunit;

namespace App.Tests;

/// <summary>
/// メニューバーの「ヘルプ」(バージョン情報・ログ・設定フォルダを開く) と、起動ログの版番号を、本体を実際に起動して確かめる。
/// 版番号は決め打ちせず、実行ファイルに埋め込まれた値 (Directory.Build.props の Version) と照らし合わせる。
/// </summary>
[Collection("App")]
public class HelpMenuTests
{
    static string ExpectedVersion => FileVersionInfo.GetVersionInfo(AppHarness.ExePath).ProductVersion!.Split('+')[0];

    static void ExpandHelpMenu(AppHarness app)
        => ((ExpandCollapsePattern)app.Find("HelpMenu").GetCurrentPattern(ExpandCollapsePattern.Pattern)).Expand();

    static void ChooseHelpItem(AppHarness app, string itemId)
    {
        ExpandHelpMenu(app);
        ((InvokePattern)app.Find(itemId).GetCurrentPattern(InvokePattern.Pattern)).Invoke();
    }

    [SkippableFact]
    public void The_help_menu_has_the_manual_about_and_open_folder_items_in_that_order()
    {
        using var app = AppHarness.StartOrSkip();

        Assert.Equal("ヘルプ(H)", app.Find("HelpMenu").Current.Name);
        ExpandHelpMenu(app);
        Assert.Equal("マニュアルを開く(M)", app.Find("OpenManualMenuItem").Current.Name);
        Assert.Equal("バージョン情報(A)...", app.Find("AboutMenuItem").Current.Name);
        Assert.Equal("ログ・設定フォルダを開く(L)", app.Find("OpenDataFolderMenuItem").Current.Name);
        Assert.True(app.Bounds("OpenManualMenuItem").Top < app.Bounds("AboutMenuItem").Top);                // マニュアルが先頭
        Assert.True(app.Bounds("AboutMenuItem").Top < app.Bounds("OpenDataFolderMenuItem").Top);
        Assert.True(app.IsEnabled("OpenManualMenuItem"));       // 押すとブラウザが開いてしまうので、存在と有効だけを確かめる (押したときの案内は、マニュアルを消したコピーで別に確かめる)
        Assert.True(app.IsEnabled("AboutMenuItem"));
        Assert.True(app.IsEnabled("OpenDataFolderMenuItem"));   // 押すとエクスプローラーが開いてしまうので、存在と有効だけを確かめる
    }

    [SkippableFact]
    public void The_help_menu_is_left_aligned_after_options_and_language()
    {
        using var app = AppHarness.StartOrSkip();
        var help = app.Bounds("HelpMenu");
        var language = app.Bounds("LanguageMenu");
        var window = app.WindowBounds;

        Assert.True(language.Right <= help.Left + 1, "ヘルプは「Language」の右隣にあるはずです");
        Assert.True(help.Right < window.Left + window.Width / 2, $"ヘルプが右半分にあります (左寄せのはずです。ヘルプの右端 {help.Right} / ウィンドウ {window})");
    }

    [SkippableFact]
    public void The_help_submenu_opens_inside_the_window()
    {
        // 左寄せなので標準の「左端そろえ」で、ポップアップはウィンドウの内側に開く
        using var app = AppHarness.StartOrSkip();
        var window = app.WindowBounds;

        ExpandHelpMenu(app);

        foreach (string id in new[] { "OpenManualMenuItem", "AboutMenuItem", "OpenDataFolderMenuItem" })
        {
            var item = app.Bounds(id);
            Assert.True(item.Left >= window.Left, $"{id} がウィンドウの左の外にはみ出しています (項目の左端 {item.Left} / ウィンドウの左端 {window.Left})");
            Assert.True(item.Right <= window.Right, $"{id} がウィンドウの右の外にはみ出しています (項目の右端 {item.Right} / ウィンドウの右端 {window.Right})");
        }
    }

    [SkippableFact]
    public void The_about_window_shows_the_embedded_version_the_environment_and_the_license()
    {
        using var app = AppHarness.StartOrSkip();

        ChooseHelpItem(app, "AboutMenuItem");
        var dialog = app.WaitForWindow("バージョン情報");

        Assert.Equal($"Koromogae {ExpectedVersion}", Element(dialog, "AboutHeading").Current.Name);
        string report = Element(dialog, "AboutReportText").GetValue();
        Assert.StartsWith($"Koromogae {ExpectedVersion}", report);
        Assert.Contains(".NET:", report);
        Assert.Contains("OS:", report);
        Assert.Contains("PortAudio:", report);
        Assert.Contains($"32bit: {ExpectedVersion} (本体と一致)", report);     // ホストも同じ版 (ビルドの食い違いがない)
        Assert.Contains($"64bit: {ExpectedVersion} (本体と一致)", report);
        Assert.Contains("x86 版 .NET:", report);
        Assert.DoesNotContain("保存先", report);                                    // ログ・設定フォルダの情報は出さない
        Assert.Contains("Permission is hereby granted, free of charge", Element(dialog, "AboutLicenseText").GetValue());   // 最初のタブ: LICENSE (MIT)

        AppHarness.ClickIn(dialog, "AboutCloseButton");
        app.WaitForWindowClosed("バージョン情報");
    }

    [SkippableFact]
    public void The_copy_button_reports_success()
    {
        using var app = AppHarness.StartOrSkip();
        ChooseHelpItem(app, "AboutMenuItem");
        var dialog = app.WaitForWindow("バージョン情報");

        AppHarness.ClickIn(dialog, "AboutCopyButton");

        Assert.Equal("コピーしました", WaitForName(dialog, "AboutCopyStatus"));    // クリップボードの中身は、他のアプリの影響を受けやすいので見ない
        AppHarness.ClickIn(dialog, "AboutCloseButton");
        app.WaitForWindowClosed("バージョン情報");
    }

    [SkippableFact]
    public void The_startup_is_logged_with_the_version()
    {
        using var app = AppHarness.StartOrSkip();
        app.Close();

        string log = File.ReadAllText(app.LogPath);
        Assert.Contains($"起動: Koromogae {ExpectedVersion} / ", log);
    }

    static AutomationElement Element(AutomationElement dialog, string automationId)
        => dialog.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, automationId))
           ?? throw new InvalidOperationException($"部品が見つかりません: {automationId}");

    /// <summary>ボタンの Invoke は非同期なので、表示が変わるのを少し待つ。</summary>
    static string WaitForName(AutomationElement dialog, string automationId, double seconds = 5)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        string name = "";
        while (DateTime.UtcNow < deadline)
        {
            name = Element(dialog, automationId).Current.Name;
            if (!string.IsNullOrEmpty(name)) return name;
            Thread.Sleep(100);
        }
        return name;
    }

    // ---- マニュアルを開く ----

    /// <summary>マニュアルを消した配布物のコピーを起動して、「マニュアルを開く」を押す (ブラウザは開かない)。メッセージボックスの本文を返す。</summary>
    static string PressOpenManualWithoutManuals(string language)
    {
        using var app = AppHarness.StartPortableOrSkip(markerName: "", beforeLaunch: dir =>
        {
            foreach (string manual in Directory.GetFiles(dir, "manual.*.html")) File.Delete(manual);
        }, language: language);

        ChooseHelpItem(app, "OpenManualMenuItem");
        var box = app.TryFindMessageBox();
        Assert.NotNull(box);
        string text = AppHarness.MessageBoxText(box!);
        AppHarness.ClickMessageBoxButton(box!, AppHarness.MessageBoxButtons(box!)[0]);
        Assert.Null(app.TryFindMessageBox(seconds: 1.5));        // 「OK」で閉じた
        return text;
    }

    [SkippableFact]
    public void Open_manual_tells_the_user_when_the_manual_is_missing()
    {
        string text = PressOpenManualWithoutManuals("ja");

        Assert.Contains("マニュアルが見つかりません", text);
        Assert.Contains("KoromogaePortable_", text);             // 探したフォルダ (本体のフォルダ) を示す
        Assert.Contains("manual.ja.html", text);
    }

    [SkippableFact]
    public void Open_manual_missing_manual_message_is_english_on_an_english_screen()
    {
        string text = PressOpenManualWithoutManuals("en");

        Assert.Contains("The manual was not found in:", text);
        Assert.Contains("Keep manual.ja.html / manual.en.html in the same folder as Koromogae.exe.", text);
        Assert.DoesNotMatch(@"[\u3040-\u30FF\u4E00-\u9FFF]", text.Replace("\n", " "));
    }

    [SkippableTheory]
    [InlineData("ja")]
    [InlineData("en")]
    public void The_about_window_shows_the_vst_trademark_attribution_in_both_languages(string language)
    {
        // Steinberg の VST Usage Guidelines: About ボックスに、ロゴ (使わない) の代わりに、帰属文を出す。ガイドラインの英文のまま、翻訳しない
        using var app = AppHarness.StartOrSkip(language: language);

        ChooseHelpItem(app, "AboutMenuItem");
        var dialog = app.WaitForWindow(language == "ja" ? "バージョン情報" : "About");

        Assert.Equal("VST is a registered trademark of Steinberg Media Technologies GmbH.", Element(dialog, "AboutTrademark").Current.Name);
        AppHarness.ClickIn(dialog, "AboutCloseButton");
        app.WaitForWindowClosed(language == "ja" ? "バージョン情報" : "About");
    }
}