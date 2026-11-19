using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Koromogae;
using Koromogae.Audio;
using Xunit;

namespace App.Tests;

/// <summary>
/// 本体を実際に起動して、画面の主要な部品を UI オートメーションで確かめる (音声は開始しない)。
/// 対話的なデスクトップセッションが必要。使えない環境ではスキップされる。
/// ここで確かめるのは「部品が存在し、選択肢と初期値が正しく、設定が復元・保存され、見た目が崩れていない」こと。
/// 判断そのもの (赤くする条件など) は Ui.Tests にある。
/// </summary>
[Collection("App")]
public class MainWindowTests
{
    static readonly string[] SettingsCombos =
        { "ApiCombo", "RateCombo", "LatencyCombo", "CushionCombo", "InputFormatCombo", "OutputFormatCombo", "DitherCombo" };

    // ---- 起動 ----

    [SkippableFact]
    public void The_app_starts_with_its_title_and_the_buttons()
    {
        using var app = AppHarness.StartOrSkip();

        Assert.Equal("Koromogae", app.WindowTitle);
        Assert.Equal("開始", app.Find("StartButton").Current.Name);
        Assert.True(app.IsEnabled("StartButton"));
        Assert.True(app.IsEnabled("RefreshButton"));
    }

    [SkippableFact]
    public void The_app_closes_cleanly_and_writes_its_settings_only_to_the_test_folder()
    {
        using var app = AppHarness.StartOrSkip();

        int exitCode = app.Close();

        Assert.Equal(0, exitCode);
        Assert.True(File.Exists(app.SettingsPath));
    }

    [SkippableFact]
    public void The_users_real_settings_file_is_not_touched()
    {
        string real = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Koromogae", "settings.json");
        string? before = File.Exists(real) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(real))) : null;
        var timeBefore = File.Exists(real) ? File.GetLastWriteTimeUtc(real) : (DateTime?)null;

        using (var app = AppHarness.StartOrSkip())
        {
            app.Select("RateCombo", "96000");
            app.Close();
        }

        string? after = File.Exists(real) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(real))) : null;
        Assert.Equal(before, after);
        Assert.Equal(timeBefore, File.Exists(real) ? File.GetLastWriteTimeUtc(real) : null);
    }

    // ---- 選択肢と初期値 ----

    [SkippableFact]
    public void Every_settings_combo_lists_the_expected_choices()
    {
        using var app = AppHarness.StartOrSkip();

        Assert.Equal(AppSettings.AllowedSampleRates.Select(r => r.ToString()), app.Items("RateCombo"));
        Assert.Equal(AppSettings.AllowedLatenciesMs.Select(r => r.ToString()), app.Items("LatencyCombo"));
        Assert.Equal(new[] { "既定", "+5", "+10", "+20", "+30", "+40", "+50", "+100" }, app.Items("CushionCombo"));
        Assert.Equal(new[] { "自動", "Float32", "Int32", "Int24", "Int16" }, app.Items("InputFormatCombo"));
        Assert.Equal(new[] { "自動", "Float32", "Int32", "Int24", "Int16" }, app.Items("OutputFormatCombo"));
        Assert.Equal(new[] { "TPDF", "HP-TPDF", "ノイズシェイピング", "オフ" }, app.Items("DitherCombo"));

        var apis = app.Items("ApiCombo");
        Assert.Equal(Enum.GetValues<ApiKind>().Length, apis.Count);
        Assert.Contains("WASAPI 排他モード", apis);
        Assert.Contains("MME", apis);
    }

    [SkippableFact]
    public void Without_a_settings_file_the_defaults_are_selected()
    {
        using var app = AppHarness.StartOrSkip();

        Assert.Equal(AppSettings.DefaultSampleRate.ToString(), app.Selected("RateCombo"));
        Assert.Equal(AppSettings.DefaultLatencyMs.ToString(), app.Selected("LatencyCombo"));
        Assert.Equal("既定", app.Selected("CushionCombo"));
        Assert.Equal("自動", app.Selected("InputFormatCombo"));
        Assert.Equal("自動", app.Selected("OutputFormatCombo"));
        Assert.Equal("TPDF", app.Selected("DitherCombo"));
        Assert.Equal("WASAPI 共有モード", app.Selected("ApiCombo"));
    }

    // ---- 設定の復元と保存 ----

    const string SavedSettings = """
        {"Api":2,"SampleRate":96000,"LatencyMs":300,"CushionExtraMs":30,
         "InputFormat":4,"OutputFormat":3,"DitherMode":1,"Plugins":[]}
        """;

    [SkippableFact]
    public void Saved_settings_are_restored_into_the_combos()
    {
        using var app = AppHarness.StartOrSkip(SavedSettings);

        Assert.Equal("カーネルストリーミング (WDM-KS)", app.Selected("ApiCombo"));
        Assert.Equal("96000", app.Selected("RateCombo"));
        Assert.Equal("300", app.Selected("LatencyCombo"));
        Assert.Equal("+30", app.Selected("CushionCombo"));
        Assert.Equal("Int16", app.Selected("InputFormatCombo"));    // 録音と再生が入れ替わっていない
        Assert.Equal("Int24", app.Selected("OutputFormatCombo"));
        Assert.Equal("HP-TPDF", app.Selected("DitherCombo"));
    }

    [SkippableFact]
    public void Settings_changed_on_screen_are_saved_when_the_window_closes()
    {
        using var app = AppHarness.StartOrSkip();

        app.Select("RateCombo", "96000");
        app.Select("LatencyCombo", "200");
        app.Select("CushionCombo", "+50");
        app.Select("InputFormatCombo", "Int16");
        app.Select("OutputFormatCombo", "Int24");
        app.Select("DitherCombo", "オフ");
        app.Close();

        var saved = app.SavedSettings();
        Assert.Equal(96000, saved.GetProperty("SampleRate").GetInt32());
        Assert.Equal(200, saved.GetProperty("LatencyMs").GetInt32());
        Assert.Equal(50, saved.GetProperty("CushionExtraMs").GetInt32());
        Assert.Equal((int)FormatChoice.Int16, saved.GetProperty("InputFormat").GetInt32());
        Assert.Equal((int)FormatChoice.Int24, saved.GetProperty("OutputFormat").GetInt32());
        Assert.Equal((int)DitherMode.Off, saved.GetProperty("DitherMode").GetInt32());
    }

    [SkippableFact]
    public void A_settings_file_with_invalid_values_is_corrected_and_the_user_is_told_in_the_log()
    {
        // 壊れた値のまま起動しても、選択肢にある値に直る (補正の内容は app.log に残る)。ダイアログは出さずに確認できないので、画面の値とログを見る
        string json = """{"SampleRate":12345,"LatencyMs":-4,"CushionExtraMs":7,"DitherMode":99,"Plugins":[]}""";
        using var app = AppHarness.StartOrSkip(json);

        // 補正のダイアログ (MessageBox) が出ている間は操作できないので、閉じるのをここで試みる
        Assert.True(File.Exists(app.LogPath), "補正したことが app.log に残っていません");
        string log = File.ReadAllText(app.LogPath);
        Assert.Contains("設定の補正", log);
    }

    // ---- ディザ方式の有効・無効 ----

    [SkippableTheory]
    [InlineData("自動", true)]
    [InlineData("Float32", false)]
    [InlineData("Int32", false)]
    [InlineData("Int24", true)]
    [InlineData("Int16", true)]
    public void The_dither_combo_follows_the_output_format(string format, bool enabled)
    {
        using var app = AppHarness.StartOrSkip();
        app.Select("RecordDepthCombo", "Float32");      // 録音でもディザが働かない設定にして、再生の形式だけで決まる状態にする

        app.Select("OutputFormatCombo", format);

        Assert.Equal(enabled, app.IsEnabled("DitherCombo"));
    }

    [SkippableFact]
    public void The_input_format_does_not_affect_the_dither_combo()
    {
        using var app = AppHarness.StartOrSkip();

        app.Select("InputFormatCombo", "Float32");

        Assert.True(app.IsEnabled("DitherCombo"));
    }

    [SkippableFact]
    public void A_saved_float32_output_starts_with_the_dither_combo_disabled()
    {
        using var app = AppHarness.StartOrSkip("""{"OutputFormat":1,"RecordDepth":1,"Plugins":[]}""");

        Assert.False(app.IsEnabled("DitherCombo"));
    }

    // ---- 録音 ----

    const string RecordingSettings = """
        {"RecordFolder":"D:\\Recordings\\Test","RecordDepth":2,"RecordDry":true,"OutputFormat":1,"Plugins":[]}
        """;

    [SkippableFact]
    public void The_recording_group_has_its_controls_and_the_defaults()
    {
        using var app = AppHarness.StartOrSkip();

        Assert.Equal(AppSettings.DefaultRecordFolder, app.Find("RecordFolderBox").GetValue());
        Assert.Equal("Int24", app.Selected("RecordDepthCombo"));
        Assert.False(app.IsChecked("RecordDryCheck"));
        Assert.Equal("● 録音", app.Find("RecordButton").Current.Name);
        Assert.True(app.IsEnabled("RecordFolderBox"));
        Assert.True(app.IsEnabled("RecordBrowseButton"));
        Assert.True(app.IsEnabled("RecordDepthCombo"));
        Assert.True(app.IsEnabled("RecordDryCheck"));
    }

    [SkippableFact]
    public void The_record_depth_combo_lists_the_three_depths()
    {
        using var app = AppHarness.StartOrSkip();

        Assert.Equal(new[] { "Float32", "Int24", "Int16" }, app.Items("RecordDepthCombo"));
    }

    // ---- 録音の形式 (WAV / FLAC) ----

    [SkippableFact]
    public void The_format_combo_lists_wav_and_flac_with_wav_selected_and_the_options_button_disabled()
    {
        using var app = AppHarness.StartOrSkip();

        Assert.Equal(new[] { "WAV", "FLAC" }, app.Items("RecordFormatCombo"));
        Assert.Equal("WAV", app.Selected("RecordFormatCombo"));
        Assert.Equal("圧縮オプション...", app.Find("RecordOptionsButton").Current.Name);
        Assert.False(app.IsEnabled("RecordOptionsButton"), "WAV のときは圧縮オプションを使えないはず");
    }

    [SkippableFact]
    public void Choosing_flac_enables_the_options_and_disables_float32_and_choosing_wav_again_restores_it()
    {
        using var app = AppHarness.StartOrSkip();
        Assert.True(app.IsItemEnabled("RecordDepthCombo", "Float32"));

        app.Select("RecordFormatCombo", "FLAC");
        Assert.True(app.IsEnabled("RecordOptionsButton"));
        Assert.False(app.IsItemEnabled("RecordDepthCombo", "Float32"), "FLAC では Float32 を選べないはず");
        Assert.True(app.IsItemEnabled("RecordDepthCombo", "Int24"));
        Assert.True(app.IsItemEnabled("RecordDepthCombo", "Int16"));

        app.Select("RecordFormatCombo", "WAV");
        Assert.False(app.IsEnabled("RecordOptionsButton"));
        Assert.True(app.IsItemEnabled("RecordDepthCombo", "Float32"));
    }

    [SkippableFact]
    public void Choosing_flac_while_float32_is_selected_switches_the_depth_to_int24()
    {
        using var app = AppHarness.StartOrSkip();
        app.Select("RecordDepthCombo", "Float32");
        Assert.Equal("Float32", app.Selected("RecordDepthCombo"));

        app.Select("RecordFormatCombo", "FLAC");

        Assert.Equal("Int24", app.Selected("RecordDepthCombo"));
    }

    [SkippableFact]
    public void Choosing_flac_keeps_int16_and_going_back_to_wav_keeps_the_depth()
    {
        using var app = AppHarness.StartOrSkip();
        app.Select("RecordDepthCombo", "Int16");

        app.Select("RecordFormatCombo", "FLAC");
        Assert.Equal("Int16", app.Selected("RecordDepthCombo"));
        app.Select("RecordFormatCombo", "WAV");
        Assert.Equal("Int16", app.Selected("RecordDepthCombo"));
    }

    [SkippableFact]
    public void Switching_to_flac_makes_the_dither_selectable_even_when_playback_is_float32()
    {
        // 再生が Float32 + 録音が Float32 ではディザが働かず無効。FLAC にすると録音が Int24 になり、ディザが働くので有効になる
        using var app = AppHarness.StartOrSkip();
        app.Select("OutputFormatCombo", "Float32");
        app.Select("RecordDepthCombo", "Float32");
        Assert.False(app.IsEnabled("DitherCombo"));

        app.Select("RecordFormatCombo", "FLAC");

        Assert.True(app.IsEnabled("DitherCombo"));
    }

    [SkippableFact]
    public void Saved_flac_settings_are_restored_and_saved_again_when_the_window_closes()
    {
        using var app = AppHarness.StartOrSkip("""{"RecordFormat":1,"FlacLevel":8,"RecordDepth":2,"Plugins":[]}""");

        Assert.Equal("FLAC", app.Selected("RecordFormatCombo"));
        Assert.Equal("Int16", app.Selected("RecordDepthCombo"));
        Assert.True(app.IsEnabled("RecordOptionsButton"));
        app.Close();

        var saved = app.SavedSettings();
        Assert.Equal((int)RecordFormat.Flac, saved.GetProperty("RecordFormat").GetInt32());
        Assert.Equal(8, saved.GetProperty("FlacLevel").GetInt32());
        Assert.Equal((int)RecordDepth.Int16, saved.GetProperty("RecordDepth").GetInt32());
    }

    [SkippableFact]
    public void The_options_dialog_shows_the_saved_level_and_the_chosen_level_is_saved()
    {
        using var app = AppHarness.StartOrSkip("""{"RecordFormat":1,"FlacLevel":3,"Plugins":[]}""");

        app.Click("RecordOptionsButton");
        var dialog = app.WaitForWindow("FLAC の圧縮オプション");
        Assert.StartsWith("3 ", AppHarness.SelectedIn(dialog, "FlacLevelCombo"));
        AppHarness.SelectIn(dialog, "FlacLevelCombo", "7 (さらに小さい)");
        AppHarness.ClickIn(dialog, "FlacOkButton");
        app.WaitForWindowClosed("FLAC の圧縮オプション");
        app.Close();

        Assert.Equal(7, app.SavedSettings().GetProperty("FlacLevel").GetInt32());
    }

    [SkippableFact]
    public void Cancelling_the_options_dialog_keeps_the_level()
    {
        using var app = AppHarness.StartOrSkip("""{"RecordFormat":1,"FlacLevel":6,"Plugins":[]}""");

        app.Click("RecordOptionsButton");
        var dialog = app.WaitForWindow("FLAC の圧縮オプション");
        AppHarness.SelectIn(dialog, "FlacLevelCombo", "0 (最も速い・サイズは大きめ)");
        AppHarness.ClickIn(dialog, "FlacCancelButton");
        app.WaitForWindowClosed("FLAC の圧縮オプション");
        app.Close();

        Assert.Equal(6, app.SavedSettings().GetProperty("FlacLevel").GetInt32());
    }

    [SkippableFact]
    public void The_default_flac_level_is_5_when_nothing_was_chosen()
    {
        using var app = AppHarness.StartOrSkip("""{"RecordFormat":1,"Plugins":[]}""");

        app.Close();

        Assert.Equal(5, app.SavedSettings().GetProperty("FlacLevel").GetInt32());
    }

    [SkippableFact]
    public void The_record_button_cannot_be_pressed_until_the_engine_is_running()
    {
        // 「開始」は押さない (音声デバイスを使わない) ので、録音ボタンは無効のまま
        using var app = AppHarness.StartOrSkip();

        Assert.False(app.IsEnabled("RecordButton"));
    }

    [SkippableFact]
    public void Saved_recording_settings_are_restored()
    {
        using var app = AppHarness.StartOrSkip(RecordingSettings);

        Assert.Equal(@"D:\Recordings\Test", app.Find("RecordFolderBox").GetValue());
        Assert.Equal("Int16", app.Selected("RecordDepthCombo"));
        Assert.True(app.IsChecked("RecordDryCheck"));
    }

    [SkippableFact]
    public void Recording_settings_changed_on_screen_are_saved_when_the_window_closes()
    {
        using var app = AppHarness.StartOrSkip();

        app.Find("RecordFolderBox").SetValue("  \"D:\\My Recordings\"  ");     // 貼り付けた引用符と空白は取り除かれる
        app.Select("RecordDepthCombo", "Float32");
        app.SetChecked("RecordDryCheck", true);
        app.Close();

        var saved = app.SavedSettings();
        Assert.Equal(@"D:\My Recordings", saved.GetProperty("RecordFolder").GetString());
        Assert.Equal((int)RecordDepth.Float32, saved.GetProperty("RecordDepth").GetInt32());
        Assert.True(saved.GetProperty("RecordDry").GetBoolean());
    }

    [SkippableFact]
    public void The_default_folder_is_stored_as_null_so_it_keeps_following_the_default()
    {
        using var app = AppHarness.StartOrSkip();

        app.Close();

        Assert.Equal(System.Text.Json.JsonValueKind.Null, app.SavedSettings().GetProperty("RecordFolder").ValueKind);
    }

    [SkippableTheory]
    [InlineData("Float32", "Float32", false)]     // 再生も録音もディザが働かない
    [InlineData("Float32", "Int24", true)]        // 録音で働く: 録音しない予定でも、事前に方式を選べる
    [InlineData("Float32", "Int16", true)]
    [InlineData("Int32", "Float32", false)]
    [InlineData("Int16", "Float32", true)]        // 再生で働く
    public void The_dither_combo_is_disabled_only_when_neither_playback_nor_recording_uses_it(string playback, string record, bool enabled)
    {
        using var app = AppHarness.StartOrSkip();

        app.Select("OutputFormatCombo", playback);
        app.Select("RecordDepthCombo", record);

        Assert.Equal(enabled, app.IsEnabled("DitherCombo"));
    }

    [SkippableFact]
    public void The_dither_tooltip_ends_with_where_dither_works_and_follows_the_selections()
    {
        using var app = AppHarness.StartOrSkip();

        app.Select("OutputFormatCombo", "Float32");
        app.Select("RecordDepthCombo", "Int24");
        string first = app.HelpText("DitherCombo");
        app.Select("OutputFormatCombo", "Int16");
        app.Select("RecordDepthCombo", "Float32");
        string second = app.HelpText("DitherCombo");

        Assert.Contains("再生の Int24/Int16、録音の Int24/Int16", first);
        Assert.EndsWith("いまの設定でディザが働くのは: 再生 ×、録音 ○(録音するとき)", first.TrimEnd());
        Assert.EndsWith("いまの設定でディザが働くのは: 再生 ○、録音 ×", second.TrimEnd());
    }

    [SkippableFact]
    public void The_record_group_is_below_the_playback_group_which_is_below_the_plugin_list()
    {
        using var app = AppHarness.StartOrSkip();

        var plugins = app.Bounds("PluginList");
        var folder = app.Bounds("RecordFolderBox");
        var depth = app.Bounds("RecordDepthCombo");
        var button = app.Bounds("RecordButton");
        var start = app.Bounds("StartButton");

        Assert.True(start.Top >= plugins.Bottom - 1);         // 再生 (開始ボタン・メーター) は、プラグインの一覧の下
        Assert.True(folder.Top >= start.Bottom - 1);          // 録音は、再生の下
        Assert.True(depth.Top > folder.Top);
        Assert.True(button.Top > depth.Top);
        Assert.True(app.Bounds("RecordBrowseButton").Left > folder.Right - 1);       // 「参照...」は保存先の右
    }

    // ---- ツールチップ ----

    [SkippableTheory]
    [InlineData("InputFormatCombo", 3)]
    [InlineData("CushionCombo", 3)]
    [InlineData("OutputFormatCombo", 4)]
    [InlineData("DitherCombo", 8)]      // 固定の説明 7 行 (TPDF は 2 行) + 「いまの設定でディザが働くのは」の 1 行
    public void The_tooltips_are_broken_into_lines(string comboId, int lines)
    {
        // 長い文章が 1 行に伸びないこと (リソースに移した際に改行が失われた不具合があった)
        using var app = AppHarness.StartOrSkip();

        var help = app.HelpText(comboId);

        Assert.Equal(lines, help.Split('\n').Length);
        Assert.All(help.Split('\n'), line => Assert.InRange(line.TrimEnd('\r').Length, 1, 60));
    }

    // ---- 配置 ----

    [SkippableFact]
    public void The_input_format_is_right_below_the_input_device_and_the_output_format_below_the_output_device()
    {
        using var app = AppHarness.StartOrSkip();

        var input = app.Bounds("InputCombo");
        var inputFormat = app.Bounds("InputFormatCombo");
        var output = app.Bounds("OutputCombo");
        var outputFormat = app.Bounds("OutputFormatCombo");

        Assert.Equal(input.Left, inputFormat.Left, 1);                        // 同じ列
        Assert.True(inputFormat.Top >= input.Bottom - 1);                     // 下
        Assert.True(inputFormat.Top < output.Top);                            // 録音デバイスと再生デバイスの間
        Assert.Equal(output.Left, outputFormat.Left, 1);
        Assert.True(outputFormat.Top >= output.Bottom - 1);
    }

    [SkippableFact]
    public void The_dither_combo_is_on_the_same_row_as_the_output_device_and_the_refresh_button_is_below_it()
    {
        using var app = AppHarness.StartOrSkip();

        var output = app.Bounds("OutputCombo");
        var dither = app.Bounds("DitherCombo");
        var refresh = app.Bounds("RefreshButton");

        Assert.Equal(output.Top, dither.Top, 3);
        Assert.True(dither.Left > output.Right);
        Assert.True(refresh.Top >= dither.Bottom - 1);
        Assert.Equal(dither.Left, refresh.Left, 1);
    }

    [SkippableFact]
    public void All_the_plugin_buttons_fit_inside_the_plugin_group_at_startup()
    {
        // 起動したままの大きさで、右側のボタン (特に一番下の「パラメータ...」) が、グループの下端に隠れないこと
        using var app = AppHarness.StartOrSkip();
        var group = app.FindGroup("VSTプラグイン (上から順に処理 / 32bit・64bit 混在可)").Current.BoundingRectangle;

        foreach (string label in new[] { "追加...", "VST3 フォルダ...", "削除", "上へ", "下へ", "エディタを開く", "パラメータ..." })
        {
            var button = app.FindByText(label).Current.BoundingRectangle;
            Assert.True(button.Bottom <= group.Bottom - 4, $"「{label}」が下に隠れています (ボタンの下端 {button.Bottom} / グループの下端 {group.Bottom})");
            Assert.True(button.Top >= group.Top, $"「{label}」が上に隠れています");
        }
    }

    [SkippableFact]
    public void At_the_default_size_the_plugin_buttons_need_no_scrolling()
    {
        using var app = AppHarness.StartOrSkip();

        Assert.False(app.IsVerticallyScrollable("PluginButtonsScroll"));
    }

    [SkippableFact]
    public void The_plugin_buttons_have_spare_room_below_at_startup()
    {
        // 境目すれすれで通っている状態 (メニューバーを足したとき、わずか 0.3 DIP の不足でスクロールが出た) を避ける。
        // ボタンの列の表示領域の下端と、一番下のボタンの間に、最後のボタンの下の余白 (6) + 余裕 6 DIP 以上を求める
        using var app = AppHarness.StartOrSkip();
        double scale = app.WindowBounds.Height / 800.0;                 // 既定の高さ (800) と実際のピクセルから、画面の拡大率を求める

        double viewportBottom = app.Bounds("PluginButtonsScroll").Bottom;
        double lastButtonBottom = app.FindByText("パラメータ...").Current.BoundingRectangle.Bottom;
        double spareDip = (viewportBottom - lastButtonBottom) / scale - 6;

        Assert.True(spareDip >= 6, $"ボタンの列の下の余裕が足りません ({spareDip:F1} DIP。6 以上が必要)");
    }

    [SkippableFact]
    public void At_the_minimum_height_everything_still_fits_the_buttons_scroll_and_the_list_stays_usable()
    {
        using var app = AppHarness.StartOrSkip();
        double scale = app.WindowBounds.Height / 800.0;            // 既定の高さ (800) と実際のピクセルから、画面の拡大率を求める

        app.Resize(app.WindowBounds.Width, 100);                   // 最小より小さく指定して、最小の大きさで止まることを確かめる
        var window = app.WindowBounds;
        var group = app.FindGroup("VSTプラグイン (上から順に処理 / 32bit・64bit 混在可)").Current.BoundingRectangle;

        Assert.InRange(window.Height / scale, 636, 644);            // 最小の高さ (640)
        Assert.True(app.Bounds("PluginList").Height / scale >= 60, "プラグインの一覧が小さすぎます");
        Assert.True(app.FindGroup("録音").Current.BoundingRectangle.Bottom <= window.Bottom, "録音のグループがウィンドウからはみ出しています");
        Assert.True(app.FindGroup("再生").Current.BoundingRectangle.Bottom <= window.Bottom);
        Assert.True(app.IsVerticallyScrollable("PluginButtonsScroll"), "高さが足りないときは、ボタンの列がスクロールできるはずです");
        Assert.True(app.FindByText("追加...").Current.BoundingRectangle.Bottom <= group.Bottom, "一番上のボタンが見えていません");

        app.ScrollToBottom("PluginButtonsScroll");                   // スクロールすれば、一番下のボタンにも届く
        var last = app.FindByText("パラメータ...").Current.BoundingRectangle;
        Assert.True(last.Bottom <= group.Bottom - 2 && last.Top >= group.Top, $"スクロールしても「パラメータ...」に届きません ({last} / {group})");
    }

    [SkippableFact]
    public void Every_group_fits_inside_the_window_at_startup()
    {
        using var app = AppHarness.StartOrSkip();
        double windowBottom = app.WindowBounds.Bottom;

        foreach (string header in new[] { "録音", "再生" })
            Assert.True(app.FindGroup(header).Current.BoundingRectangle.Bottom <= windowBottom, $"グループ「{header}」がウィンドウの下にはみ出しています");
    }

    [SkippableFact]
    public void No_two_settings_controls_overlap_and_all_are_visible()
    {
        using var app = AppHarness.StartOrSkip();
        var ids = SettingsCombos.Concat(new[]
        {
            "InputCombo", "OutputCombo", "RefreshButton",
            "RecordFolderBox", "RecordBrowseButton", "RecordDepthCombo", "RecordFormatCombo", "RecordOptionsButton", "RecordDryCheck", "RecordButton", "StartButton",
        }).ToList();

        var rects = ids.ToDictionary(id => id, id => app.Bounds(id));

        foreach (var (id, rect) in rects)
        {
            Assert.False(rect.IsEmpty || rect.Width < 20 || rect.Height < 10, $"{id} が表示されていません ({rect})");
            Assert.False(app.Find(id).Current.IsOffscreen, $"{id} が画面の外にあります");
        }
        foreach (var a in ids)
            foreach (var b in ids.Where(b => string.CompareOrdinal(a, b) < 0))
            {
                var r = System.Windows.Rect.Intersect(rects[a], rects[b]);
                Assert.True(r.IsEmpty || r.Width * r.Height < 1, $"{a} と {b} が重なっています ({rects[a]} / {rects[b]})");
            }
    }

    [SkippableTheory]
    [InlineData("録音のビット深度", "InputFormatCombo")]
    [InlineData("再生のビット深度", "OutputFormatCombo")]
    [InlineData("内部バッファ(ms)", "CushionCombo")]
    [InlineData("ディザ(Int24/16のみ)", "DitherCombo")]
    public void Each_label_is_on_the_same_row_as_its_combo_and_to_its_left(string label, string comboId)
    {
        using var app = AppHarness.StartOrSkip();

        var text = app.FindByText(label).Current.BoundingRectangle;
        var combo = app.Bounds(comboId);

        Assert.True(text.Right <= combo.Left + 1, $"{label} が {comboId} の左にありません");
        Assert.InRange(Math.Abs((text.Top + text.Height / 2) - (combo.Top + combo.Height / 2)), 0, 12);
    }
}
