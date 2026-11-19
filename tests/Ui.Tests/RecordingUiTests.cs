using Koromogae.Audio;
using Koromogae.Ui;
using Xunit;

namespace Ui.Tests;

/// <summary>録音に関する画面の文言: ディザの状態の 1 行、経過時間・サイズ、診断の赤。</summary>
public class RecordingUiTests
{
    // ---- ディザの状態 (ツールチップの最後の行) ----

    [Theory]
    [InlineData(FormatChoice.Float32, false, RecordDepth.Int24, false, "いまの設定でディザが働くのは: 再生 ×、録音 ○(録音するとき)")]
    [InlineData(FormatChoice.Int32, false, RecordDepth.Int16, false, "いまの設定でディザが働くのは: 再生 ×、録音 ○(録音するとき)")]
    [InlineData(FormatChoice.Int16, false, RecordDepth.Float32, false, "いまの設定でディザが働くのは: 再生 ○、録音 ×")]
    [InlineData(FormatChoice.Int24, false, RecordDepth.Int24, false, "いまの設定でディザが働くのは: 再生 ○、録音 ○(録音するとき)")]
    [InlineData(FormatChoice.Float32, false, RecordDepth.Float32, false, "いまの設定でディザが働くのは: 再生 ×、録音 ×")]
    [InlineData(FormatChoice.Auto, false, RecordDepth.Float32, false, "いまの設定でディザが働くのは: 再生 △(開いた形式で決まります)、録音 ×")]
    [InlineData(FormatChoice.Auto, false, RecordDepth.Int24, false, "いまの設定でディザが働くのは: 再生 △(開いた形式で決まります)、録音 ○(録音するとき)")]
    public void While_stopped_the_line_follows_the_selected_formats(FormatChoice playback, bool actual, RecordDepth record, bool recording, string expected)
    {
        Assert.Equal(expected, DitherStatus.Describe(playback, actual, record, recording));
    }

    [Theory]
    [InlineData(FormatChoice.Float32, RecordDepth.Int24, true, "いまの設定でディザが働くのは: 再生 ×(Float32 で開いています)、録音 ○(録音中)")]
    [InlineData(FormatChoice.Int16, RecordDepth.Int16, true, "いまの設定でディザが働くのは: 再生 ○(Int16 で開いています)、録音 ○(録音中)")]
    [InlineData(FormatChoice.Int32, RecordDepth.Float32, false, "いまの設定でディザが働くのは: 再生 ×(Int32 で開いています)、録音 ×")]
    [InlineData(FormatChoice.Int24, RecordDepth.Int24, false, "いまの設定でディザが働くのは: 再生 ○(Int24 で開いています)、録音 ○(録音するとき)")]
    public void While_running_the_playback_part_uses_the_format_that_was_actually_opened(FormatChoice opened, RecordDepth record, bool recording, string expected)
    {
        Assert.Equal(expected, DitherStatus.Describe(opened, playbackIsActual: true, record, recording));
    }

    [Fact]
    public void A_float32_recording_never_says_recording_even_while_recording()
    {
        Assert.EndsWith("録音 ×", DitherStatus.Describe(FormatChoice.Int16, true, RecordDepth.Float32, recording: true));
    }

    // ---- 経過時間・サイズ ----

    [Theory]
    [InlineData(0, "00:00:00")]
    [InlineData(59.9, "00:00:59")]
    [InlineData(60, "00:01:00")]
    [InlineData(754, "00:12:34")]
    [InlineData(3600, "01:00:00")]
    [InlineData(100 * 3600 + 5, "100:00:05")]       // 100 時間を超えても桁を増やすだけ
    [InlineData(-5, "00:00:00")]
    public void Elapsed_time_is_hours_minutes_seconds(double seconds, string expected)
    {
        Assert.Equal(expected, RecordingText.FormatElapsed(TimeSpan.FromSeconds(seconds)));
    }

    [Theory]
    [InlineData(0L, "0.0 MB")]
    [InlineData(1048576L, "1.0 MB")]
    [InlineData(125_829_120L, "120.0 MB")]
    [InlineData(1_073_741_823L, "1024.0 MB")]      // 1GB の手前は MB のまま
    [InlineData(1_073_741_824L, "1.00 GB")]
    [InlineData(5_368_709_120L, "5.00 GB")]
    public void Sizes_switch_from_MB_to_GB_at_one_gigabyte(long bytes, string expected)
    {
        Assert.Equal(expected, RecordingText.FormatSize(bytes));
    }

    [Fact]
    public void The_status_line_shows_time_and_size_and_splits_the_sizes_when_dry_is_recorded()
    {
        Assert.Equal("00:00:10  1.0 MB", RecordingText.Format(TimeSpan.FromSeconds(10), 1048576, 0, includesDry: false, lowDisk: false));
        Assert.Equal("00:00:10  加工後 1.0 MB / 加工前 2.0 MB",
            RecordingText.Format(TimeSpan.FromSeconds(10), 1048576, 2097152, includesDry: true, lowDisk: false));
    }

    [Fact]
    public void A_low_disk_warning_is_appended()
    {
        Assert.EndsWith("※ディスクの空きが少なくなっています",
            RecordingText.Format(TimeSpan.FromSeconds(1), 0, 0, includesDry: false, lowDisk: true));
    }

    // ---- 録音ファイルの形式 (録音グループの状態表示) ----

    [Theory]
    [InlineData(44100, RecordDepth.Int16, DitherMode.HpTpdf, "44100 Hz / 2ch Int16 / ディザ HP-TPDF")]
    [InlineData(48000, RecordDepth.Int24, DitherMode.Tpdf, "48000 Hz / 2ch Int24 / ディザ TPDF")]
    [InlineData(96000, RecordDepth.Int24, DitherMode.Off, "96000 Hz / 2ch Int24 / ディザ オフ")]
    [InlineData(48000, RecordDepth.Int24, DitherMode.Shaped, "48000 Hz / 2ch Int24 / ディザ ノイズシェイピング")]
    [InlineData(48000, RecordDepth.Float32, DitherMode.Shaped, "48000 Hz / 2ch Float32 / ディザ オフ")]
    [InlineData(48000, RecordDepth.Float32, DitherMode.Tpdf, "48000 Hz / 2ch Float32 / ディザ オフ")]       // Float32 は丸めないので、方式を選んでいても「オフ」
    [InlineData(192000, RecordDepth.Float32, DitherMode.HpTpdf, "192000 Hz / 2ch Float32 / ディザ オフ")]
    public void The_spec_line_shows_rate_channels_depth_and_the_dither_that_really_applies(int rate, RecordDepth depth, DitherMode mode, string expected)
    {
        Assert.Equal(expected, RecordingText.Spec(rate, depth, mode));
    }

    [Theory]
    [InlineData(5, "44100 Hz / 2ch Int24 / ディザ TPDF / FLAC レベル5")]
    [InlineData(0, "44100 Hz / 2ch Int24 / ディザ TPDF / FLAC レベル0")]
    [InlineData(8, "44100 Hz / 2ch Int24 / ディザ TPDF / FLAC レベル8")]
    public void The_spec_for_flac_ends_with_the_format_and_the_level(int level, string expected)
    {
        Assert.Equal(expected, RecordingText.Spec(44100, RecordDepth.Int24, DitherMode.Tpdf, RecordFormat.Flac, level));
    }

    [Fact]
    public void The_spec_for_wav_has_no_format_suffix_as_before()
    {
        Assert.Equal("44100 Hz / 2ch Int24 / ディザ TPDF", RecordingText.Spec(44100, RecordDepth.Int24, DitherMode.Tpdf, RecordFormat.Wav, 8));
        Assert.Equal("44100 Hz / 2ch Int24 / ディザ TPDF", RecordingText.Spec(44100, RecordDepth.Int24, DitherMode.Tpdf));
    }

    [Fact]
    public void A_flac_status_line_has_the_format_then_time_and_compressed_size()
    {
        string spec = RecordingText.Spec(48000, RecordDepth.Int16, DitherMode.HpTpdf, RecordFormat.Flac, 5);

        string text = RecordingText.Format(TimeSpan.FromSeconds(2), 700_000, 0, includesDry: false, lowDisk: false, spec);

        Assert.Equal("48000 Hz / 2ch Int16 / ディザ HP-TPDF / FLAC レベル5  /  00:00:02  0.7 MB", text);
    }

    [Fact]
    public void Every_combination_of_depth_and_dither_has_a_label()
    {
        foreach (var depth in Enum.GetValues<RecordDepth>())
            foreach (var mode in Enum.GetValues<DitherMode>())
                Assert.Matches(@"^ディザ (TPDF|HP-TPDF|ノイズシェイピング|オフ)$", RecordingText.DitherLabel(depth, mode));
    }

    [Fact]
    public void The_spec_uses_the_same_slash_separator_as_the_playback_description()
    {
        string spec = RecordingText.Spec(44100, RecordDepth.Int24, DitherMode.Tpdf);
        Assert.Equal(2, spec.Split(" / ").Length - 1);            // サンプルレート / チャンネル数とビット深度 / ディザ
        Assert.Contains("2ch Int24", spec);                        // チャンネル数とビット深度は空白だけでつなぐ
        Assert.DoesNotContain("2ch /", spec);
        Assert.DoesNotContain("録音", spec);     // 録音グループの中なので、「録音」を繰り返さない
    }

    [Fact]
    public void While_recording_the_status_is_one_line_with_the_spec_first_then_time_and_sizes()
    {
        string spec = RecordingText.Spec(44100, RecordDepth.Int16, DitherMode.HpTpdf);

        string text = RecordingText.Format(TimeSpan.FromSeconds(4), 1_258_291, 1_258_291, includesDry: true, lowDisk: false, spec);

        Assert.Equal("44100 Hz / 2ch Int16 / ディザ HP-TPDF  /  00:00:04  加工後 1.2 MB / 加工前 1.2 MB", text);
        Assert.DoesNotContain("\n", text);
    }

    [Fact]
    public void Without_dry_the_sizes_are_a_single_number()
    {
        string text = RecordingText.Format(TimeSpan.FromSeconds(4), 1_258_291, 0, includesDry: false, lowDisk: false,
            RecordingText.Spec(44100, RecordDepth.Int24, DitherMode.Tpdf));

        Assert.Equal("44100 Hz / 2ch Int24 / ディザ TPDF  /  00:00:04  1.2 MB", text);
    }

    [Fact]
    public void Without_a_spec_the_status_is_just_time_and_size_as_before()
    {
        Assert.Equal("00:00:04  1.0 MB", RecordingText.Format(TimeSpan.FromSeconds(4), 1048576, 0, includesDry: false, lowDisk: false));
    }

    [Fact]
    public void The_spec_stays_at_the_same_place_while_the_time_and_sizes_change()
    {
        // 形式が左に固定されるので、経過時間やサイズの桁が増えても、形式の位置は動かない
        string spec = RecordingText.Spec(44100, RecordDepth.Int24, DitherMode.Tpdf);

        string early = RecordingText.Format(TimeSpan.FromSeconds(1), 10_000, 0, false, false, spec);
        string late = RecordingText.Format(TimeSpan.FromHours(12), 90_000_000_000, 0, false, false, spec);

        Assert.StartsWith(spec, early);
        Assert.StartsWith(spec, late);
    }

    [Fact]
    public void A_low_disk_warning_is_added_at_the_end_of_the_single_line()
    {
        string text = RecordingText.Format(TimeSpan.Zero, 0, 0, includesDry: false, lowDisk: true, "44100 Hz / 2ch Int24 / ディザ TPDF");

        Assert.DoesNotContain("\n", text);
        Assert.StartsWith("44100 Hz", text);
        Assert.EndsWith("※ディスクの空きが少なくなっています", text);
    }

    [Fact]
    public void Before_recording_the_ready_text_previews_the_format()
    {
        Assert.Equal("録音できます (44100 Hz / 2ch Int24 / ディザ TPDF)",
            RecordingText.Ready(RecordingText.Spec(44100, RecordDepth.Int24, DitherMode.Tpdf)));
    }

    // ---- 診断の赤・ログ ----

    static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void A_recording_gap_is_a_fault_shown_in_the_diagnostics_and_in_red()
    {
        var info = new DiagnosticsInfo(0, 0, 441_000, 20, 0, RecordDroppedMs: 250);
        var alert = new DiagnosticsAlert();

        alert.Update(info, T0);

        Assert.True(info.HasFault);
        Assert.EndsWith("録音の欠落 250 ms", info.Format());
        Assert.True(alert.IsAlert(T0.AddMinutes(5)));      // 続いている間は赤
    }

    [Fact]
    public void A_recording_gap_is_logged_once()
    {
        var info = new DiagnosticsInfo(0, 0, 441_000, 20, 0, RecordDroppedMs: 250);
        var alert = new DiagnosticsAlert();

        Assert.Equal(DiagnosticsAlert.Faults.RecordDrop, alert.TakeNewFaults(info));
        Assert.Equal(DiagnosticsAlert.Faults.None, alert.TakeNewFaults(info with { RecordDroppedMs = 900 }));
    }

    [Fact]
    public void Without_recording_gaps_the_diagnostics_do_not_mention_recording()
    {
        var info = new DiagnosticsInfo(0, 0, 441_000, 20, 0);

        Assert.DoesNotContain("録音", info.Format());
        Assert.False(info.HasFault);
    }
}
