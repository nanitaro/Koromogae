using System.Diagnostics;
using System.IO;
using System.Windows.Automation;
using Xunit;

namespace App.Tests;

/// <summary>
/// 二重起動の防止 (同じ保存先の 2 つ目は、最初のウィンドウを前面に出して終わる)・トレイからの復元 (最小化する前の状態に戻る)・
/// 「オプション」メニューの「最小化時にタスクトレイに格納する」。
/// トレイのアイコンの操作は環境に左右されるので使わず、<b>2 つ目の起動</b>が、トレイからの復元と同じ処理 (<c>RestoreFromTray</c>) を呼ぶことを使って確かめる。
/// </summary>
[Collection("App")]
public class SingleInstanceAndMinimizeTests
{
    /// <summary>「最小化時にトレイへ格納する」をオンにした設定 (既定はオフ)。</summary>
    const string TrayOn = """{"MinimizeToTray":true}""";

    static AutomationElement WaitFind(AppHarness app, string id)
    {
        for (int i = 0; i < 20; i++)
        {
            try { return app.Find(id); }
            catch (InvalidOperationException) { Thread.Sleep(150); }
        }
        return app.Find(id);
    }

    static void ExpandOptions(AppHarness app)
        => ((ExpandCollapsePattern)app.Find("OptionsMenu").GetCurrentPattern(ExpandCollapsePattern.Pattern)).Expand();

    static bool TrayChecked(AppHarness app)
    {
        ExpandOptions(app);
        return ((TogglePattern)WaitFind(app, "MinimizeToTrayMenuItem").GetCurrentPattern(TogglePattern.Pattern)).Current.ToggleState == ToggleState.On;
    }

    /// <summary>2 つ目を起動して、終了するまで待つ。終了コードを返す。</summary>
    static int RunSecond(AppHarness app, params string[] args)
    {
        using var second = app.StartAnother(args);
        Assert.True(second.WaitForExit(20_000), "2 つ目のプロセスが終了しません (二重起動になっています)");
        return second.ExitCode;
    }

    // ---- 二重起動 ----

    [SkippableFact]
    public void A_second_start_with_the_same_data_folder_exits_and_the_first_keeps_running()
    {
        using var app = AppHarness.StartOrSkip();

        Assert.Equal(0, RunSecond(app));
        Assert.True(app.IsProcessRunning);
        Assert.True(app.IsWindowShown);
        Assert.Contains("すでに起動しているため", File.ReadAllText(app.LogPath));
    }

    [SkippableFact]
    public void A_second_start_is_not_blocked_when_the_data_folders_differ()
    {
        // 別の保存先 (別の場所にコピーしたポータブル版、並列のテスト) は、互いに妨げず起動できる
        using var first = AppHarness.StartOrSkip();
        using var other = AppHarness.StartOrSkip();

        Assert.True(first.IsProcessRunning);
        Assert.True(other.IsProcessRunning);
        Assert.True(first.IsWindowShown && other.IsWindowShown);
    }

    [SkippableFact]
    public void A_second_start_brings_a_window_in_the_tray_back()
    {
        using var app = AppHarness.StartOrSkip(TrayOn);
        app.MinimizeLikeUser();
        Assert.True(AppHarness.WaitFor(() => !app.IsWindowShown), "最小化しても、トレイへ格納されません");

        Assert.Equal(0, RunSecond(app));

        Assert.True(AppHarness.WaitFor(() => app.IsWindowShown && !app.IsMinimized),
            $"トレイから戻りません (表示={app.IsWindowShown}, 最小化={app.IsMinimized})");
        Assert.True(app.IsProcessRunning);
    }

    [SkippableFact]
    public void Restoring_from_the_tray_returns_to_the_state_before_minimizing_maximized()
    {
        using var app = AppHarness.StartOrSkip(TrayOn);
        app.Maximize();
        Assert.True(AppHarness.WaitFor(() => app.IsMaximized));
        app.MinimizeLikeUser();
        Assert.True(AppHarness.WaitFor(() => !app.IsWindowShown));

        RunSecond(app);

        Assert.True(AppHarness.WaitFor(() => app.IsWindowShown && !app.IsMinimized && app.IsMaximized),
            $"最大化に戻りません (表示={app.IsWindowShown}, 最小化={app.IsMinimized}, 最大化={app.IsMaximized})");
    }

    [SkippableFact]
    public void Restoring_from_the_tray_returns_a_normal_window_to_its_position_and_size()
    {
        using var app = AppHarness.StartOrSkip(TrayOn);
        var before = app.WindowBounds;
        app.MinimizeLikeUser();
        Assert.True(AppHarness.WaitFor(() => !app.IsWindowShown));

        RunSecond(app);

        Assert.True(AppHarness.WaitFor(() => app.IsWindowShown && !app.IsMinimized));
        Assert.False(app.IsMaximized);
        var after = app.WindowBounds;
        Assert.True(Math.Abs(after.Width - before.Width) <= 2 && Math.Abs(after.Height - before.Height) <= 2, $"大きさが変わりました ({before} → {after})");
        Assert.True(Math.Abs(after.X - before.X) <= 2 && Math.Abs(after.Y - before.Y) <= 2, $"位置が変わりました ({before} → {after})");
    }

    // ---- 「オプション」メニュー ----

    [SkippableTheory]
    [InlineData("ja", "オプション(O)", "最小化時にタスクトレイに格納する")]
    [InlineData("en", "Options", "Minimize to the system tray")]
    public void The_options_menu_has_the_tray_item_in_both_languages(string language, string title, string item)
    {
        using var app = AppHarness.StartOrSkip(language: language);

        Assert.Equal(title, app.Find("OptionsMenu").Current.Name);
        Assert.Contains("Alt+O", app.Find("OptionsMenu").Current.AccessKey);
        ExpandOptions(app);
        Assert.Equal(item, WaitFind(app, "MinimizeToTrayMenuItem").Current.Name);
    }

    [SkippableFact]
    public void The_menus_are_left_aligned_in_the_order_options_language_help_and_open_inside_the_window()
    {
        using var app = AppHarness.StartOrSkip();
        var window = app.WindowBounds;
        var options = app.Bounds("OptionsMenu");
        var language = app.Bounds("LanguageMenu");
        var help = app.Bounds("HelpMenu");
        ExpandOptions(app);

        // 左から オプション / Language / ヘルプ の順で、すべて左寄せ (右端には寄せない)
        Assert.True(options.Right <= language.Left + 1, "「Language」は「オプション」の右隣にあるはずです");
        Assert.True(language.Right <= help.Left + 1, "「ヘルプ」は「Language」の右隣にあるはずです");
        Assert.True(help.Right <= window.Left + window.Width / 2, "メニューは左半分に並ぶはずです");
        var item = WaitFind(app, "MinimizeToTrayMenuItem").Current.BoundingRectangle;
        Assert.True(item.Left >= window.Left && item.Right <= window.Right, $"項目がウィンドウの外にはみ出しています ({item} / {window})");
    }

    [SkippableTheory]
    [InlineData(null, false)]
    [InlineData("""{"MinimizeToTray":true}""", true)]
    [InlineData("""{"MinimizeToTray":false}""", false)]
    public void The_tray_item_is_checked_by_the_saved_setting_and_is_off_by_default(string? settings, bool expected)
    {
        using var app = AppHarness.StartOrSkip(settings);

        Assert.Equal(expected, TrayChecked(app));
    }

    [SkippableFact]
    public void Choosing_the_tray_item_saves_it_at_once()
    {
        using var app = AppHarness.StartOrSkip();
        ExpandOptions(app);
        ((InvokePattern)WaitFind(app, "MinimizeToTrayMenuItem").GetCurrentPattern(InvokePattern.Pattern)).Invoke();

        Assert.True(AppHarness.WaitFor(() => File.Exists(app.SettingsPath) && app.SavedSettings().GetProperty("MinimizeToTray").GetBoolean()),
            "選んだ直後に、設定ファイルへ保存されません");
        Assert.True(TrayChecked(app));
    }

    // ---- 最小化の動作 ----

    [SkippableFact]
    public void With_the_setting_off_minimizing_keeps_the_taskbar_window_and_does_not_hide_it()
    {
        using var app = AppHarness.StartOrSkip("""{"MinimizeToTray":false}""");
        app.MinimizeLikeUser();

        Assert.True(AppHarness.WaitFor(() => app.IsMinimized));
        Thread.Sleep(1000);
        Assert.True(app.IsWindowShown, "設定が off なのに、ウィンドウが隠れました (トレイへ格納されています)");
        Assert.True(app.IsMinimized);
    }

    [SkippableFact]
    public void With_the_setting_off_a_second_start_restores_a_minimized_window()
    {
        using var app = AppHarness.StartOrSkip("""{"MinimizeToTray":false}""");
        app.MinimizeLikeUser();
        Assert.True(AppHarness.WaitFor(() => app.IsMinimized));

        RunSecond(app);

        Assert.True(AppHarness.WaitFor(() => app.IsWindowShown && !app.IsMinimized), "最小化から戻りません");
    }

    [SkippableFact]
    public void Switching_the_setting_on_the_menu_changes_what_minimizing_does()
    {
        using var app = AppHarness.StartOrSkip();
        ExpandOptions(app);
        ((InvokePattern)WaitFind(app, "MinimizeToTrayMenuItem").GetCurrentPattern(InvokePattern.Pattern)).Invoke();   // on にする (既定は off)
        // 押した操作は非同期に処理される。設定が保存されて (= 反映されて) から最小化する
        Assert.True(AppHarness.WaitFor(() => File.Exists(app.SettingsPath) && app.SavedSettings().GetProperty("MinimizeToTray").GetBoolean()));
        app.MinimizeLikeUser();
        Assert.True(AppHarness.WaitFor(() => !app.IsWindowShown), "on にしたのに、トレイへ格納されません");
    }
}
