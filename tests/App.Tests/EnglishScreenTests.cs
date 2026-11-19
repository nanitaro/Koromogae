using System.IO;
using System.Windows.Automation;
using Koromogae;
using Koromogae.Audio;
using Xunit;

namespace App.Tests;

/// <summary>
/// 画面の言語を英語にして (環境変数 KOROMOGAE_LANG=en) 本体を起動し、文字が英語になっていること、
/// そして英語の文字列 (日本語より長い) でも配置が崩れない (切れない・重ならない・ボタンが隠れない) ことを確かめる。
/// 日本語の画面の同じ確認は MainWindowTests にある。
/// </summary>
[Collection("App")]
public class EnglishScreenTests
{
    const string PluginGroup = "VST plugins (processed from top to bottom / 32-bit and 64-bit can be mixed)";

    static readonly string[] PluginButtons = { "Add...", "VST3 folder...", "Remove", "Move up", "Move down", "Open editor", "Parameters..." };

    static AppHarness Start(string? settingsJson = null) => AppHarness.StartOrSkip(settingsJson, language: "en");

    // ---- 文字 ----

    [SkippableFact]
    public void The_buttons_labels_and_groups_are_english()
    {
        using var app = Start();

        Assert.Equal("Koromogae", app.WindowTitle);
        Assert.Equal("Start", app.Find("StartButton").Current.Name);
        Assert.Equal("● Record", app.Find("RecordButton").Current.Name);
        Assert.Equal("Refresh devices", app.Find("RefreshButton").Current.Name);
        Assert.Equal("Stopped", app.Find("RecordStatusText").Current.Name);
        Assert.Equal("Compression options...", app.Find("RecordOptionsButton").Current.Name);
        Assert.Equal("Also record unprocessed audio", app.Find("RecordDryCheck").Current.Name);
        Assert.Equal("Dither (Int24/16 only)", app.Find("DitherLabel").Current.Name);

        foreach (string header in new[] { "Audio settings", PluginGroup, "Playback", "Recording" }) app.FindGroup(header);
        foreach (string label in new[] { "Sound API", "Sample rate", "Recording device", "Buffer (ms)", "Recording bit depth",
                     "Internal buffer (ms)", "Playback device", "Playback bit depth", "Save folder", "File bit depth", "Format" })
            app.FindByText(label);
        foreach (string button in PluginButtons) app.FindByText(button);
    }

    [SkippableFact]
    public void The_menu_bar_is_english()
    {
        using var app = Start();

        Assert.Equal("Help", app.Find("HelpMenu").Current.Name);
        ((ExpandCollapsePattern)app.Find("HelpMenu").GetCurrentPattern(ExpandCollapsePattern.Pattern)).Expand();
        Assert.Equal("Open manual", app.Find("OpenManualMenuItem").Current.Name);
        Assert.Equal("About...", app.Find("AboutMenuItem").Current.Name);
        Assert.Equal("Open log and settings folder", app.Find("OpenDataFolderMenuItem").Current.Name);
    }

    [SkippableFact]
    public void The_combo_choices_are_english_but_the_values_are_unchanged()
    {
        using var app = Start();

        Assert.Equal(new[] { "Default", "+5", "+10", "+20", "+30", "+40", "+50", "+100" }, app.Items("CushionCombo"));
        Assert.Equal(new[] { "Auto", "Float32", "Int32", "Int24", "Int16" }, app.Items("InputFormatCombo"));
        Assert.Equal(new[] { "TPDF", "HP-TPDF", "Noise shaping", "Off" }, app.Items("DitherCombo"));
        Assert.Contains("WASAPI exclusive mode", app.Items("ApiCombo"));
        Assert.Contains("Kernel streaming (WDM-KS)", app.Items("ApiCombo"));
        Assert.Equal(AppSettings.AllowedSampleRates.Select(r => r.ToString()), app.Items("RateCombo"));
        Assert.Equal("Default", app.Selected("CushionCombo"));
        Assert.Equal("Auto", app.Selected("InputFormatCombo"));
    }

    [SkippableTheory]
    [InlineData("InputFormatCombo", 3)]
    [InlineData("CushionCombo", 3)]
    [InlineData("OutputFormatCombo", 4)]
    [InlineData("DitherCombo", 8)]
    public void The_english_tooltips_are_broken_into_lines_like_the_japanese_ones(string comboId, int lines)
    {
        using var app = Start();

        var help = app.HelpText(comboId);

        Assert.Equal(lines, help.Split('\n').Length);
        Assert.All(help.Split('\n'), line => Assert.InRange(line.TrimEnd('\r').Length, 1, 130));
        Assert.DoesNotMatch(@"[぀-ヿ一-鿿]", help);
    }

    [SkippableFact]
    public void The_flac_options_dialog_is_english()
    {
        using var app = Start("""{"RecordFormat":1,"RecordDepth":1}""");

        app.Click("RecordOptionsButton");
        var dialog = app.WaitForWindow("FLAC compression options");

        Assert.Contains("5 (standard, default)", AppHarness.SelectedIn(dialog, "FlacLevelCombo"));
        AppHarness.ClickIn(dialog, "FlacCancelButton");
        app.WaitForWindowClosed("FLAC compression options");
    }

    [SkippableFact]
    public void The_about_window_is_english()
    {
        using var app = Start();
        ((ExpandCollapsePattern)app.Find("HelpMenu").GetCurrentPattern(ExpandCollapsePattern.Pattern)).Expand();
        ((InvokePattern)app.Find("AboutMenuItem").GetCurrentPattern(InvokePattern.Pattern)).Invoke();

        var about = app.WaitForWindow("About");
        string report = about.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "AboutReportText"))
            .GetValue();

        Assert.Contains("Plugin host:", report);
        Assert.Contains("matches the app", report);
        Assert.Equal("Copy", about.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "AboutCopyButton")).Current.Name);
        AppHarness.ClickIn(about, "AboutCloseButton");
        app.WaitForWindowClosed("About");
    }

    [SkippableFact]
    public void The_settings_correction_dialog_is_english_while_the_log_stays_japanese()
    {
        using var app = Start("""{"SampleRate":12345,"Plugins":[]}""");

        var box = app.TryFindMessageBox();
        Assert.NotNull(box);
        string text = AppHarness.MessageBoxText(box!);
        AppHarness.ClickMessageBoxButton(box!, AppHarness.MessageBoxButtons(box!)[0]);

        Assert.StartsWith("Some values in the settings file were corrected on load (1).", text);
        string log = File.ReadAllText(app.LogPath);
        Assert.Contains("設定の補正", log);        // ログは画面の言語にかかわらず日本語
        Assert.Contains("起動: Koromogae", log);
    }

    // ---- 配置 (英語は日本語より長い) ----

    [SkippableFact]
    public void All_the_plugin_buttons_fit_and_need_no_scrolling_at_startup()
    {
        using var app = Start();
        var group = app.FindGroup(PluginGroup).Current.BoundingRectangle;

        foreach (string label in PluginButtons)
        {
            var button = app.FindByText(label).Current.BoundingRectangle;
            Assert.True(button.Bottom <= group.Bottom - 4, $"「{label}」が下に隠れています");
            Assert.True(button.Top >= group.Top, $"「{label}」が上に隠れています");
        }
        Assert.False(app.IsVerticallyScrollable("PluginButtonsScroll"));
    }

    [SkippableFact]
    public void At_the_minimum_height_everything_still_fits()
    {
        using var app = Start();
        double scale = app.WindowBounds.Height / 800.0;

        app.Resize(app.WindowBounds.Width, 100);
        var window = app.WindowBounds;
        var group = app.FindGroup(PluginGroup).Current.BoundingRectangle;

        Assert.InRange(window.Height / scale, 636, 644);
        Assert.True(app.FindGroup("Recording").Current.BoundingRectangle.Bottom <= window.Bottom, "The Recording group is cut off");
        Assert.True(app.FindGroup("Playback").Current.BoundingRectangle.Bottom <= window.Bottom);
        app.ScrollToBottom("PluginButtonsScroll");
        var last = app.FindByText("Parameters...").Current.BoundingRectangle;
        Assert.True(last.Bottom <= group.Bottom - 2 && last.Top >= group.Top);
    }

    [SkippableFact]
    public void No_two_controls_overlap_and_all_are_visible()
    {
        using var app = Start();
        var ids = new[]
        {
            "ApiCombo", "RateCombo", "LatencyCombo", "CushionCombo", "InputFormatCombo", "OutputFormatCombo", "DitherCombo",
            "InputCombo", "OutputCombo", "RefreshButton",
            "RecordFolderBox", "RecordBrowseButton", "RecordDepthCombo", "RecordFormatCombo", "RecordOptionsButton", "RecordDryCheck", "RecordButton", "StartButton",
        };
        var rects = ids.ToDictionary(id => id, id => app.Bounds(id));

        foreach (var (id, rect) in rects)
        {
            Assert.False(rect.IsEmpty || rect.Width < 20 || rect.Height < 10, $"{id} is not displayed ({rect})");
            Assert.False(app.Find(id).Current.IsOffscreen, $"{id} is off screen");
        }
        foreach (var a in ids)
            foreach (var b in ids.Where(b => string.CompareOrdinal(a, b) < 0))
            {
                var r = System.Windows.Rect.Intersect(rects[a], rects[b]);
                Assert.True(r.IsEmpty || r.Width * r.Height < 1, $"{a} and {b} overlap ({rects[a]} / {rects[b]})");
            }
    }

    [SkippableTheory]
    [InlineData("Sound API", "ApiCombo")]
    [InlineData("Sample rate", "RateCombo")]
    [InlineData("Recording device", "InputCombo")]
    [InlineData("Buffer (ms)", "LatencyCombo")]
    [InlineData("Recording bit depth", "InputFormatCombo")]
    [InlineData("Internal buffer (ms)", "CushionCombo")]
    [InlineData("Playback device", "OutputCombo")]
    [InlineData("Playback bit depth", "OutputFormatCombo")]
    [InlineData("Dither (Int24/16 only)", "DitherCombo")]
    public void Each_label_is_on_the_same_row_as_its_combo_and_does_not_run_into_it(string label, string comboId)
    {
        using var app = Start();

        var text = app.FindByText(label).Current.BoundingRectangle;
        var combo = app.Bounds(comboId);

        Assert.True(text.Right <= combo.Left + 1, $"{label} runs into {comboId} (label right {text.Right} / combo left {combo.Left})");
        Assert.InRange(Math.Abs((text.Top + text.Height / 2) - (combo.Top + combo.Height / 2)), 0, 12);
    }

    // ---- プラグインのロード失敗 (本体側と、別プロセスのプラグインホスト側の両方) ----

    static string RestoreFailureText(string pluginPath, string language)
    {
        string json = "{\"Plugins\":[{\"Path\":" + System.Text.Json.JsonSerializer.Serialize(pluginPath) + ",\"Enabled\":true}]}";
        using var app = AppHarness.StartOrSkip(json, language);
        var box = app.TryFindMessageBox(seconds: 20);
        Assert.NotNull(box);
        string text = AppHarness.MessageBoxText(box!);
        AppHarness.ClickMessageBoxButton(box!, AppHarness.MessageBoxButtons(box!)[0]);
        return text;
    }

    [SkippableFact]
    public void A_missing_plugin_file_is_reported_in_english()
    {
        string text = RestoreFailureText(@"C:\no\such\plugin.dll", "en");

        Assert.Contains("Some plugins could not be loaded", text);
        Assert.Contains("File not found.", text);
        Assert.Contains("removed from settings because the file was not found", text);
        Assert.Contains("Details:", text);
    }

    [SkippableTheory]
    [InlineData("en", "The VST2 entry point (VSTPluginMain) was not found")]
    [InlineData("ja", "VST2 のエントリポイント (VSTPluginMain) が見つかりません")]
    public void A_dll_that_is_not_a_plugin_is_reported_by_the_host_in_the_screen_language(string language, string expected)
    {
        // プラグインホスト (別プロセス) が返すエラーも、画面の言語になる (KOROMOGAE_LANG を引き継ぐ)
        string dll = Path.Combine(Environment.SystemDirectory, "version.dll");
        Skip.IfNot(File.Exists(dll), "System32 の DLL が見つかりません");

        string text = RestoreFailureText(dll, language);

        Assert.Contains(expected, text);
    }
}