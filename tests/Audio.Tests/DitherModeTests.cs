using Koromogae.Audio;
using Xunit;

namespace Audio.Tests;

/// <summary>ディザ方式 (TPDF / HP-TPDF / オフ) の性質と、画面に出すディザの状態の文言のテスト。</summary>
public unsafe class DitherModeTests
{
    const uint Seed = 0x2545F491;
    const int N = 200_000;

    /// <summary>ステレオ 16bit で書き出し、左右それぞれの値を返す。</summary>
    static (short[] L, short[] R) WriteInt16Stereo(float[] l, float[] r, DitherMode mode, uint seed = Seed)
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

    static float[] Constant(double lsb, int n = N) => Enumerable.Repeat((float)(lsb / 32768.0), n).ToArray();

    /// <summary>出力と本来の値 (LSB) の誤差の、平均を引いた電力と、隣り合うサンプル同士の相関 (ラグ 1)。</summary>
    static (double Power, double Lag1) ErrorStats(short[] output, double lsb)
    {
        double[] e = output.Select(v => v - lsb).ToArray();
        double mean = e.Average();
        for (int i = 0; i < e.Length; i++) e[i] -= mean;
        double power = e.Average(x => x * x);
        double cross = 0;
        for (int i = 1; i < e.Length; i++) cross += e[i] * e[i - 1];
        return (power, cross / (e.Length - 1) / power);
    }

    // ---- HP-TPDF の性質 ----

    [Theory]
    [InlineData(0.05)]
    [InlineData(0.3)]
    [InlineData(0.5)]
    public void HpTpdf_has_the_same_error_power_as_tpdf(double lsb)
    {
        // 分布は TPDF と同じ (1/12 + 1/6 = 0.25 LSB^2)。違うのは周波数の偏りだけ
        var values = Constant(lsb);

        var tpdf = ErrorStats(WriteInt16Stereo(values, values, DitherMode.Tpdf).L, lsb);
        var hp = ErrorStats(WriteInt16Stereo(values, values, DitherMode.HpTpdf).L, lsb);

        Assert.InRange(tpdf.Power, 0.24, 0.26);
        Assert.InRange(hp.Power, 0.24, 0.26);
    }

    [Fact]
    public void HpTpdf_noise_leans_to_high_frequencies_while_tpdf_is_white()
    {
        // 隣り合うサンプルの相関: 白色 (TPDF) は 0、前回との差 (HP-TPDF) は負 (= 高域寄り)
        var values = Constant(0.3);

        var tpdf = ErrorStats(WriteInt16Stereo(values, values, DitherMode.Tpdf).L, 0.3);
        var hp = ErrorStats(WriteInt16Stereo(values, values, DitherMode.HpTpdf).L, 0.3);

        Assert.InRange(tpdf.Lag1, -0.05, 0.05);
        Assert.InRange(hp.Lag1, -0.5, -0.2);
    }

    [Fact]
    public void HpTpdf_keeps_the_previous_random_value_per_channel()
    {
        // 左右を交互に書くので、前回の値を共通にすると各チャンネルの差が別の系列同士になり、高域寄りの性質が消える
        var values = Constant(0.3);

        var (left, right) = WriteInt16Stereo(values, values, DitherMode.HpTpdf);

        Assert.InRange(ErrorStats(left, 0.3).Lag1, -0.5, -0.2);
        Assert.InRange(ErrorStats(right, 0.3).Lag1, -0.5, -0.2);
    }

    [Fact]
    public void HpTpdf_still_removes_the_bias_of_plain_rounding()
    {
        // 0.3 LSB の定常値は、四捨五入だけだと 0 になる。ディザがあれば平均として 0.3 LSB が残る
        var values = Constant(0.3);

        var output = WriteInt16Stereo(values, values, DitherMode.HpTpdf).L;

        Assert.InRange(output.Average(v => (double)v), 0.28, 0.32);
        Assert.All(output, v => Assert.InRange(v, -1, 1));   // ±1 LSB に収まる
    }

    [Fact]
    public void HpTpdf_is_reproducible_from_the_seed()
    {
        var values = Enumerable.Range(0, 1000).Select(i => (float)Math.Sin(i * 0.05) * 0.001f).ToArray();

        var a = WriteInt16Stereo(values, values, DitherMode.HpTpdf, seed: 12345);
        var b = WriteInt16Stereo(values, values, DitherMode.HpTpdf, seed: 12345);
        var c = WriteInt16Stereo(values, values, DitherMode.HpTpdf, seed: 54321);

        Assert.Equal(a.L, b.L);
        Assert.NotEqual(a.L, c.L);
    }

    // ---- 全方式に共通 ----

    [Theory]
    [InlineData(DitherMode.Tpdf)]
    [InlineData(DitherMode.HpTpdf)]
    [InlineData(DitherMode.Off)]
    public void Digital_silence_stays_silent_in_every_mode(DitherMode mode)
    {
        var silence = new float[10_000];

        var (left, right) = WriteInt16Stereo(silence, silence, mode);

        Assert.All(left, v => Assert.Equal(0, v));
        Assert.All(right, v => Assert.Equal(0, v));
    }

    [Fact]
    public void Off_is_plain_rounding_and_gives_the_same_output_every_time()
    {
        var values = Constant(0.3);

        var a = WriteInt16Stereo(values, values, DitherMode.Off, seed: 1);
        var b = WriteInt16Stereo(values, values, DitherMode.Off, seed: 2);

        Assert.All(a.L, v => Assert.Equal(0, v));   // 0.3 LSB は 0 に丸まる
        Assert.Equal(a.L, b.L);
    }

    [Theory]
    [InlineData(SampleFormat.Float32)]
    [InlineData(SampleFormat.Int32)]
    public void Float32_and_Int32_ignore_the_dither_mode(SampleFormat f)
    {
        var values = Enumerable.Range(0, 2000).Select(i => (float)Math.Sin(i * 0.05) * 0.0001f).ToArray();

        byte[] Render(DitherMode mode)
        {
            var dst = new byte[values.Length * 2 * Pcm.BytesPerSample(f)];
            var state = Pcm.DitherState.Create(Seed);
            fixed (float* p = values)
            fixed (byte* pd = dst)
                Pcm.WriteStereo(p, p, f, 2, pd, values.Length, ref state, mode);
            return dst;
        }

        var tpdf = Render(DitherMode.Tpdf);
        Assert.Equal(tpdf, Render(DitherMode.HpTpdf));
        Assert.Equal(tpdf, Render(DitherMode.Off));
    }

    [Theory]
    [InlineData(SampleFormat.Int16)]
    [InlineData(SampleFormat.Int24)]
    public void The_integer_formats_that_use_dither_do_differ_by_mode(SampleFormat f)
    {
        // 24bit は 1 LSB が小さいので、その尺度で 0.3 LSB に相当する値で比べる
        double scale = f == SampleFormat.Int16 ? 32768.0 : 8388608.0;
        var values = Enumerable.Repeat((float)(0.3 / scale), 4000).ToArray();

        byte[] Render(DitherMode mode)
        {
            var dst = new byte[values.Length * 2 * Pcm.BytesPerSample(f)];
            var state = Pcm.DitherState.Create(Seed);
            fixed (float* p = values)
            fixed (byte* pd = dst)
                Pcm.WriteStereo(p, p, f, 2, pd, values.Length, ref state, mode);
            return dst;
        }

        Assert.NotEqual(Render(DitherMode.Off), Render(DitherMode.Tpdf));
        Assert.NotEqual(Render(DitherMode.Tpdf), Render(DitherMode.HpTpdf));
    }

    // ---- 画面に出す文言 ----

    [Theory]
    [InlineData(SampleFormat.Float32, DitherMode.Tpdf, "ディザ オフ")]
    [InlineData(SampleFormat.Float32, DitherMode.HpTpdf, "ディザ オフ")]
    [InlineData(SampleFormat.Float32, DitherMode.Off, "ディザ オフ")]
    [InlineData(SampleFormat.Int32, DitherMode.Tpdf, "ディザ オフ")]
    [InlineData(SampleFormat.Int32, DitherMode.HpTpdf, "ディザ オフ")]
    [InlineData(SampleFormat.Int32, DitherMode.Off, "ディザ オフ")]
    [InlineData(SampleFormat.Int24, DitherMode.Tpdf, "ディザ TPDF")]
    [InlineData(SampleFormat.Int24, DitherMode.HpTpdf, "ディザ HP-TPDF")]
    [InlineData(SampleFormat.Int24, DitherMode.Off, "ディザ オフ")]
    [InlineData(SampleFormat.Int16, DitherMode.Tpdf, "ディザ TPDF")]
    [InlineData(SampleFormat.Int16, DitherMode.HpTpdf, "ディザ HP-TPDF")]
    [InlineData(SampleFormat.Int16, DitherMode.Off, "ディザ オフ")]
    public void The_label_follows_what_actually_happens_for_the_opened_format(SampleFormat f, DitherMode mode, string expected)
    {
        Assert.Equal(expected, Pcm.DitherLabel(f, mode));
    }

    [Fact]
    public void Only_Int24_and_Int16_use_dither()
    {
        Assert.False(Pcm.UsesDither(SampleFormat.Float32));
        Assert.False(Pcm.UsesDither(SampleFormat.Int32));
        Assert.True(Pcm.UsesDither(SampleFormat.Int24));
        Assert.True(Pcm.UsesDither(SampleFormat.Int16));
    }
}
