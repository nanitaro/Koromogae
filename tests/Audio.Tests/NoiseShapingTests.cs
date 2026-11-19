using Koromogae.Audio;
using Xunit;
using Xunit.Abstractions;

namespace Audio.Tests;

/// <summary>
/// ノイズシェイピングの、サンプルレートごとの係数と、聴感の重みによる評価。
/// 評価は 3 つの重み (A 特性・ITU-R 468・絶対聴取閾値) で、ノイズシェイピングなしの TPDF を 0 dB とした聞こえるノイズの量 (負なら良い)。
/// 数値の表は、テストの出力 (--logger "console;verbosity=detailed") に出る。
/// </summary>
public unsafe class NoiseShapingTests
{
    readonly ITestOutputHelper _out;
    public NoiseShapingTests(ITestOutputHelper output) => _out = output;

    static readonly int[] HighRates = { 88200, 96000, 176400, 192000 };
    static readonly int[] LowRates = { 44100, 48000 };

    static (double A, double Itu, double Ath, double Total, double Peak) Evaluate(double[] taps, int rate) => (
        NoiseShaping.WeightedGainDb(taps, rate, PerceptualWeights.A),
        NoiseShaping.WeightedGainDb(taps, rate, PerceptualWeights.Itu468),
        NoiseShaping.WeightedGainDb(taps, rate, PerceptualWeights.Threshold),
        NoiseShaping.TotalGainDb(taps),
        NoiseShaping.PeakGainDb(taps, rate));

    static (double A, double Itu, double Ath) EvaluateHp(int rate) => (
        NoiseShaping.WeightedGainDb(f => NoiseShaping.HpTpdfPower(f, rate), rate, PerceptualWeights.A),
        NoiseShaping.WeightedGainDb(f => NoiseShaping.HpTpdfPower(f, rate), rate, PerceptualWeights.Itu468),
        NoiseShaping.WeightedGainDb(f => NoiseShaping.HpTpdfPower(f, rate), rate, PerceptualWeights.Threshold));

    // ---- 聴感の重みそのもの ----

    [Fact]
    public void The_a_weighting_matches_the_standard_values()
    {
        // IEC 61672: 1 kHz で 0 dB、100 Hz で -19.1 dB、10 kHz で -2.5 dB、20 kHz で -9.3 dB
        double Db(double f) => 10 * Math.Log10(PerceptualWeights.A(f));
        Assert.Equal(0.0, Db(1000), 3);
        Assert.Equal(-19.1, Db(100), 1);
        Assert.Equal(-2.5, Db(10000), 1);
        Assert.Equal(-9.3, Db(20000), 1);
        Assert.Equal(1.3, Db(2500), 1);      // 2〜3 kHz で最大 (約 +1.3 dB)
    }

    [Fact]
    public void The_itu_r_468_weighting_matches_the_standard_values()
    {
        // ITU-R BS.468: 1 kHz で 0 dB、6.3 kHz で +12.2 dB (最大)、100 Hz で約 -19.9 dB、16 kHz で約 -11.7 dB
        double Db(double f) => 10 * Math.Log10(PerceptualWeights.Itu468(f));
        Assert.Equal(0.0, Db(1000), 1);
        Assert.Equal(12.2, Db(6300), 1);
        Assert.Equal(-19.9, Db(100), 1);
        Assert.Equal(-11.7, Db(16000), 1);
        Assert.True(Enumerable.Range(0, 400).All(i => Db(20 + i * 50.0) <= 12.3));
    }

    [Fact]
    public void The_hearing_threshold_is_lowest_around_3_to_4_khz_and_rises_steeply_at_high_frequencies()
    {
        Assert.InRange(PerceptualWeights.ThresholdDb(3500), -6, -4);
        Assert.InRange(PerceptualWeights.ThresholdDb(1000), 2, 5);
        Assert.InRange(PerceptualWeights.ThresholdDb(16000), 60, 70);
        Assert.True(PerceptualWeights.ThresholdDb(20000) > 150);
        Assert.True(PerceptualWeights.Threshold(3500) > PerceptualWeights.Threshold(16000) * 1e5);     // 3.5kHz のノイズは 16kHz よりずっと耳に付く
    }

    // ---- 係数の選び方 ----

    [Theory]
    [InlineData(44100)]
    [InlineData(48000)]
    public void Up_to_48khz_the_published_f_weighted_filter_is_used(int rate)
    {
        Assert.Same(NoiseShaping.Published, NoiseShaping.TapsFor(rate));
        Assert.Equal(9, NoiseShaping.TapsFor(rate).Length);
    }

    [Theory]
    [InlineData(88200)]
    [InlineData(96000)]
    [InlineData(176400)]
    [InlineData(192000)]
    public void From_88khz_up_a_12th_order_filter_is_designed_for_the_rate(int rate)
    {
        var taps = NoiseShaping.TapsFor(rate);

        Assert.Equal(12, taps.Length);
        Assert.NotEqual(NoiseShaping.Published.Length, taps.Length);
        Assert.Same(taps, NoiseShaping.TapsFor(rate));         // 同じレートでは、同じ配列 (毎回設計し直さない)
    }

    [Fact]
    public void Each_rate_gets_its_own_coefficients()
    {
        var a = NoiseShaping.TapsFor(88200);
        var b = NoiseShaping.TapsFor(96000);
        var c = NoiseShaping.TapsFor(192000);

        Assert.NotEqual(a, b);
        Assert.NotEqual(b, c);
    }

    [Fact]
    public void The_design_is_deterministic()
    {
        var w = NoiseShaping.DesignWeight();

        Assert.Equal(NoiseShaping.Design(96000, 12, w), NoiseShaping.Design(96000, 12, w));
    }

    [Theory]
    [InlineData(44100)] [InlineData(48000)] [InlineData(88200)] [InlineData(96000)] [InlineData(176400)] [InlineData(192000)]
    public void Every_filter_is_minimum_phase_so_the_feedback_loop_is_stable(int rate)
    {
        // Gerzon-Craven の定理: 最小位相の FIR は、周波数にわたる ln|NTF| の平均が 0。誤りがあると外れ、ループが不安定になりうる
        Assert.Equal(0.0, NoiseShaping.MeanLogGain(NoiseShaping.TapsFor(rate), rate), 3);
    }

    [Theory]
    [InlineData(88200)] [InlineData(96000)] [InlineData(176400)] [InlineData(192000)]
    public void The_designed_filter_keeps_the_audible_band_clean_and_pushes_the_noise_above_20khz(int rate)
    {
        var taps = NoiseShaping.TapsFor(rate);
        double Db(double f) => 10 * Math.Log10(NoiseShaping.NtfPower(taps, rate, f));

        Assert.True(Db(1000) < -10, $"{rate} Hz: 1kHz のノイズが {Db(1000):F1} dB");
        Assert.True(Db(3500) < -15, $"{rate} Hz: 3.5kHz のノイズが {Db(3500):F1} dB");
        Assert.True(Db(rate / 2.0 * 0.9) > 5, $"{rate} Hz: ナイキスト付近のノイズが {Db(rate / 2.0 * 0.9):F1} dB しか増えていない (逃がし先がない)");
        Assert.True(NoiseShaping.UltrasonicPercent(taps, rate) > 85, "ノイズ電力の大半は 20kHz より上にあるはず");
    }

    // ---- 聴感に合わせた評価 (根拠) ----

    [Fact]
    public void Evaluation_table_for_every_rate()
    {
        _out.WriteLine("聞こえるノイズの量 (TPDF = 0 dB、負が良い) / 全体のノイズ電力 / 最大ゲイン / 20kHz より上の割合");
        _out.WriteLine($"{"rate",8} {"filter",-10} {"A",7} {"ITU-468",8} {"閾値",7} {"全体",7} {"最大",7} {"超音波%",8}");
        foreach (int rate in new[] { 44100, 48000, 88200, 96000, 176400, 192000 })
        {
            var hp = EvaluateHp(rate);
            _out.WriteLine($"{rate,8} {"HP-TPDF",-10} {hp.A,7:F1} {hp.Itu,8:F1} {hp.Ath,7:F1} {0.0,7:F1} {10 * Math.Log10(5.0 / 3),7:F1} {"-",8}");
            foreach (var (name, taps) in new[] { ("F特性9次", NoiseShaping.Published), ("採用", NoiseShaping.TapsFor(rate)) })
            {
                var e = Evaluate(taps, rate);
                _out.WriteLine($"{rate,8} {name,-10} {e.A,7:F1} {e.Itu,8:F1} {e.Ath,7:F1} {e.Total,7:F1} {e.Peak,7:F1} {NoiseShaping.UltrasonicPercent(taps, rate),8:F1}");
            }
        }
        Assert.True(true);
    }

    [Theory]
    [InlineData(88200)] [InlineData(96000)] [InlineData(176400)] [InlineData(192000)]
    public void From_88khz_up_the_designed_filter_beats_the_fixed_f_weighted_one_under_every_weighting(int rate)
    {
        var fixedF = Evaluate(NoiseShaping.Published, rate);
        var designed = Evaluate(NoiseShaping.TapsFor(rate), rate);

        // どの重みで測っても 8dB 以上小さい (実測: A 13〜21dB、ITU-468 10〜25dB、閾値 13〜28dB)
        Assert.True(designed.A < fixedF.A - 8, $"A: 固定 {fixedF.A:F1} → 設計 {designed.A:F1}");
        Assert.True(designed.Itu < fixedF.Itu - 8, $"ITU-468: 固定 {fixedF.Itu:F1} → 設計 {designed.Itu:F1}");
        Assert.True(designed.Ath < fixedF.Ath - 8, $"閾値: 固定 {fixedF.Ath:F1} → 設計 {designed.Ath:F1}");
        // 全体のノイズも、固定と同じかそれ以下 (3dB 以内の余裕)
        Assert.True(designed.Total < fixedF.Total + 3, $"全体: 固定 {fixedF.Total:F1} → 設計 {designed.Total:F1}");
        Assert.True(designed.Peak < fixedF.Peak + 1, $"最大: 固定 {fixedF.Peak:F1} → 設計 {designed.Peak:F1}");
    }

    [Theory]
    [InlineData(88200)] [InlineData(96000)] [InlineData(176400)] [InlineData(192000)]
    public void From_88khz_up_the_audible_noise_is_far_below_plain_tpdf(int rate)
    {
        var e = Evaluate(NoiseShaping.TapsFor(rate), rate);

        Assert.True(e.A < -20 && e.Itu < -25 && e.Ath < -25, $"A {e.A:F1} / ITU-468 {e.Itu:F1} / 閾値 {e.Ath:F1}");
    }

    // ---- HP-TPDF との比較 ----

    [Fact]
    public void The_hp_tpdf_noise_model_has_the_same_total_power_as_tpdf_and_a_fixed_gentle_tilt()
    {
        // HP-TPDF = 一様乱数 2 つの「差」。丸め誤差 (白色) と合わせた電力密度は (3 - 2cos ω)/3 (TPDF = 1)。全体の電力は TPDF と同じ
        const int rate = 48000;
        double mean = Enumerable.Range(0, 8000).Average(i => NoiseShaping.HpTpdfPower((i + 0.5) / 8000 * rate / 2, rate));

        Assert.Equal(1.0, mean, 3);
        Assert.Equal(1.0 / 3, NoiseShaping.HpTpdfPower(0, rate), 6);                  // 直流: -4.8 dB
        Assert.Equal(5.0 / 3, NoiseShaping.HpTpdfPower(rate / 2.0, rate), 6);         // ナイキスト: +2.2 dB
    }

    [Theory]
    [InlineData(44100)] [InlineData(96000)]
    public void The_measured_hp_tpdf_noise_follows_the_model(int rate)
    {
        // 実際に HP-TPDF で量子化した誤差のスペクトルが、モデルと合う (低域 -4.8dB 付近、ナイキスト付近 +2dB)
        var hp = QuantizeError(DitherMode.HpTpdf, rate);
        var tpdf = QuantizeError(DitherMode.Tpdf, rate);

        double lowDb = 10 * Math.Log10(BandFine(hp, 100, 800, rate) / BandFine(tpdf, 100, 800, rate));
        double highDb = 10 * Math.Log10(BandFine(hp, rate / 2.0 * 0.9, rate / 2.0 * 0.98, rate) / BandFine(tpdf, rate / 2.0 * 0.9, rate / 2.0 * 0.98, rate));
        double expectedLow = 10 * Math.Log10(NoiseShaping.HpTpdfPower(450, rate));
        double expectedHigh = 10 * Math.Log10(NoiseShaping.HpTpdfPower(rate / 2.0 * 0.94, rate));

        Assert.InRange(lowDb, expectedLow - 1.5, expectedLow + 1.5);
        Assert.InRange(highDb, expectedHigh - 1.5, expectedHigh + 1.5);
    }

    [Theory]
    [InlineData(44100)] [InlineData(48000)] [InlineData(88200)] [InlineData(96000)] [InlineData(176400)] [InlineData(192000)]
    public void Hp_tpdf_is_a_small_free_gain_over_tpdf_under_every_weighting(int rate)
    {
        // 全体のノイズを増やさずに、聞こえるノイズを 1.5〜5dB 減らす (レートが高いほど、音の帯域が低域に寄るぶん、わずかに効く)
        var hp = EvaluateHp(rate);

        Assert.InRange(hp.A, -5.5, -1.5);
        Assert.InRange(hp.Itu, -5.5, -1.5);
        Assert.InRange(hp.Ath, -5.5, -3);
    }

    [Theory]
    [InlineData(88200)] [InlineData(96000)] [InlineData(176400)] [InlineData(192000)]
    public void From_88khz_up_the_designed_shaping_beats_hp_tpdf_by_far_under_every_weighting(int rate)
    {
        var hp = EvaluateHp(rate);
        var shaped = Evaluate(NoiseShaping.TapsFor(rate), rate);

        // 実測: A 20〜32dB、ITU-468 24〜36dB、閾値 25〜37dB 良い
        Assert.True(shaped.A < hp.A - 15, $"A: HP-TPDF {hp.A:F1} / 設計 {shaped.A:F1}");
        Assert.True(shaped.Itu < hp.Itu - 15, $"ITU-468: HP-TPDF {hp.Itu:F1} / 設計 {shaped.Itu:F1}");
        Assert.True(shaped.Ath < hp.Ath - 15, $"閾値: HP-TPDF {hp.Ath:F1} / 設計 {shaped.Ath:F1}");
    }

    [Theory]
    [InlineData(44100)]
    [InlineData(48000)]
    public void Up_to_48khz_the_f_weighted_shaping_and_hp_tpdf_each_win_somewhere(int rate)
    {
        // 48kHz 以下では、どちらが良いかは重みによる (トレードオフ)。
        //   F 特性: 閾値 (ごく小さいノイズ) で 8dB 以上、ITU-468 で 2dB 以上 HP-TPDF に勝つ。ただし、全体のノイズが +18dB、A 特性では TPDF より悪い
        //   HP-TPDF: A 特性で F 特性に 10dB 以上勝つ。全体のノイズは増えない
        var hp = EvaluateHp(rate);
        var f = Evaluate(NoiseShaping.TapsFor(rate), rate);

        Assert.True(f.Ath < hp.Ath - 8, $"閾値: HP-TPDF {hp.Ath:F1} / F 特性 {f.Ath:F1}");
        Assert.True(f.Itu < hp.Itu - 2, $"ITU-468: HP-TPDF {hp.Itu:F1} / F 特性 {f.Itu:F1}");
        Assert.True(hp.A < f.A - 3, $"A: HP-TPDF {hp.A:F1} / F 特性 {f.A:F1}");
        Assert.True(f.Total > 15, "F 特性は全体のノイズが +18dB (HP-TPDF は 0dB)");
    }

    [Fact]
    public void The_higher_the_rate_the_less_total_noise_is_needed_for_the_same_audible_benefit()
    {
        // 20kHz より上の空きが広いほど、少ない増加でノイズを逃がせる (Gerzon-Craven の定理の帰結)
        double Total(int rate) => NoiseShaping.TotalGainDb(NoiseShaping.TapsFor(rate));

        Assert.True(Total(192000) < Total(176400) + 1);
        Assert.True(Total(176400) < Total(96000) - 5, "176.4kHz は 96kHz より 5dB 以上、全体のノイズが少ないはず");
        Assert.True(Total(96000) <= Total(88200) + 1);
    }

    [Theory]
    [InlineData(44100)]
    [InlineData(48000)]
    public void Up_to_48khz_shaping_trades_a_weighted_gain_for_a_loss_the_known_limit(int rate)
    {
        // 20kHz〜ナイキストが狭いので、ノイズの逃がし先が足りない。聞こえにくい閾値・ITU-468 では TPDF より良くなるが、
        // A 特性では TPDF より悪い (A 特性は高域を軽く見る)。設計し直しても、3 つ全部を TPDF より大きく良くすることはできない。
        // このため、実績のある発表済みの F 特性をそのまま使う。この数字が変わったら、選び方 (NoiseShaping.PublishedMaxRate) を見直す
        var e = Evaluate(NoiseShaping.TapsFor(rate), rate);

        Assert.True(e.Ath < -10, $"閾値 {e.Ath:F1}");
        Assert.True(e.Itu < -3, $"ITU-468 {e.Itu:F1}");
        Assert.True(e.A > 0, $"A 特性は TPDF より悪い (その分、閾値で大きく勝つ): {e.A:F1}");
    }

    [Theory]
    [InlineData(44100)]
    [InlineData(48000)]
    public void Redesigning_for_48khz_or_less_cannot_improve_all_three_weightings_at_once(int rate)
    {
        // 設計し直した 9 次 (3 つの重みの平均) は、A と ITU-468 では良くなるが、閾値では発表済みの F 特性に負ける
        var fixedF = Evaluate(NoiseShaping.Published, rate);
        var redesigned = Evaluate(NoiseShaping.Design(rate, 9, NoiseShaping.DesignWeight()), rate);

        Assert.True(redesigned.A < fixedF.A);
        Assert.True(redesigned.Ath > fixedF.Ath, $"閾値: 発表済み {fixedF.Ath:F1} / 設計し直し {redesigned.Ath:F1}");
    }

    // ---- 実際に量子化して、スペクトルで確かめる ----

    static double PowerAt(double[] e, double freq, int rate)
    {
        double w = 2 * Math.PI * freq / rate, coeff = 2 * Math.Cos(w), s1 = 0, s2 = 0;
        foreach (double x in e) { double s = x + coeff * s1 - s2; s2 = s1; s1 = s; }
        return (s1 * s1 + s2 * s2 - coeff * s1 * s2) / ((double)e.Length * e.Length) * 4;
    }

    static double Band(double[] e, double a, double b, int rate) => Enumerable.Range(0, 12).Average(i => PowerAt(e, a + (b - a) * i / 11, rate));

    /// <summary>
    /// 細かい帯域の平均。1 つの周波数の電力の推定は、約 ±5.6 dB ばらつく (カイ二乗分布)。TPDF との比のように小さな差を測るときは、
    /// 多くの周波数 (200 点) で平均して、ばらつきを 約 ±0.5 dB (比で ±0.7 dB) に抑える。
    /// </summary>
    static double BandFine(double[] e, double a, double b, int rate) => Enumerable.Range(0, 200).Average(i => PowerAt(e, a + (b - a) * i / 199, rate));

    static double[] QuantizeError(DitherMode mode, int rate, double lsb = 0.3, int n = 1 << 16)
    {
        var x = Enumerable.Repeat((float)(lsb / 32768.0), n).ToArray();
        var dst = new byte[n * 4];
        var state = Pcm.DitherState.Create(0x2545F491, rate);
        fixed (float* p = x)
        fixed (byte* d = dst)
            Pcm.WriteStereo(p, p, SampleFormat.Int16, 2, d, n, ref state, mode);
        return Enumerable.Range(0, n).Select(i => (double)BitConverter.ToInt16(dst, i * 4) - lsb).ToArray();
    }

    [Theory]
    [InlineData(88200)] [InlineData(96000)] [InlineData(176400)] [InlineData(192000)]
    public void Measured_noise_in_the_audible_band_falls_and_the_ultrasonic_noise_rises(int rate)
    {
        var shaped = QuantizeError(DitherMode.Shaped, rate);
        var tpdf = QuantizeError(DitherMode.Tpdf, rate);

        double midDb = 10 * Math.Log10(Band(shaped, 2000, 5000, rate) / Band(tpdf, 2000, 5000, rate));
        double lowDb = 10 * Math.Log10(Band(shaped, 100, 800, rate) / Band(tpdf, 100, 800, rate));
        double highDb = 10 * Math.Log10(Band(shaped, 22000, rate / 2.0 * 0.95, rate) / Band(tpdf, 22000, rate / 2.0 * 0.95, rate));

        Assert.True(midDb < -20, $"2〜5kHz: {midDb:F1} dB");
        Assert.True(lowDb < -10, $"100〜800Hz: {lowDb:F1} dB");
        Assert.True(highDb > 5, $"20kHz より上: {highDb:F1} dB (増えているはず)");
    }

    [Theory]
    [InlineData(88200)] [InlineData(192000)]
    public void The_total_measured_noise_matches_the_filters_power_gain(int rate)
    {
        var taps = NoiseShaping.TapsFor(rate);
        var e = QuantizeError(DitherMode.Shaped, rate, 0.37);
        double mean = e.Average();
        double variance = e.Average(v => (v - mean) * (v - mean));
        double expected = 0.25 * (1 + taps.Sum(a => a * a));

        Assert.InRange(variance, expected * 0.9, expected * 1.1);
    }

    [Theory]
    [InlineData(96000)] [InlineData(192000)]
    public void A_full_scale_nyquist_square_wave_and_silence_behave_at_high_rates_too(int rate)
    {
        var x = Enumerable.Range(0, 30000).Select(i => i % 2 == 0 ? 1f : -1f).Concat(new float[2000]).ToArray();
        var dst = new byte[x.Length * 4];
        var state = Pcm.DitherState.Create(0x2545F491, rate);
        fixed (float* p = x)
        fixed (byte* d = dst)
            Pcm.WriteStereo(p, p, SampleFormat.Int16, 2, d, x.Length, ref state, DitherMode.Shaped);
        var l = Enumerable.Range(0, x.Length).Select(i => BitConverter.ToInt16(dst, i * 4)).ToArray();

        for (int i = 0; i < 30000; i++) Assert.True(i % 2 == 0 ? l[i] > 30000 : l[i] < -30000, $"{i} 番目: {l[i]}");
        Assert.All(l.Skip(30000), v => Assert.Equal(0, v));
    }

    [Fact]
    public void The_state_carries_the_taps_of_its_sample_rate()
    {
        Assert.Same(NoiseShaping.TapsFor(44100), Pcm.DitherState.Create(1).Taps);                 // 既定は 44.1kHz
        Assert.Same(NoiseShaping.TapsFor(96000), Pcm.DitherState.Create(1, 96000).Taps);
        Assert.Equal(12, Pcm.DitherState.Create(1, 96000).Hist0.Length);
        Assert.Equal(9, Pcm.DitherState.Create(1, 48000).Hist1.Length);
    }

    [Fact]
    public void The_same_audio_gets_different_noise_at_different_rates()
    {
        // 係数がレートで切り替わっていること: 同じ入力・同じ種でも、44.1k と 96k では結果が違う
        var x = Enumerable.Repeat(0.3f / 32768f, 4000).ToArray();
        byte[] Render(int rate)
        {
            var dst = new byte[x.Length * 4];
            var state = Pcm.DitherState.Create(0x2545F491, rate);
            fixed (float* p = x)
            fixed (byte* d = dst)
                Pcm.WriteStereo(p, p, SampleFormat.Int16, 2, d, x.Length, ref state, DitherMode.Shaped);
            return dst;
        }

        Assert.NotEqual(Render(44100), Render(96000));
        Assert.Equal(Render(44100), Render(48000).Take(0).Concat(Render(44100)).ToArray());      // 同じレートなら再現する
    }
}