using Koromogae.Ui;
using Xunit;

namespace Ui.Tests;

/// <summary>入出力メーターの減衰と、クリップ表示の保持。</summary>
public class LevelMeterTests
{
    static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    // ---- クリップの判定 ----

    [Theory]
    [InlineData(0.9998f, false)]
    [InlineData(0.99985f, false)]
    [InlineData(0.99991f, true)]
    [InlineData(0.99997f, true)]   // 16bit の最大値 32767/32768 = 0.99997 もクリップに入る
    [InlineData(1.0f, true)]
    public void Input_clips_from_the_threshold_inclusive(float peak, bool clips)
    {
        var meter = LevelMeter.ForInput();

        meter.Update(peak, T0);

        Assert.Equal(clips, meter.IsClipping(T0));
    }

    [Theory]
    [InlineData(0.9999f, false)]
    [InlineData(1.0f, false)]      // ちょうど 1.0 は丸められないので、クリップではない
    [InlineData(1.0001f, true)]
    [InlineData(1.5f, true)]
    public void Output_clips_only_when_the_value_exceeds_full_scale(float peak, bool clips)
    {
        var meter = LevelMeter.ForOutput();

        meter.Update(peak, T0);

        Assert.Equal(clips, meter.IsClipping(T0));
    }

    // ---- 保持 ----

    [Fact]
    public void The_clip_stays_visible_for_300ms()
    {
        var meter = LevelMeter.ForInput();

        meter.Update(1.0f, T0);

        Assert.True(meter.IsClipping(T0.AddMilliseconds(299)));
        Assert.False(meter.IsClipping(T0.AddMilliseconds(300)));
    }

    [Fact]
    public void A_clip_shorter_than_one_timer_tick_is_still_seen_on_the_following_ticks()
    {
        // タイマーは 80ms ごと。クリップが 1 回の更新だけで終わっても、次の数回の更新で赤が見える
        var meter = LevelMeter.ForInput();
        meter.Update(1.0f, T0);

        var visible = new List<bool>();
        for (int tick = 1; tick <= 4; tick++)
        {
            var now = T0.AddMilliseconds(80 * tick);
            meter.Update(0f, now);
            visible.Add(meter.IsClipping(now));
        }

        Assert.Equal(new[] { true, true, true, false }, visible);   // 80, 160, 240ms は赤、320ms で戻る
    }

    [Fact]
    public void A_new_clip_while_red_extends_the_hold()
    {
        var meter = LevelMeter.ForOutput();
        meter.Update(2.0f, T0);

        meter.Update(2.0f, T0.AddMilliseconds(200));

        Assert.True(meter.IsClipping(T0.AddMilliseconds(499)));
        Assert.False(meter.IsClipping(T0.AddMilliseconds(500)));
    }

    // ---- メーターの値 ----

    [Fact]
    public void The_level_follows_a_louder_peak_immediately()
    {
        var meter = LevelMeter.ForInput();
        meter.Update(0.2f, T0);

        meter.Update(0.7f, T0.AddMilliseconds(80));

        Assert.Equal(0.7, meter.Level, 6);
    }

    [Fact]
    public void The_level_falls_by_the_decay_factor_each_update_when_there_is_no_peak()
    {
        var meter = LevelMeter.ForInput();
        meter.Update(1.0f, T0);

        meter.Update(0f, T0.AddMilliseconds(80));
        Assert.Equal(0.85, meter.Level, 6);
        meter.Update(0f, T0.AddMilliseconds(160));
        Assert.Equal(0.85 * 0.85, meter.Level, 6);
    }

    [Fact]
    public void A_quieter_peak_does_not_pull_the_level_below_the_decayed_value()
    {
        var meter = LevelMeter.ForInput();
        meter.Update(1.0f, T0);

        meter.Update(0.3f, T0.AddMilliseconds(80));   // 減衰後 0.85 > 0.3

        Assert.Equal(0.85, meter.Level, 6);
    }

    [Fact]
    public void The_displayed_level_never_exceeds_one()
    {
        var meter = LevelMeter.ForOutput();

        meter.Update(3.5f, T0);

        Assert.Equal(1.0, meter.Level, 6);   // 出力は 1.0 を超えることがある (クリップ中)
    }

    [Fact]
    public void Silence_reads_as_zero()
    {
        var meter = LevelMeter.ForInput();

        meter.Update(0f, T0);

        Assert.Equal(0.0, meter.Level, 6);
        Assert.False(meter.IsClipping(T0));
    }
}
