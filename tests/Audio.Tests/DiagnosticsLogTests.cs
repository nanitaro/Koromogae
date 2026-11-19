using Koromogae.Audio;
using Xunit;

namespace Audio.Tests;

/// <summary>
/// 停止時に app.log へ残す診断の警告 (音飛び・例外・停止・録音の欠落があったときだけ)。
/// 利用者から app.log をもらって不具合を調べるときに頼る文面なので、内容と順序をテストで固定する。
/// </summary>
public class DiagnosticsLogTests
{
    static StreamStats Stream(long callbacks = 100, int minFrames = 441, int maxFrames = 441, double maxGapMs = 12.4, long handlerErrors = 0, string? firstError = null)
        => new(Callbacks: callbacks, XrunCallbacks: 0, StatusFlags: 0, MinFrames: minFrames, MaxFrames: maxFrames,
               MaxHandlerMs: 1, MinGapMs: 1, MaxGapMs: maxGapMs, AvgGapMs: 10, ReportedLatencyMs: 10, SampleRate: 44100,
               HandlerErrors: handlerErrors, FirstError: firstError);

    static AudioStats Stats(StreamStats? input = null, StreamStats? output = null, long underruns = 0, double maxChainMs = 2.34,
        double inputStalledMs = 0, double outputStalledMs = 0, double recordDroppedMs = 0)
        => new(input ?? Stream(), output ?? Stream(), underruns, SilentChunks: 0, Trims: 0, TrimmedFrames: 0, MinSlackFrames: 100,
               maxChainMs, DeliveredFrames: 44100, HeadroomMs: 10, inputStalledMs, outputStalledMs, recordDroppedMs);

    // ---- 出すかどうか ----

    [Fact]
    public void Nothing_is_logged_when_there_were_no_underruns_and_no_faults()
    {
        Assert.False(DiagnosticsLog.ShouldLog(Stats()));
    }

    [Fact]
    public void Underruns_alone_are_logged()
    {
        Assert.True(DiagnosticsLog.ShouldLog(Stats(underruns: 1)));
    }

    [Fact]
    public void A_callback_exception_alone_is_logged()
    {
        Assert.True(DiagnosticsLog.ShouldLog(Stats(output: Stream(handlerErrors: 3, firstError: "X: y"))));
        Assert.True(DiagnosticsLog.ShouldLog(Stats(input: Stream(handlerErrors: 1, firstError: "X: y"))));
    }

    [Fact]
    public void A_stalled_input_or_output_callback_alone_is_logged()
    {
        Assert.True(DiagnosticsLog.ShouldLog(Stats(inputStalledMs: 1600)));
        Assert.True(DiagnosticsLog.ShouldLog(Stats(outputStalledMs: 1600)));
    }

    [Fact]
    public void A_recording_drop_alone_is_logged()
    {
        Assert.True(DiagnosticsLog.ShouldLog(Stats(recordDroppedMs: 250)));
    }

    // ---- 文面 (移す前の AudioEngine.LogDiagnosticsIfNeeded と同じ文字列) ----

    [Fact]
    public void The_message_is_exactly_what_the_engine_used_to_write()
    {
        var stats = Stats(
            input: Stream(minFrames: 354, maxFrames: 1024, maxGapMs: 61.4),
            output: Stream(minFrames: 441, maxFrames: 4410, maxGapMs: 111.6),
            underruns: 2, maxChainMs: 5.84);

        string text = DiagnosticsLog.Format("動作中: WASAPI 共有モード / 44100 Hz", stats);

        Assert.Equal(
            "音飛びまたは異常がありました: 動作中: WASAPI 共有モード / 44100 Hz / " +
            "音飛び 2 回  /  捨てた音 0.0%  /  確保した余裕 10 ms  /  デバイスXRUN 0 回 / " +
            "入力コールバック間隔の最大 61ms (サイズ 354〜1024)、" +
            "出力コールバック間隔の最大 112ms (サイズ 441〜4410)、チェーン処理の最大 5.8ms",
            text);
    }

    [Fact]
    public void The_input_and_output_figures_are_not_swapped()
    {
        string text = DiagnosticsLog.Format("d", Stats(
            input: Stream(minFrames: 111, maxFrames: 222, maxGapMs: 33),
            output: Stream(minFrames: 444, maxFrames: 555, maxGapMs: 66), underruns: 1));

        int input = text.IndexOf("入力コールバック間隔の最大 33ms (サイズ 111〜222)", StringComparison.Ordinal);
        int output = text.IndexOf("出力コールバック間隔の最大 66ms (サイズ 444〜555)", StringComparison.Ordinal);
        Assert.True(input >= 0, text);
        Assert.True(output > input, text);       // 入力が先、出力が後
    }

    [Fact]
    public void The_first_callback_exception_is_included_and_the_input_one_wins()
    {
        string both = DiagnosticsLog.Format("d", Stats(
            input: Stream(handlerErrors: 1, firstError: "InvalidOperationException: 入力側"),
            output: Stream(handlerErrors: 2, firstError: "NullReferenceException: 出力側")));

        Assert.Contains("最初のコールバックの例外 InvalidOperationException: 入力側 / ", both);
        Assert.DoesNotContain("出力側", both);

        string outputOnly = DiagnosticsLog.Format("d", Stats(output: Stream(handlerErrors: 2, firstError: "NullReferenceException: 出力側")));
        Assert.Contains("最初のコールバックの例外 NullReferenceException: 出力側 / ", outputOnly);
    }

    [Fact]
    public void Without_an_exception_the_message_has_no_exception_part()
    {
        string text = DiagnosticsLog.Format("d", Stats(underruns: 1));

        Assert.DoesNotContain("最初のコールバックの例外", text);
    }

    [Fact]
    public void The_diagnostics_line_carries_stalls_and_recording_drops()
    {
        string text = DiagnosticsLog.Format("d", Stats(outputStalledMs: 2500, recordDroppedMs: 300));

        Assert.Contains("出力のコールバックが止まっています (2.5 秒)", text);
        Assert.Contains("録音の欠落 300 ms", text);
    }
}