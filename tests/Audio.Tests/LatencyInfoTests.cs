using Koromogae.Audio;
using Xunit;

namespace Audio.Tests;

/// <summary>画面に出す実遅延の内訳 (入力 + 内部バッファ + プラグイン + 出力) の集計と表示。</summary>
public class LatencyInfoTests
{
    [Fact]
    public void Total_is_the_sum_of_the_four_parts()
    {
        var latency = new LatencyInfo(InputMs: 109.6, RingMs: 20.5, PluginMs: 6.5, OutputMs: 109.6);

        Assert.Equal(246.2, latency.TotalMs, 6);
    }

    [Fact]
    public void Format_shows_each_part_and_the_total()
    {
        var latency = new LatencyInfo(110, 20, 7, 110);

        Assert.Equal("遅延 約 247 ms  (入力 110 + 内部バッファ 20 + プラグイン 7 + 出力 110)", latency.Format());
    }

    [Fact]
    public void Displayed_total_always_equals_the_sum_of_the_displayed_parts()
    {
        // 別々に丸めると 109.6+19.5+6.5+109.6 = 245.2 → 245 だが、画面の数字は 110+20+7+110 = 247。足し合わない表示にしない
        var latency = new LatencyInfo(109.6, 19.5, 6.5, 109.6);

        Assert.Equal("遅延 約 247 ms  (入力 110 + 内部バッファ 20 + プラグイン 7 + 出力 110)", latency.Format());
    }

    [Theory]
    [InlineData(0.4, "0")]
    [InlineData(0.5, "1")]    // 四捨五入 (0.5 は切り上げ)
    [InlineData(1.49, "1")]
    [InlineData(1.5, "2")]
    public void Parts_are_rounded_to_the_nearest_millisecond(double ms, string expected)
    {
        var text = new LatencyInfo(0, 0, ms, 0).Format();

        Assert.Contains($"プラグイン {expected} +", text);
    }

    [Fact]
    public void No_plugin_latency_is_shown_as_zero()
    {
        var text = new LatencyInfo(15, 10, 0, 100).Format();

        Assert.Equal("遅延 約 125 ms  (入力 15 + 内部バッファ 10 + プラグイン 0 + 出力 100)", text);
    }

    [Fact]
    public void Negative_values_are_shown_as_zero()
    {
        var text = new LatencyInfo(-5, 10, 0, 100).Format();

        Assert.Equal("遅延 約 110 ms  (入力 0 + 内部バッファ 10 + プラグイン 0 + 出力 100)", text);
    }

    [Fact]
    public void Latency_reported_in_samples_converts_to_milliseconds_as_expected()
    {
        // プラグインの申告 (サンプル数) → ms。Pitch Fix の実測値: 44.1kHz で 1764 サンプル = 40ms
        double ms = 1764 * 1000.0 / 44100;

        Assert.Equal("遅延 約 40 ms  (入力 0 + 内部バッファ 0 + プラグイン 40 + 出力 0)", new LatencyInfo(0, 0, ms, 0).Format());
    }
}
