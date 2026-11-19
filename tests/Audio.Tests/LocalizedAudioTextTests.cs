using Koromogae;
using Koromogae.Audio;
using Xunit;

namespace Audio.Tests;

/// <summary>
/// 英語の画面での表示と、「ログは画面の言語にかかわらず日本語」の確認。
/// 画面の言語は <see cref="Loc.Override"/> で差し替える (並列に走るほかのテストには影響しない)。
/// </summary>
public class LocalizedAudioTextTests
{
    static DiagnosticsInfo Sample(long errors = 0, double inStall = 0, double outStall = 0, double recDrop = 0)
        => new(Underruns: 3, TrimmedFrames: 441, DeliveredFrames: 44100, HeadroomMs: 10, DeviceXruns: 2,
               HandlerErrors: errors, InputStalledMs: inStall, OutputStalledMs: outStall, RecordDroppedMs: recDrop);

    [Fact]
    public void The_diagnostics_line_is_japanese_by_default_and_english_when_the_screen_is_english()
    {
        var info = Sample();

        Assert.Equal("音飛び 3 回  /  捨てた音 1.0%  /  確保した余裕 10 ms  /  デバイスXRUN 2 回", info.Format());
        using (Loc.Override(Loc.English))
            Assert.Equal("Dropouts 3  /  Discarded 1.0%  /  Headroom 10 ms  /  Device XRUN 2", info.Format());
    }

    [Fact]
    public void Every_fault_part_is_translated()
    {
        var info = Sample(errors: 2, inStall: 2500, outStall: 1600, recDrop: 300);

        using (Loc.Override(Loc.English))
        {
            string text = info.Format();
            Assert.Contains("Callback exceptions 2", text);
            Assert.Contains("Input callback has stopped (2.5 s)", text);
            Assert.Contains("Output callback has stopped (1.6 s)", text);
            Assert.Contains("Recording lost 300 ms", text);
            Assert.DoesNotMatch(@"[\u3040-\u30FF\u4E00-\u9FFF]", text);
        }
    }

    [Fact]
    public void An_explicit_culture_wins_over_the_screen_language()
    {
        // ログに残すときは、画面が英語でも日本語を渡す
        using (Loc.Override(Loc.English))
            Assert.StartsWith("音飛び 3 回", Sample().Format(Loc.Japanese));
        using (Loc.Override(Loc.Japanese))
            Assert.StartsWith("Dropouts 3", Sample().Format(Loc.English));
    }

    [Fact]
    public void The_latency_line_is_translated()
    {
        var latency = new LatencyInfo(10, 20, 5, 15);

        Assert.Equal("遅延 約 50 ms  (入力 10 + 内部バッファ 20 + プラグイン 5 + 出力 15)", latency.Format());
        using (Loc.Override(Loc.English))
            Assert.Equal("Latency about 50 ms  (input 10 + internal buffer 20 + plugins 5 + output 15)", latency.Format());
    }

    [Theory]
    [InlineData(DitherMode.Tpdf, "Dither TPDF", "ディザ TPDF")]
    [InlineData(DitherMode.HpTpdf, "Dither HP-TPDF", "ディザ HP-TPDF")]
    [InlineData(DitherMode.Shaped, "Dither noise shaping", "ディザ ノイズシェイピング")]
    [InlineData(DitherMode.Off, "Dither off", "ディザ オフ")]
    public void The_dither_label_follows_the_language(DitherMode mode, string english, string japanese)
    {
        using (Loc.Override(Loc.English))
        {
            Assert.Equal(english, Pcm.DitherLabel(SampleFormat.Int16, mode));
            Assert.Equal(japanese, Pcm.DitherLabel(SampleFormat.Int16, mode, Loc.Japanese));
            Assert.Equal("Dither off", Pcm.DitherLabel(SampleFormat.Float32, mode));   // Float32 は方式にかかわらず「オフ」
        }
    }

    [Fact]
    public void The_log_message_stays_japanese_on_an_english_screen()
    {
        var stats = new AudioStats(
            new StreamStats(Callbacks: 100, XrunCallbacks: 0, StatusFlags: 0, MinFrames: 441, MaxFrames: 441, MaxHandlerMs: 1, MinGapMs: 1, MaxGapMs: 12.4, AvgGapMs: 10, ReportedLatencyMs: 10, SampleRate: 44100),
            new StreamStats(Callbacks: 100, XrunCallbacks: 0, StatusFlags: 0, MinFrames: 441, MaxFrames: 441, MaxHandlerMs: 1, MinGapMs: 1, MaxGapMs: 12.4, AvgGapMs: 10, ReportedLatencyMs: 10, SampleRate: 44100),
            Underruns: 2, SilentChunks: 0, Trims: 0, TrimmedFrames: 0, MinSlackFrames: 100, 2.34, DeliveredFrames: 44100, HeadroomMs: 10, 0, 0, 0);

        string japanese = DiagnosticsLog.Format("動作中: テスト", stats);
        string onEnglishScreen;
        using (Loc.Override(Loc.English)) onEnglishScreen = DiagnosticsLog.Format("動作中: テスト", stats);

        Assert.Equal(japanese, onEnglishScreen);
        Assert.Contains("音飛びまたは異常がありました", onEnglishScreen);
        Assert.Contains("音飛び 2 回", onEnglishScreen);
    }
}