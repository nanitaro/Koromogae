using Koromogae.Audio;
using Xunit;

namespace Audio.Tests;

/// <summary>コールバックが止まっていないかの監視。</summary>
public class CallbackWatchTests
{
    [Fact]
    public void A_stream_whose_count_keeps_growing_is_never_stalled()
    {
        var watch = new CallbackWatch(startMs: 0);

        for (int i = 1; i <= 100; i++)
            Assert.Equal(0, watch.Update(callbacks: i * 10, nowMs: i * 500.0));
    }

    [Fact]
    public void A_stream_that_never_calls_back_is_stalled_after_the_threshold_from_the_start()
    {
        // DirectSound 0ms の入力のように、開始から一度もコールバックが来ない場合
        var watch = new CallbackWatch(startMs: 10_000);

        Assert.Equal(0, watch.Update(0, 10_500));
        Assert.Equal(0, watch.Update(0, 10_000 + CallbackWatch.StallThresholdMs - 1));
        Assert.Equal(CallbackWatch.StallThresholdMs, watch.Update(0, 10_000 + CallbackWatch.StallThresholdMs));
    }

    [Fact]
    public void A_stream_that_stops_after_working_reports_how_long_it_has_been_stopped()
    {
        // WASAPI 排他の 300ms 以上のように、最初の充填のあと止まる場合
        var watch = new CallbackWatch(0);
        Assert.Equal(0, watch.Update(50, 500));
        Assert.Equal(0, watch.Update(50, 1500));   // 回数が増えてから 1000ms: まだ
        Assert.Equal(1500, watch.Update(50, 2000));
        Assert.Equal(3000, watch.Update(50, 3500));
    }

    [Fact]
    public void A_stream_that_resumes_is_no_longer_stalled()
    {
        var watch = new CallbackWatch(0);
        Assert.True(watch.Update(0, 5000) > 0);

        Assert.Equal(0, watch.Update(1, 5500));
        Assert.Equal(0, watch.Update(1, 5500 + CallbackWatch.StallThresholdMs - 1));   // 再開した時刻から数え直す
    }

    [Fact]
    public void The_threshold_is_longer_than_the_largest_selectable_buffer_period()
    {
        // WASAPI 排他の 500ms バッファは、約 511ms ごとにまとまって届く。正常な動作を「止まった」と誤認しない
        Assert.True(CallbackWatch.StallThresholdMs > 511 * 2);
    }
}
