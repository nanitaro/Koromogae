using Koromogae.Audio;
using Xunit;

namespace Audio.Tests;

/// <summary>ノイズシェイピング付きディザ: ノイズの周波数の偏り・全体の量・無音・暴走しないこと・チャンネルの独立。</summary>
public unsafe class ShapedDitherTests
{
    const int Rate = 44100;
    const uint Seed = 0x2545F491;

    // ---- 補助 ----

    /// <summary>Int16 に丸めた結果 (LSB 単位の整数) を、左右の別々の入力で返す。</summary>
    static (short[] L, short[] R) Quantize(float[] l, float[] r, DitherMode mode, uint seed = Seed)
    {
        var dst = new byte[l.Length * 4];
        var state = Pcm.DitherState.Create(seed);
        fixed (float* pl = l, pr = r)
        fixed (byte* pd = dst)
            Pcm.WriteStereo(pl, pr, SampleFormat.Int16, 2, pd, l.Length, ref state, mode);
        var left = new short[l.Length];
        var right = new short[l.Length];
        for (int i = 0; i < l.Length; i++)
        {
            left[i] = BitConverter.ToInt16(dst, i * 4);
            right[i] = BitConverter.ToInt16(dst, i * 4 + 2);
        }
        return (left, right);
    }

    static float[] Dc(double lsb, int n) => Enumerable.Repeat((float)(lsb / 32768.0), n).ToArray();

    /// <summary>誤差 (出力 - 入力、LSB) の、指定の周波数での電力 (Goertzel 法)。</summary>
    static double PowerAt(double[] e, double freq)
    {
        double w = 2 * Math.PI * freq / Rate, coeff = 2 * Math.Cos(w), s1 = 0, s2 = 0;
        foreach (double x in e) { double s = x + coeff * s1 - s2; s2 = s1; s1 = s; }
        return (s1 * s1 + s2 * s2 - coeff * s1 * s2) / ((double)e.Length * e.Length) * 4;
    }

    static double BandPower(double[] e, double from, double to, int steps = 12)
        => Enumerable.Range(0, steps).Average(i => PowerAt(e, from + (to - from) * i / (steps - 1)));

    static double Db(double ratio) => 10 * Math.Log10(ratio);

    static double[] Errors(DitherMode mode, double lsb = 0.3, int n = 1 << 16, uint seed = Seed)
    {
        var x = Dc(lsb, n);
        var (l, _) = Quantize(x, x, mode, seed);
        return l.Select(v => v - lsb).ToArray();
    }

    // ---- 係数 ----

    [Fact]
    public void The_taps_are_a_minimum_phase_filter_so_the_total_log_gain_is_zero()
    {
        // Gerzon-Craven の定理: 最小位相の FIR は、周波数にわたる ln|NTF| の平均が 0。係数の書き間違い・符号の誤りがあると外れる
        var a = NoiseShaping.Published;
        double sum = 0;
        int n = 4000;
        for (int i = 0; i < n; i++)
        {
            double w = Math.PI * (i + 0.5) / n, re = 1, im = 0;
            for (int k = 0; k < a.Length; k++) { re += a[k] * Math.Cos((k + 1) * w); im -= a[k] * Math.Sin((k + 1) * w); }
            sum += Math.Log(Math.Sqrt(re * re + im * im));
        }

        Assert.Equal(0.0, sum / n, 3);
    }

    [Fact]
    public void The_filter_has_nine_taps_with_the_published_magnitudes()
    {
        Assert.Equal(new[] { 2.412f, 3.370f, 3.937f, 4.174f, 3.353f, 2.205f, 1.281f, 0.569f, 0.0847f }, NoiseShaping.Published.Select(a => (float)Math.Abs(a)));
        Assert.True(NoiseShaping.Published[0] < 0);      // 符号: NTF = 1 - 2.412 z^-1 + 3.370 z^-2 - ...
    }

    // ---- ノイズの周波数の偏り ----

    [Fact]
    public void The_noise_in_the_most_audible_midrange_is_far_below_plain_tpdf()
    {
        var shaped = Errors(DitherMode.Shaped);
        var tpdf = Errors(DitherMode.Tpdf);

        double reduction = Db(BandPower(shaped, 2000, 5000) / BandPower(tpdf, 2000, 5000));

        Assert.True(reduction < -15, $"2〜5kHz のノイズが TPDF より {reduction:F1} dB しか減っていません");
    }

    [Fact]
    public void The_noise_at_low_frequencies_is_also_reduced()
    {
        double reduction = Db(BandPower(Errors(DitherMode.Shaped), 100, 800) / BandPower(Errors(DitherMode.Tpdf), 100, 800));

        Assert.True(reduction < -8, $"低域のノイズが {reduction:F1} dB しか減っていません");
    }

    [Fact]
    public void The_noise_is_moved_to_the_high_frequencies_where_it_is_much_larger()
    {
        double increase = Db(BandPower(Errors(DitherMode.Shaped), 17000, 21000) / BandPower(Errors(DitherMode.Tpdf), 17000, 21000));

        Assert.True(increase > 15, $"高域のノイズが {increase:F1} dB しか増えていません (移っていないはず)");
    }

    [Fact]
    public void The_total_noise_power_matches_the_filter_gain_times_the_white_noise()
    {
        // 誤差 (ディザ込み) の電力 1/4 LSB^2 に、ノイズ伝達関数の電力ゲイン (1 + Σ a^2 ≒ 69) を掛けたもの
        double gain = 1 + NoiseShaping.Published.Sum(a => a * a);
        var e = Errors(DitherMode.Shaped, 0.37);
        double mean = e.Average();
        double variance = e.Average(v => (v - mean) * (v - mean));

        Assert.InRange(variance, gain * 0.25 * 0.9, gain * 0.25 * 1.1);
    }

    // ---- 偏り (バイアス) ・分布 ----

    [Theory]
    [InlineData(0.05)]
    [InlineData(0.3)]
    [InlineData(0.5)]
    public void The_average_output_still_equals_the_input_so_there_is_no_dc_error(double lsb)
    {
        var e = Errors(DitherMode.Shaped, lsb);

        Assert.InRange(e.Average(), -0.05, 0.05);
    }

    [Fact]
    public void A_tone_far_below_one_lsb_is_still_encoded_in_the_average()
    {
        // 0.3 LSB しかない直流は、四捨五入だけだと消える。ノイズシェイピング付きのディザでも、平均として残る
        var plain = Quantize(Dc(0.3, 20000), Dc(0.3, 20000), DitherMode.Off).L;
        var shaped = Quantize(Dc(0.3, 20000), Dc(0.3, 20000), DitherMode.Shaped).L;

        Assert.Equal(0.0, plain.Average(v => (double)v), 6);
        Assert.InRange(shaped.Average(v => (double)v), 0.25, 0.35);
    }

    // ---- 無音・クリップ・安定 ----

    [Fact]
    public void Digital_silence_stays_exactly_zero()
    {
        var silence = new float[20000];

        var (l, r) = Quantize(silence, silence, DitherMode.Shaped);

        Assert.All(l, v => Assert.Equal(0, v));
        Assert.All(r, v => Assert.Equal(0, v));
    }

    [Fact]
    public void Silence_after_signal_is_exactly_zero_at_once_because_the_history_is_dropped()
    {
        // 信号のあとに無音が来たとき、フィードバックの履歴が残っていると、無音のはずの出力にノイズが出る
        var x = Dc(0.4, 5000).Concat(new float[5000]).ToArray();

        var (l, _) = Quantize(x, x, DitherMode.Shaped);

        Assert.True(l.Take(5000).Any(v => v != 0));
        Assert.All(l.Skip(5000), v => Assert.Equal(0, v));
    }

    [Fact]
    public void NaN_and_overload_do_not_blow_up_the_feedback_loop()
    {
        // フルスケールを超える方形波 (クリップし続ける) のあと、通常の小さな信号に戻ったら、ノイズが元の大きさに戻る
        var overload = Enumerable.Range(0, 20000).Select(i => (i / 40) % 2 == 0 ? 4f : -4f).ToArray();
        var quiet = Dc(0.3, 40000);
        var x = overload.Concat(new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity }).Concat(quiet).ToArray();

        var (l, _) = Quantize(x, x, DitherMode.Shaped);

        Assert.All(l.Take(20000), v => Assert.InRange((int)v, short.MinValue, short.MaxValue));
        var tail = l.Skip(20003 + 2000).Select(v => v - 0.3).ToArray();       // 戻ってからしばらく後
        double rms = Math.Sqrt(tail.Average(v => v * v));
        double expected = Math.Sqrt((1 + NoiseShaping.Published.Sum(a => a * a)) * 0.25);
        Assert.InRange(rms, expected * 0.85, expected * 1.15);
    }

    [Fact]
    public void Full_scale_input_is_clipped_not_wrapped_and_stays_stable()
    {
        var loud = Enumerable.Repeat(1.0f, 30000).ToArray();

        var (l, _) = Quantize(loud, loud, DitherMode.Shaped);

        Assert.All(l, v => Assert.True(v > 32000, $"フルスケールの入力が {v} に反転しています"));
    }

    [Fact]
    public void A_full_scale_nyquist_square_wave_stays_on_the_rails_with_the_right_sign()
    {
        // クリップし続ける最悪の入力。ノイズ整形の誤差が大きくなりすぎると、出力の符号が入力と食い違う (暴走)
        var x = Enumerable.Range(0, 40000).Select(i => i % 2 == 0 ? 1f : -1f).ToArray();

        var (l, _) = Quantize(x, x, DitherMode.Shaped);

        for (int i = 0; i < l.Length; i++)
            Assert.True(i % 2 == 0 ? l[i] > 30000 : l[i] < -30000, $"{i} 番目: 入力 {(i % 2 == 0 ? "+" : "-")}1.0 に対して出力 {l[i]}");
    }

    [Fact]
    public void The_error_history_is_cleared_by_digital_silence()
    {
        // 履歴が残っていると、次に信号が来たとき、古い誤差が混ざる
        var x = Dc(0.4, 3000).Concat(new float[10]).ToArray();
        var xr = new float[x.Length];
        var dst = new byte[x.Length * 4];
        var state = Pcm.DitherState.Create(Seed);
        fixed (float* pl = x, pr = xr)
        fixed (byte* pd = dst)
            Pcm.WriteStereo(pl, pr, SampleFormat.Int16, 2, pd, x.Length, ref state, DitherMode.Shaped);

        Assert.All(state.Hist0, h => Assert.Equal(0f, h));
    }

    // ---- 再現性・チャンネル ----

    [Fact]
    public void The_same_seed_gives_the_same_output_and_another_seed_gives_another()
    {
        var x = Dc(0.3, 5000);

        var a = Quantize(x, x, DitherMode.Shaped, 12345).L;
        var b = Quantize(x, x, DitherMode.Shaped, 12345).L;
        var c = Quantize(x, x, DitherMode.Shaped, 54321).L;

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }

    [Fact]
    public void Each_channel_has_its_own_error_history()
    {
        // 左だけに信号があるとき、右 (無音) は無音のまま。左右でフィードバックを共有していると、右にノイズが漏れる
        var left = Dc(0.3, 20000);
        var right = new float[20000];

        var (l, r) = Quantize(left, right, DitherMode.Shaped);

        Assert.True(l.Any(v => v != 0));
        Assert.All(r, v => Assert.Equal(0, v));
    }

    [Fact]
    public void A_channels_noise_does_not_depend_on_the_other_channels_signal()
    {
        // 左の誤差の系列が、右の信号の有無で変わる (履歴が混ざる) ことはない。乱数は共有しているので、右が無音のときと右に信号があるときで
        // 左の「フィードバックの形」(誤差の自己相関) が同じことで確かめる
        var n = 1 << 15;
        var left = Dc(0.3, n);
        double Lag1(double[] e) { double m = e.Average(); double c = 0, v = 0; for (int i = 1; i < e.Length; i++) { c += (e[i] - m) * (e[i - 1] - m); v += (e[i] - m) * (e[i] - m); } return c / v; }

        var alone = Quantize(left, new float[n], DitherMode.Shaped).L.Select(v => v - 0.3).ToArray();
        var both = Quantize(left, Dc(0.45, n), DitherMode.Shaped).L.Select(v => v - 0.3).ToArray();

        Assert.InRange(Math.Abs(Lag1(alone) - Lag1(both)), 0, 0.05);
        Assert.True(Lag1(alone) < -0.3, "整形されたノイズは、隣り合うサンプルが負に相関するはず (低域が少ない)");
    }

    // ---- 他の形式では働かない ----

    [Theory]
    [InlineData(SampleFormat.Float32)]
    [InlineData(SampleFormat.Int32)]
    public void Float32_and_Int32_are_unchanged_by_the_shaped_mode(SampleFormat f)
    {
        var values = Enumerable.Range(0, 3000).Select(i => (float)Math.Sin(i * 0.05) * 0.0001f).ToArray();

        byte[] Render(DitherMode mode)
        {
            var dst = new byte[values.Length * 2 * Pcm.BytesPerSample(f)];
            var state = Pcm.DitherState.Create(Seed);
            fixed (float* p = values)
            fixed (byte* pd = dst)
                Pcm.WriteStereo(p, p, f, 2, pd, values.Length, ref state, mode);
            return dst;
        }

        Assert.Equal(Render(DitherMode.Off), Render(DitherMode.Shaped));
    }

    [Fact]
    public void Int24_output_is_shaped_too_and_the_mid_band_noise_is_far_below_the_white_dither()
    {
        // 24bit でも同じ仕組みが働く (1 LSB が小さいので、可聴域の差は小さいが、ノイズの形は同じ)
        const int n = 1 << 16;
        double lsb = 0.3;
        var x = Enumerable.Repeat((float)(lsb / 8388608.0), n).ToArray();
        double[] Err(DitherMode mode)
        {
            var dst = new byte[n * 6];
            var state = Pcm.DitherState.Create(Seed);
            fixed (float* p = x)
            fixed (byte* pd = dst)
                Pcm.WriteStereo(p, p, SampleFormat.Int24, 2, pd, n, ref state, mode);
            return Enumerable.Range(0, n).Select(i => (double)(((dst[i * 6] | dst[i * 6 + 1] << 8 | dst[i * 6 + 2] << 16) << 8) >> 8) - lsb).ToArray();
        }

        double reduction = Db(BandPower(Err(DitherMode.Shaped), 2000, 5000) / BandPower(Err(DitherMode.Tpdf), 2000, 5000));

        Assert.True(reduction < -15);
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(0.9)]
    public void Int24_shaping_still_works_for_loud_signals_because_the_loop_is_computed_in_double(double amp)
    {
        // float で計算すると、24bit の大きな信号 (±数百万 LSB) では 1 LSB 未満の端数が消え、整形がほとんど効かなくなる
        // (以前は 0.5 で -8dB、0.9 で -5dB。double にして -22dB 前後)。誤差は「出力 - 入力 × 2^23」で測り、信号そのものは含めない
        const int n = 1 << 16;
        var x = Enumerable.Range(0, n).Select(i => (float)(amp * Math.Sin(2 * Math.PI * 997 * i / Rate))).ToArray();
        double[] Err(DitherMode mode)
        {
            var dst = new byte[n * 6];
            var state = Pcm.DitherState.Create(Seed);
            fixed (float* p = x)
            fixed (byte* pd = dst)
                Pcm.WriteStereo(p, p, SampleFormat.Int24, 2, pd, n, ref state, mode);
            return Enumerable.Range(0, n).Select(i => (double)(((dst[i * 6] | dst[i * 6 + 1] << 8 | dst[i * 6 + 2] << 16) << 8) >> 8) - (double)x[i] * 8388608.0).ToArray();
        }

        double reduction = Db(BandPower(Err(DitherMode.Shaped), 2000, 5000) / BandPower(Err(DitherMode.Tpdf), 2000, 5000));

        Assert.True(reduction < -15, $"振幅 {amp} の 24bit で、2〜5kHz のノイズが {reduction:F1} dB しか減っていません");
    }

    [Theory]
    [InlineData(DitherMode.Shaped, "ディザ ノイズシェイピング")]
    [InlineData(DitherMode.Tpdf, "ディザ TPDF")]
    public void The_label_names_the_shaped_mode_for_integer_outputs_only(DitherMode mode, string expected)
    {
        Assert.Equal(expected, Pcm.DitherLabel(SampleFormat.Int16, mode));
        Assert.Equal(expected, Pcm.DitherLabel(SampleFormat.Int24, mode));
        Assert.Equal("ディザ オフ", Pcm.DitherLabel(SampleFormat.Float32, mode));
        Assert.Equal("ディザ オフ", Pcm.DitherLabel(SampleFormat.Int32, mode));
    }
}