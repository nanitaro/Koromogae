using Koromogae.Audio;
using Xunit;

namespace Audio.Tests;

/// <summary>画面に出す音飛びの診断の集計と表示。</summary>
public class DiagnosticsInfoTests
{
    [Fact]
    public void Format_shows_all_four_numbers()
    {
        var info = new DiagnosticsInfo(Underruns: 3, TrimmedFrames: 44100, DeliveredFrames: 4_410_000, HeadroomMs: 99.6, DeviceXruns: 2);

        Assert.Equal("音飛び 3 回  /  捨てた音 1.0%  /  確保した余裕 100 ms  /  デバイスXRUN 2 回", info.Format());
    }

    [Fact]
    public void Dropped_percent_is_the_share_of_the_input_that_was_discarded()
    {
        Assert.Equal(25.0, new DiagnosticsInfo(0, 250, 1000, 0, 0).DroppedPercent, 6);
        Assert.Equal(0.0, new DiagnosticsInfo(0, 0, 1000, 0, 0).DroppedPercent, 6);
    }

    [Fact]
    public void Dropped_percent_is_zero_before_any_input_arrives()
    {
        Assert.Equal(0.0, new DiagnosticsInfo(0, 0, 0, 0, 0).DroppedPercent, 6);
        Assert.Contains("捨てた音 0.0%", new DiagnosticsInfo(0, 0, 0, 0, 0).Format());
    }

    [Fact]
    public void Handler_errors_and_stalls_are_shown_only_when_present()
    {
        var clean = new DiagnosticsInfo(0, 0, 441_000, 23.2, 0);
        Assert.False(clean.HasFault);
        Assert.DoesNotContain("例外", clean.Format());
        Assert.DoesNotContain("止まって", clean.Format());

        var errors = new DiagnosticsInfo(0, 0, 441_000, 23.2, 0, HandlerErrors: 7);
        Assert.True(errors.HasFault);
        Assert.EndsWith("コールバックの例外 7 回", errors.Format());
    }

    [Fact]
    public void A_stalled_input_or_output_is_named_with_the_duration()
    {
        var input = new DiagnosticsInfo(0, 0, 0, 0, 0, InputStalledMs: 2500);
        var output = new DiagnosticsInfo(0, 0, 0, 0, 0, OutputStalledMs: 4200);

        Assert.True(input.HasFault);
        Assert.EndsWith("入力のコールバックが止まっています (2.5 秒)", input.Format());
        Assert.DoesNotContain("出力のコールバック", input.Format());
        Assert.True(output.HasFault);
        Assert.EndsWith("出力のコールバックが止まっています (4.2 秒)", output.Format());
        Assert.DoesNotContain("入力のコールバック", output.Format());
    }

    [Fact]
    public void Underruns_alone_are_not_a_fault()
    {
        // 音飛びは別の表示 (数字) で扱う。HasFault は例外・停止だけ
        Assert.False(new DiagnosticsInfo(5, 0, 1000, 20, 3).HasFault);
    }

    [Fact]
    public void A_clean_run_reads_as_all_zeros()
    {
        Assert.Equal("音飛び 0 回  /  捨てた音 0.0%  /  確保した余裕 23 ms  /  デバイスXRUN 0 回",
            new DiagnosticsInfo(0, 0, 441_000, 23.2, 0).Format());
    }
}
