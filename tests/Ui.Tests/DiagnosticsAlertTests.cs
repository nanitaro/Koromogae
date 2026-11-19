using Koromogae.Audio;
using Koromogae.Ui;
using Xunit;

namespace Ui.Tests;

/// <summary>診断の行を赤くする条件と、例外・停止を 1 回だけログに残す判断。</summary>
public class DiagnosticsAlertTests
{
    static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    static DiagnosticsInfo Info(long underruns = 0, long errors = 0, double inputStall = 0, double outputStall = 0)
        => new(underruns, 0, 441_000, 20, 0, errors, inputStall, outputStall);

    [Fact]
    public void A_clean_run_is_never_red()
    {
        var alert = new DiagnosticsAlert();

        alert.Update(Info(), T0);

        Assert.False(alert.IsAlert(T0));
        Assert.False(alert.IsAlert(T0.AddMinutes(5)));
    }

    [Fact]
    public void A_new_underrun_turns_red_for_exactly_three_seconds()
    {
        var alert = new DiagnosticsAlert();
        alert.Update(Info(underruns: 0), T0);

        alert.Update(Info(underruns: 1), T0.AddSeconds(10));

        Assert.True(alert.IsAlert(T0.AddSeconds(10)));
        Assert.True(alert.IsAlert(T0.AddSeconds(12.999)));
        Assert.False(alert.IsAlert(T0.AddSeconds(13)));
    }

    [Fact]
    public void An_unchanged_underrun_count_does_not_turn_red_again()
    {
        var alert = new DiagnosticsAlert();
        alert.Update(Info(underruns: 2), T0);

        // 回数が同じ (累計) なら、その後の更新では赤くならない
        alert.Update(Info(underruns: 2), T0.AddSeconds(10));

        Assert.False(alert.IsAlert(T0.AddSeconds(10)));
    }

    [Fact]
    public void Another_underrun_while_red_extends_the_red_time()
    {
        var alert = new DiagnosticsAlert();
        alert.Update(Info(underruns: 1), T0);

        alert.Update(Info(underruns: 2), T0.AddSeconds(2));

        Assert.True(alert.IsAlert(T0.AddSeconds(4.9)));
        Assert.False(alert.IsAlert(T0.AddSeconds(5)));
    }

    [Theory]
    [InlineData(3, 0, 0)]
    [InlineData(0, 2500, 0)]
    [InlineData(0, 0, 4200)]
    public void Handler_errors_and_stalls_stay_red_for_as_long_as_they_last(long errors, double inputStall, double outputStall)
    {
        var alert = new DiagnosticsAlert();

        alert.Update(Info(errors: errors, inputStall: inputStall, outputStall: outputStall), T0);

        Assert.True(alert.IsAlert(T0));
        Assert.True(alert.IsAlert(T0.AddMinutes(10)));   // 時間では消えない
    }

    [Fact]
    public void A_stall_that_recovers_is_no_longer_red()
    {
        var alert = new DiagnosticsAlert();
        alert.Update(Info(outputStall: 3000), T0);

        alert.Update(Info(outputStall: 0), T0.AddSeconds(1));

        Assert.False(alert.IsAlert(T0.AddSeconds(1)));
    }

    [Fact]
    public void Reset_makes_the_first_underrun_of_the_next_run_visible()
    {
        // 次の動作は回数が 0 から数え直し。基準を戻さないと、前回の 5 回より少ない 1 回が見逃される
        var alert = new DiagnosticsAlert();
        alert.Update(Info(underruns: 5), T0);
        alert.Reset();

        alert.Update(Info(underruns: 1), T0.AddMinutes(1));

        Assert.True(alert.IsAlert(T0.AddMinutes(1)));
    }

    [Fact]
    public void Reset_clears_a_fault_but_lets_an_underrun_flash_run_out()
    {
        var alert = new DiagnosticsAlert();
        alert.Update(Info(underruns: 1, errors: 1), T0);

        alert.Reset();

        Assert.True(alert.IsAlert(T0.AddSeconds(1)));    // 音飛びの赤は時間が過ぎるまで残る
        Assert.False(alert.IsAlert(T0.AddSeconds(3)));   // 例外の赤は動作の終了で消える
    }

    // ---- ログに残す判断 ----

    [Fact]
    public void Nothing_is_reported_for_a_clean_run()
    {
        var alert = new DiagnosticsAlert();

        Assert.Equal(DiagnosticsAlert.Faults.None, alert.TakeNewFaults(Info(underruns: 9)));   // 音飛びだけは対象外
    }

    [Fact]
    public void Each_fault_is_reported_once_per_run()
    {
        var alert = new DiagnosticsAlert();
        var info = Info(errors: 1, inputStall: 2000);

        var first = alert.TakeNewFaults(info);
        var second = alert.TakeNewFaults(info);

        Assert.Equal(DiagnosticsAlert.Faults.HandlerErrors | DiagnosticsAlert.Faults.InputStall, first);
        Assert.Equal(DiagnosticsAlert.Faults.None, second);
    }

    [Fact]
    public void A_fault_that_appears_later_is_reported_when_it_appears()
    {
        var alert = new DiagnosticsAlert();
        Assert.Equal(DiagnosticsAlert.Faults.InputStall, alert.TakeNewFaults(Info(inputStall: 1600)));

        var later = alert.TakeNewFaults(Info(inputStall: 5000, outputStall: 1600));

        Assert.Equal(DiagnosticsAlert.Faults.OutputStall, later);   // 入力の停止は報告済み
    }

    [Fact]
    public void Faults_are_reported_again_in_the_next_run_after_Reset()
    {
        var alert = new DiagnosticsAlert();
        alert.TakeNewFaults(Info(errors: 1));
        alert.Reset();

        Assert.Equal(DiagnosticsAlert.Faults.HandlerErrors, alert.TakeNewFaults(Info(errors: 1)));
    }
}
