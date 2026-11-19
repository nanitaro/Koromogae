using Koromogae.Audio;
using Xunit;

namespace Audio.Tests;

/// <summary>整数出力への変換 (四捨五入・ディザ・クリップ) と、デバイス形式 ⇄ ステレオ float の変換のテスト。</summary>
public unsafe class PcmTests
{
    const uint Seed = 0x2545F491;

    // ---- 1 サンプルを書き出して、整数値として読み戻すための補助 ----

    static int WriteOne(SampleFormat f, float v, bool dither = false, uint seed = Seed)
    {
        var l = new[] { v };
        var r = new[] { v };
        var dst = new byte[8];
        var state = Pcm.DitherState.Create(seed);
        fixed (float* pl = l, pr = r)
        fixed (byte* pd = dst)
            Pcm.WriteStereo(pl, pr, f, 1, pd, 1, ref state, dither ? DitherMode.Tpdf : DitherMode.Off);

        return f switch
        {
            SampleFormat.Int16 => BitConverter.ToInt16(dst, 0),
            SampleFormat.Int24 => ((dst[0] | (dst[1] << 8) | (dst[2] << 16)) << 8) >> 8,   // 符号拡張
            SampleFormat.Int32 => BitConverter.ToInt32(dst, 0),
            _ => throw new ArgumentException("整数形式のみ"),
        };
    }

    static byte[] WriteMany(SampleFormat f, float[] values, bool dither, uint seed = Seed)
    {
        var dst = new byte[values.Length * Pcm.BytesPerSample(f)];
        var state = Pcm.DitherState.Create(seed);
        fixed (float* p = values)
        fixed (byte* pd = dst)
            Pcm.WriteStereo(p, p, f, 1, pd, values.Length, ref state, dither ? DitherMode.Tpdf : DitherMode.Off);
        return dst;
    }

    static float[] ReadMono(SampleFormat f, byte[] data, int frames)
    {
        var l = new float[frames];
        var r = new float[frames];
        fixed (byte* pd = data)
        fixed (float* pl = l, pr = r)
            Pcm.ReadStereo(pd, f, 1, frames, pl, pr);
        return l;
    }

    // ---- 四捨五入 ----

    [Theory]
    [InlineData(1.6f, 2)]
    [InlineData(1.4f, 1)]
    [InlineData(-1.6f, -2)]
    [InlineData(-1.4f, -1)]
    [InlineData(0.4f, 0)]
    [InlineData(-0.4f, 0)]
    [InlineData(100.51f, 101)]
    public void Int16_rounds_to_nearest_instead_of_truncating(float lsb, int expected)
    {
        // 以前の実装は (short)(v * 32767f) で、1.6 LSB を 1 に切り捨てていた
        Assert.Equal(expected, WriteOne(SampleFormat.Int16, lsb / 32768f));
    }

    [Fact]
    public void Rounding_error_never_exceeds_half_an_lsb()
    {
        var rng = new Random(1);
        foreach (var f in new[] { SampleFormat.Int16, SampleFormat.Int24 })
        {
            float scale = f == SampleFormat.Int16 ? 32768f : 8388608f;
            for (int i = 0; i < 20000; i++)
            {
                float v = (float)(rng.NextDouble() * 1.8 - 0.9);
                double error = Math.Abs(WriteOne(f, v) - (double)v * scale);
                // v は float なので、24bit では v * scale 自体にも float の丸めが入る。その分だけ余裕を見る
                Assert.True(error <= 0.5 + 1e-3, $"{f}: v={v} 誤差 {error} LSB");
            }
        }
    }

    // ---- 整数 → float → 整数 の往復 ----

    [Fact]
    public void Every_int16_value_round_trips_exactly_without_dither()
    {
        var all = Enumerable.Range(short.MinValue, 65536).Select(i => (short)i).ToArray();
        var bytes = new byte[all.Length * 2];
        Buffer.BlockCopy(all, 0, bytes, 0, bytes.Length);

        var asFloat = ReadMono(SampleFormat.Int16, bytes, all.Length);
        var back = WriteMany(SampleFormat.Int16, asFloat, dither: false);

        Assert.Equal(bytes, back);
    }

    [Fact]
    public void Int24_values_round_trip_exactly_without_dither()
    {
        var values = new List<int> { -8388608, -8388607, -1, 0, 1, 8388606, 8388607 };
        for (int v = -8388608; v <= 8388607; v += 4099) values.Add(v);

        var bytes = new byte[values.Count * 3];
        for (int i = 0; i < values.Count; i++)
        {
            bytes[i * 3] = (byte)values[i];
            bytes[i * 3 + 1] = (byte)(values[i] >> 8);
            bytes[i * 3 + 2] = (byte)(values[i] >> 16);
        }

        var asFloat = ReadMono(SampleFormat.Int24, bytes, values.Count);
        var back = WriteMany(SampleFormat.Int24, asFloat, dither: false);

        Assert.Equal(bytes, back);
    }

    [Fact]
    public void Int32_values_representable_in_float_round_trip_exactly()
    {
        // float の仮数は 24bit なので、下位 8bit が 0 の値なら厳密に表せる
        var values = new List<int> { int.MinValue, 0, 256, -256, 2147483392 };
        for (long v = int.MinValue; v < int.MaxValue - 1000; v += 256 * 100003) values.Add((int)v);

        var bytes = new byte[values.Count * 4];
        Buffer.BlockCopy(values.ToArray(), 0, bytes, 0, bytes.Length);

        var asFloat = ReadMono(SampleFormat.Int32, bytes, values.Count);
        var back = WriteMany(SampleFormat.Int32, asFloat, dither: false);

        Assert.Equal(bytes, back);
    }

    // ---- クリップ・異常値 ----

    [Theory]
    [InlineData(SampleFormat.Int16, short.MaxValue, short.MinValue)]
    [InlineData(SampleFormat.Int24, 8388607, -8388608)]
    public void Full_scale_and_overload_clip_without_wrapping(SampleFormat f, int max, int min)
    {
        Assert.Equal(max, WriteOne(f, 1.0f));
        Assert.Equal(min, WriteOne(f, -1.0f));
        Assert.Equal(max, WriteOne(f, 5.0f));
        Assert.Equal(min, WriteOne(f, -5.0f));
        Assert.Equal(max, WriteOne(f, float.PositiveInfinity));
        Assert.Equal(min, WriteOne(f, float.NegativeInfinity));
        // ディザは ±1 LSB の乱数を足すので、フルスケールぴったりにはならないが、範囲を越えて桁あふれ (符号の反転) はしない
        for (uint seed = 1; seed <= 200; seed++)
        {
            Assert.InRange(WriteOne(f, 1.0f, dither: true, seed: seed), max - 1, max);
            Assert.InRange(WriteOne(f, -1.0f, dither: true, seed: seed), min, min + 1);
        }
    }

    [Fact]
    public void Int32_full_scale_clips_without_wrapping()
    {
        Assert.True(WriteOne(SampleFormat.Int32, 1.0f) > 2147483000);
        Assert.True(WriteOne(SampleFormat.Int32, 5.0f) > 2147483000);
        Assert.Equal(int.MinValue, WriteOne(SampleFormat.Int32, -1.0f));
        Assert.Equal(int.MinValue, WriteOne(SampleFormat.Int32, -5.0f));
    }

    [Theory]
    [InlineData(SampleFormat.Int16)]
    [InlineData(SampleFormat.Int24)]
    [InlineData(SampleFormat.Int32)]
    public void NaN_becomes_silence(SampleFormat f)
    {
        Assert.Equal(0, WriteOne(f, float.NaN));
        Assert.Equal(0, WriteOne(f, float.NaN, dither: true));
    }

    [Fact]
    public void Float32_output_is_clamped_but_otherwise_untouched()
    {
        var bytes = WriteMany(SampleFormat.Float32, new[] { 0.123456f, 2f, -2f, float.NaN, 0f }, dither: true);
        var result = new float[5];
        Buffer.BlockCopy(bytes, 0, result, 0, bytes.Length);

        Assert.Equal(new[] { 0.123456f, 1f, -1f, 0f, 0f }, result);
    }

    // ---- ディザ ----

    [Theory]
    [InlineData(SampleFormat.Int16)]
    [InlineData(SampleFormat.Int24)]
    public void Digital_silence_stays_silent_even_with_dither(SampleFormat f)
    {
        var bytes = WriteMany(f, new float[10000], dither: true);

        Assert.All(bytes, b => Assert.Equal(0, b));   // 無音にヒスノイズを足さない
    }

    [Fact]
    public void Dither_preserves_a_signal_smaller_than_one_lsb_on_average()
    {
        const int n = 400_000;
        var values = Enumerable.Repeat(0.3f / 32768f, n).ToArray();   // 0.3 LSB の直流

        var plain = ReadAsInt16(WriteMany(SampleFormat.Int16, values, dither: false));
        var dithered = ReadAsInt16(WriteMany(SampleFormat.Int16, values, dither: true));

        Assert.All(plain, v => Assert.Equal(0, v));                   // ディザなしでは、小さい信号は完全に消える
        Assert.InRange(dithered.Average(v => (double)v), 0.29, 0.31); // ディザありでは、平均として 0.3 LSB が残る
        Assert.All(dithered, v => Assert.InRange(v, -1, 1));          // TPDF なので ±1 LSB に収まる
    }

    [Fact]
    public void Dither_error_power_does_not_depend_on_the_signal_level()
    {
        // TPDF の特徴: 量子化誤差 + ディザの総電力が、信号の大きさによらず一定 (= 1/12 + 1/6 = 0.25 LSB^2)。
        // 一様分布のディザだと、平均は保たれても、誤差の電力が信号の大きさで変わる (ノイズが信号に追従して揺れる)
        const int n = 200_000;
        var powers = new List<double>();
        foreach (double offsetLsb in new[] { 0.05, 0.25, 0.5, 0.75 })
        {
            var values = Enumerable.Repeat((float)(offsetLsb / 32768.0), n).ToArray();
            var output = ReadAsInt16(WriteMany(SampleFormat.Int16, values, dither: true));

            double[] errors = output.Select(v => v - offsetLsb).ToArray();
            double mean = errors.Average();
            powers.Add(errors.Average(e => (e - mean) * (e - mean)));
        }

        Assert.All(powers, p => Assert.InRange(p, 0.24, 0.26));
        Assert.True(powers.Max() - powers.Min() < 0.01, $"誤差電力が信号レベルで変わっています: {string.Join(", ", powers.Select(p => p.ToString("F4")))}");
    }

    [Fact]
    public void Dither_removes_harmonic_distortion_from_a_quiet_sine()
    {
        // 48kHz で 1kHz (1 周期 = 48 サンプルで、量子化誤差も 48 サンプル周期になり、高調波だけに集中する)。
        // 振幅 3.3 LSB (-80dBFS) の小さな正弦波。
        const int n = 48000;
        var sine = new float[n];
        for (int i = 0; i < n; i++) sine[i] = (float)(3.3 / 32768.0 * Math.Sin(2 * Math.PI * 1000 * i / 48000.0));

        double withoutDither = HarmonicPower(ReadAsInt16(WriteMany(SampleFormat.Int16, sine, dither: false)));
        double withDither = HarmonicPower(ReadAsInt16(WriteMany(SampleFormat.Int16, sine, dither: true)));

        // 高調波の電力が 10dB (電力比 10 倍) 以上減ること
        Assert.True(withoutDither > 10 * withDither,
            $"高調波電力: ディザなし {withoutDither:E3} / ディザあり {withDither:E3} (比 {withoutDither / withDither:F1})");
    }

    [Fact]
    public void The_same_seed_gives_the_same_output_and_different_seeds_differ()
    {
        var values = Enumerable.Range(0, 2000).Select(i => (float)Math.Sin(i * 0.01) * 0.001f).ToArray();

        var a = WriteMany(SampleFormat.Int16, values, dither: true, seed: 12345);
        var b = WriteMany(SampleFormat.Int16, values, dither: true, seed: 12345);
        var c = WriteMany(SampleFormat.Int16, values, dither: true, seed: 54321);

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }

    // ---- チャンネルの扱い ----

    [Fact]
    public void Mono_output_averages_left_and_right()
    {
        var l = new[] { 0.5f, -0.25f };
        var r = new[] { 0.1f, -0.75f };
        var dst = new byte[4];
        var state = Pcm.DitherState.Create(Seed);
        fixed (float* pl = l, pr = r)
        fixed (byte* pd = dst)
            Pcm.WriteStereo(pl, pr, SampleFormat.Int16, 1, pd, 2, ref state, DitherMode.Off);

        Assert.Equal((short)Math.Round(0.3 * 32768), BitConverter.ToInt16(dst, 0));
        Assert.Equal((short)Math.Round(-0.5 * 32768), BitConverter.ToInt16(dst, 2));
    }

    [Fact]
    public void Multichannel_output_puts_left_and_right_first_and_silences_the_rest()
    {
        var l = new[] { 0.5f };
        var r = new[] { -0.5f };
        var dst = new byte[8];
        Array.Fill(dst, (byte)0xAA);   // 書かれなかったバイトが残っていないことを確かめる
        var state = Pcm.DitherState.Create(Seed);
        fixed (float* pl = l, pr = r)
        fixed (byte* pd = dst)
            Pcm.WriteStereo(pl, pr, SampleFormat.Int16, 4, pd, 1, ref state, DitherMode.Tpdf);

        Assert.InRange(BitConverter.ToInt16(dst, 0), 16383, 16385);     // ディザで ±1 LSB 揺れる
        Assert.InRange(BitConverter.ToInt16(dst, 2), -16385, -16383);
        Assert.Equal(0, BitConverter.ToInt16(dst, 4));
        Assert.Equal(0, BitConverter.ToInt16(dst, 6));
    }

    [Fact]
    public void Mono_input_is_duplicated_to_both_channels()
    {
        var src = new short[] { 1000, -2000 };
        var bytes = new byte[4];
        Buffer.BlockCopy(src, 0, bytes, 0, 4);
        var l = new float[2];
        var r = new float[2];
        fixed (byte* pd = bytes)
        fixed (float* pl = l, pr = r)
            Pcm.ReadStereo(pd, SampleFormat.Int16, 1, 2, pl, pr);

        Assert.Equal(l, r);
        Assert.Equal(1000 / 32768f, l[0]);
        Assert.Equal(-2000 / 32768f, l[1]);
    }

    // ---- 補助 ----

    static short[] ReadAsInt16(byte[] bytes)
    {
        var result = new short[bytes.Length / 2];
        Buffer.BlockCopy(bytes, 0, result, 0, bytes.Length);
        return result;
    }

    /// <summary>1kHz の整数倍の周波数 (2〜10 次の高調波) の電力の合計。基本波は含めない。</summary>
    static double HarmonicPower(short[] samples)
    {
        int n = samples.Length;
        double total = 0;
        for (int harmonic = 2; harmonic <= 10; harmonic++)
        {
            double re = 0, im = 0;
            for (int i = 0; i < n; i++)
            {
                double angle = 2 * Math.PI * harmonic * 1000 * i / 48000.0;
                re += samples[i] * Math.Cos(angle);
                im -= samples[i] * Math.Sin(angle);
            }
            total += (re * re + im * im) / ((double)n * n);
        }
        return total;
    }
}
