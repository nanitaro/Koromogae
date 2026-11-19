using Koromogae.Audio;
using Xunit;
using Xunit.Abstractions;

namespace Audio.Tests;

/// <summary>入力と出力をつなぐリングバッファ。仮想時間の模擬で、コールバックの届き方ごとの音飛びと捨てた量を確かめる。</summary>
public class StereoRingTests
{
    const int Rate = 44100;
    readonly ITestOutputHelper _out;

    public StereoRingTests(ITestOutputHelper output) => _out = output;

    // ---- 補助: 1 サンプルごとに連番を入れ、順序と欠落を確かめる ----

    static void Write(StereoRing ring, ref long next, int n, bool announce = true)
    {
        var l = new float[n];
        var r = new float[n];
        for (int i = 0; i < n; i++) { l[i] = next + i; r[i] = -(next + i); }
        next += n;
        if (announce) ring.InputBlock = n;
        ring.Write(l, r);
    }

    /// <summary>total フレームを block フレームずつの入力コールバックとして書く (直前のコールバックの大きさが余裕に効くため)。</summary>
    static void Fill(StereoRing ring, ref long next, int total, int block)
    {
        for (int done = 0; done < total; done += block) Write(ring, ref next, Math.Min(block, total - done));
    }

    static (bool Ok, float[] L, float[] R) Read(StereoRing ring, int n)
    {
        var l = new float[n];
        var r = new float[n];
        bool ok = ring.Read(l, r);
        return (ok, l, r);
    }

    // ---- 基本の動作 ----

    [Fact]
    public void Nothing_is_read_until_the_cushion_has_built_up()
    {
        // 余裕 = 出力 n + 直前の入力コールバック (ここでは 256 + 256)
        var ring = new StereoRing(Rate, 0);
        long next = 0;

        Write(ring, ref next, 256);
        Assert.False(Read(ring, 256).Ok);       // 256 しか無い: 足りない
        Write(ring, ref next, 255, announce: false);
        Assert.False(Read(ring, 256).Ok);       // 511: あと 1
        Write(ring, ref next, 1, announce: false);
        Assert.True(Read(ring, 256).Ok);        // 512 になった
        Assert.Equal(0, ring.Underruns);        // 開始時の充填待ちは音飛びに数えない
        Assert.Equal(2, ring.SilentChunks);
    }

    [Fact]
    public void Samples_come_out_in_order_and_unchanged_across_the_wrap_around()
    {
        var ring = new StereoRing(Rate, 0);
        long next = 0, expected = 0;
        Fill(ring, ref next, 512, 256);

        // リングの大きさ (65536) の何倍も流して、折り返しをまたぐ
        for (int round = 0; round < 1000; round++)
        {
            Write(ring, ref next, 256);
            var (ok, l, r) = Read(ring, 256);
            Assert.True(ok);
            for (int i = 0; i < 256; i++)
            {
                Assert.Equal(expected + i, l[i]);
                Assert.Equal(-(expected + i), r[i]);
            }
            expected += 256;
        }
        Assert.Equal(0, ring.Underruns);
        Assert.Equal(0, ring.Trims);
    }

    [Fact]
    public void A_steady_stream_never_underruns_or_trims()
    {
        var ring = new StereoRing(Rate, 0);
        long next = 0;
        Fill(ring, ref next, 882, 441);

        for (int i = 0; i < 5000; i++)
        {
            Write(ring, ref next, 441);
            Assert.True(Read(ring, 441).Ok);
        }

        Assert.Equal(0, ring.Underruns);
        Assert.Equal(0, ring.Trims);
        Assert.Equal(0, ring.TrimmedFrames);
        Assert.True(ring.MinSlackFrames >= 0);
    }

    [Fact]
    public void Running_dry_is_counted_once_and_the_ring_must_refill_before_playing_again()
    {
        var ring = new StereoRing(Rate, 0);
        long next = 0;
        Fill(ring, ref next, 512, 256);
        Assert.True(Read(ring, 256).Ok);        // 256 残る

        Assert.True(Read(ring, 256).Ok);        // 空になるまで読む (avail 256 >= n)
        Assert.False(Read(ring, 256).Ok);       // 足りない: 音飛び
        Assert.Equal(1, ring.Underruns);

        Write(ring, ref next, 256);
        Assert.False(Read(ring, 256).Ok);       // まだ余裕が溜まっていない (256 < 512)
        Assert.Equal(1, ring.Underruns);        // 充填待ちは数え直さない
        Write(ring, ref next, 256);
        Assert.True(Read(ring, 256).Ok);
    }

    // ---- 溜まりすぎ ----

    [Fact]
    public void When_too_much_has_piled_up_the_oldest_audio_is_dropped_and_the_newest_is_kept()
    {
        var ring = new StereoRing(Rate, 0);
        long next = 0;
        Fill(ring, ref next, 512, 256);
        Assert.True(Read(ring, 256).Ok);        // 通常どおり 1 回読む (256 残る)

        // 出力が 1 秒止まった想定: 閾値 (3*(256+512)+882 = 3186) を大きく超える
        for (int i = 0; i < 100; i++) Write(ring, ref next, 441, announce: false);
        ring.InputBlock = 441;
        long total = next;

        var (ok, l, _) = Read(ring, 256);

        Assert.True(ok);
        Assert.Equal(1, ring.Trims);
        Assert.True(ring.TrimmedFrames > 40_000);
        // 読めたのは、古い音ではなく、直近 (余裕 = 256 + 441 フレームぶん) の先頭から
        Assert.Equal(total - (256 + 441), l[0]);
    }

    [Fact]
    public void Trimming_keeps_exactly_the_cushion_so_the_next_read_still_works()
    {
        var ring = new StereoRing(Rate, 0);
        long next = 0;
        Fill(ring, ref next, 512, 256);
        Read(ring, 256);
        for (int i = 0; i < 100; i++) Write(ring, ref next, 441, announce: false);
        ring.InputBlock = 441;

        Assert.True(Read(ring, 256).Ok);
        Assert.True(Read(ring, 256).Ok);        // 捨てた直後に続けて読める
        Assert.Equal(0, ring.Underruns);
    }

    [Fact]
    public void The_extra_makes_the_ring_wait_for_more_before_it_starts()
    {
        long next = 0, next2 = 0;
        var plain = new StereoRing(Rate, 0);
        var padded = new StereoRing(Rate, Rate / 20);   // +50ms = 2205 フレーム
        Fill(plain, ref next, 512, 256);
        Fill(padded, ref next2, 512, 256);

        Assert.True(Read(plain, 256).Ok);
        Assert.False(Read(padded, 256).Ok);

        Fill(padded, ref next2, 2205, 256);             // 512 + 2205 >= 256 + 256 + 2205
        Assert.True(Read(padded, 256).Ok);
    }

    [Fact]
    public void The_extra_also_raises_the_trim_threshold_by_the_same_amount()
    {
        // 上乗せ分を閾値に足さないと、溜めた分をすぐ捨ててしまう。
        // 上乗せなしの閾値 (3*(256+256) + 882 = 2418) を超える 4000 フレームは、上乗せなしでは捨てるが、+100ms (4410) では捨てない
        var plain = new StereoRing(Rate, 0);
        var padded = new StereoRing(Rate, Rate / 10);
        long a = 0, b = 0;
        Fill(plain, ref a, 4000, 256);
        Fill(padded, ref b, 4000, 256);
        plain.InputBlock = 256;
        padded.InputBlock = 256;

        plain.Read(new float[256], new float[256]);
        padded.Read(new float[256], new float[256]);

        Assert.Equal(1, plain.Trims);
        Assert.Equal(0, padded.Trims);
    }

    [Fact]
    public void A_full_ring_drops_new_input_instead_of_overwriting_unread_audio()
    {
        var ring = new StereoRing(Rate, 0);
        long next = 0;
        for (int i = 0; i < StereoRing.Frames / 4096; i++) Write(ring, ref next, 4096);
        Assert.Equal(StereoRing.Frames, ring.WrittenFrames);

        Write(ring, ref next, 4096);            // もう入らない

        Assert.Equal(StereoRing.Frames, ring.WrittenFrames);
    }

    [Fact]
    public void Slack_and_average_fill_are_tracked()
    {
        var ring = new StereoRing(Rate, 0);
        long next = 0;
        Assert.Equal(-1, ring.MinSlackFrames);   // まだ読み出していない
        Fill(ring, ref next, 1024, 256);

        Assert.True(Read(ring, 256).Ok);

        Assert.Equal(1024 - 256, ring.MinSlackFrames);
        Assert.True(ring.AverageFrames > 0);
    }

    // ---- 仮想時間の模擬 ----

    /// <summary>コールバックの届き方。周期 periodMs ごとに、frames フレームの呼び出しが count 回、同時に連続して届く。</summary>
    record Pattern(double PeriodMs, int Count, int Frames, double PhaseMs = 0, double SpreadMs = 0);

    (long Underruns, long Trims, long TrimmedFrames, long SilentChunks) Simulate(
        Pattern input, Pattern output, int extraFrames, double seconds = 20)
    {
        var ring = new StereoRing(Rate, extraFrames);
        var events = new List<(double Time, int Order, bool IsInput, int Frames)>();
        void Add(Pattern p, bool isInput)
        {
            for (double t = p.PhaseMs; t < seconds * 1000; t += p.PeriodMs)
                for (int k = 0; k < p.Count; k++)
                    events.Add((t + k * p.SpreadMs, isInput ? 0 : 1, isInput, p.Frames));
        }
        Add(input, true);
        Add(output, false);
        events.Sort((a, b) => a.Time != b.Time ? a.Time.CompareTo(b.Time) : a.Order.CompareTo(b.Order));

        long next = 0;
        foreach (var e in events)
        {
            if (e.IsInput) Write(ring, ref next, e.Frames);
            else Read(ring, e.Frames);
        }
        return (ring.Underruns, ring.Trims, ring.TrimmedFrames, ring.SilentChunks);
    }

    // WASAPI 共有・MME のように、10ms ごとに 441 フレーム
    static readonly Pattern Smooth = new(10, 1, 441);
    // WASAPI 排他 100ms: 100ms ごとに、441 フレームの呼び出しが 10 回連続して届く
    static Pattern Burst(double phaseMs = 0) => new(100, 10, 441, phaseMs);

    [Fact]
    public void Smooth_callbacks_play_cleanly_with_the_automatic_cushion()
    {
        var r = Simulate(Smooth, Smooth, extraFrames: 0);
        _out.WriteLine($"smooth: {r}");

        Assert.Equal(0, r.Underruns);
        Assert.Equal(0, r.Trims);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(37)]
    [InlineData(63)]
    public void Bursty_callbacks_break_the_automatic_cushion(double outputPhaseMs)
    {
        // 既知の問題: 余裕が「コールバック 1 回ぶん」しか無いので、バーストで音飛びと廃棄が起きる。
        // この模擬が「音飛びがある」ことを確かめるのは、方針を変えたときに、この数字が改善したことを見えるようにするため
        var r = Simulate(Burst(), Burst(outputPhaseMs), extraFrames: 0);
        _out.WriteLine($"burst phase={outputPhaseMs}: {r}");

        Assert.True(r.Underruns + r.Trims > 0);
    }

    [Theory]
    [InlineData(50)]
    [InlineData(100)]
    [InlineData(150)]
    [InlineData(200)]
    public void A_manual_extra_of_at_least_50ms_absorbs_the_100ms_bursts(int extraMs)
    {
        // 手動の「内部バッファ」の上乗せの効果。入出力が同じ周期の理想的なバースト (実機の揺れは含まない) では、
        // 上乗せがあれば音飛びは起きない。捨てるのは、開始直後の 1 回 (3528 フレーム = 80ms) まで
        foreach (double phase in new[] { 0, 25, 50, 75, 99 })
        {
            var r = Simulate(Burst(), Burst(phase), extraMs * Rate / 1000);
            _out.WriteLine($"burst extra={extraMs}ms phase={phase}: {r}");

            Assert.Equal(0, r.Underruns);
            Assert.True(r.Trims <= 1, $"trims={r.Trims}");
        }
    }
}