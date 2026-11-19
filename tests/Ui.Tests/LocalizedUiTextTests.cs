using Koromogae;
using Koromogae.Audio;
using Koromogae.Ui;
using Xunit;

namespace Ui.Tests;

/// <summary>画面の文言の英語版と、ログ用の文字列が日本語のままであることの確認。言語は <see cref="Loc.Override"/> で差し替える。</summary>
public class LocalizedUiTextTests
{
    // ---- 録音の状態 ----

    [Fact]
    public void The_recording_spec_and_status_lines_are_english()
    {
        using (Loc.Override(Loc.English))
        {
            string spec = RecordingText.Spec(44100, RecordDepth.Int16, DitherMode.HpTpdf);
            Assert.Equal("44100 Hz / 2ch Int16 / Dither HP-TPDF", spec);
            Assert.Equal("44100 Hz / 2ch Int24 / Dither TPDF / FLAC level 5",
                RecordingText.Spec(44100, RecordDepth.Int24, DitherMode.Tpdf, RecordFormat.Flac, 5));
            Assert.Equal("Ready to record (X)", RecordingText.Ready("X"));
            Assert.Equal("S  /  00:00:02  Processed 1.0 MB / Unprocessed 2.0 MB",
                RecordingText.Format(TimeSpan.FromSeconds(2), 1048576, 2097152, includesDry: true, lowDisk: false, "S"));
            Assert.EndsWith("  * Disk space is running low", RecordingText.Format(TimeSpan.Zero, 0, 0, false, lowDisk: true));
        }
    }

    [Fact]
    public void The_recording_spec_for_the_log_can_stay_japanese_on_an_english_screen()
    {
        using (Loc.Override(Loc.English))
        {
            string spec = RecordingText.Spec(44100, RecordDepth.Int24, DitherMode.Tpdf, RecordFormat.Flac, 5, Loc.Japanese);
            Assert.Equal("44100 Hz / 2ch Int24 / ディザ TPDF / FLAC レベル5", spec);
        }
    }

    // ---- ディザの状態・プラグインの表記 ----

    [Fact]
    public void The_dither_status_is_english()
    {
        using (Loc.Override(Loc.English))
        {
            Assert.Equal("Where dither applies with the current settings: playback ○ (opened as Int16), recording ○ (when recording)",
                DitherStatus.Describe(FormatChoice.Int16, playbackIsActual: true, RecordDepth.Int24, recording: false));
            Assert.Equal("Where dither applies with the current settings: playback △ (depends on the format actually opened), recording ×",
                DitherStatus.Describe(FormatChoice.Auto, false, RecordDepth.Float32, false));
        }
    }

    [Fact]
    public void The_plugin_label_marks_a_stopped_host_in_english()
    {
        using (Loc.Override(Loc.English))
            Assert.Equal("Signalizer  [VST2 / 64bit]  * Plugin host stopped", PluginLabel.Format("Signalizer", false, false, true));
    }

    // ---- プラグイン復元の判断 ----

    [Fact]
    public void The_restore_messages_are_english_but_the_log_message_stays_japanese()
    {
        using (Loc.Override(Loc.English))
        {
            var missing = PluginRestorePolicy.OnFailure(@"C:\vst\a.dll", "boom", 0, fileExists: false);
            Assert.Equal("a.dll: boom (removed from settings because the file was not found)", missing.Message);
            Assert.Equal(@"見つからないプラグインを設定から削除: C:\vst\a.dll", missing.LogMessage);

            var kept = PluginRestorePolicy.OnFailure(@"C:\vst\a.dll", "boom", 0, fileExists: true, maxFailures: 3);
            Assert.Equal("a.dll: boom (kept in settings: attempt 1/3)", kept.Message);

            var many = Enumerable.Range(1, 10).Select(i => $"P{i}").ToList();
            string text = PluginRestorePolicy.FormatFailures("Some plugins could not be loaded", many, @"C:\log.txt")!;
            Assert.Contains("...and 2 more", text);
            Assert.EndsWith("Details: C:\\log.txt", text);
            Assert.Equal("The maximum number of plugins (64) has been reached.", PluginRestorePolicy.CapacityError(64, 0, 64));
        }
    }

    // ---- バージョン情報 ----

    [Fact]
    public void The_about_texts_are_english_and_the_startup_log_line_stays_japanese()
    {
        using (Loc.Override(Loc.English))
        {
            Assert.Equal("32bit: not found", AppVersionInfo.HostStatus("32bit", null, "1.0.0"));
            Assert.Equal("64bit: 1.0.0 (matches the app)", AppVersionInfo.HostStatus("64bit", "1.0.0+abc", "1.0.0"));
            Assert.Equal("64bit: 0.9.0 (does not match the app 1.0.0. Please rebuild.)", AppVersionInfo.HostStatus("64bit", "0.9.0", "1.0.0"));
            Assert.Equal("unknown", AppVersionInfo.CleanVersion(null));
            Assert.StartsWith("x86 .NET: not found (The x86 .NET 10 runtime is required to use 32-bit plugins)", AppVersionInfo.X86RuntimeStatus(Array.Empty<string>()));
            Assert.Equal("x86 .NET: 10.0.2 (available)", AppVersionInfo.X86RuntimeStatus(new[] { "10.0.2" }));
            Assert.Contains("Plugin host:", AppVersionInfo.Report(new AppVersionReport("1.0.0", ".NET 10", "Windows", "X64", "PortAudio", "1.0.0", "1.0.0", Array.Empty<string>())));

            // 起動のログ (app.log) は、画面の言語にかかわらず日本語
            Assert.Equal("起動: Koromogae 1.2.3 / .NET 10 X64 / Windows", AppVersionInfo.StartupLine("1.2.3", ".NET 10", "Windows", "X64"));
            Assert.StartsWith("起動: Koromogae 不明", AppVersionInfo.StartupLine("", ".NET 10", "Windows", "X64"));
        }
    }

    // ---- 設定ファイルの読み込みメッセージ ----

    [Fact]
    public void The_settings_load_message_is_english_on_an_english_screen()
    {
        string dir = Path.Combine(Path.GetTempPath(), "KoromogaeLocTest_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "settings.json");
            File.WriteAllText(path, "{ \"SampleRate\": 12345 }");

            using (Loc.Override(Loc.English))
                Assert.StartsWith("Some values in the settings file were corrected on load (1).", AppSettings.Load(path).LoadMessage);

            File.WriteAllText(path, "{ broken");
            using (Loc.Override(Loc.English))
                Assert.StartsWith("The settings file could not be loaded (", AppSettings.Load(path).LoadMessage);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}