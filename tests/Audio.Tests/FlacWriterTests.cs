using System.Diagnostics;
using Koromogae.Audio;
using Xunit;

namespace Audio.Tests;

/// <summary>
/// FLAC の書き出し。独立した復号器 (FlacTestDecoder) で読み、WAV と同じ値に完全に戻ること、ヘッダ・CRC・MD5 が正しいことを確かめる。
/// 公式の flac コマンドがある環境では、それでも検査する。
/// </summary>
public class FlacWriterTests
{
    const string FlacExe = @"D:\tools\foobar2000\encoders\flac.exe";

    // ---- 入力の信号 ----

    static (float[] L, float[] R) Sine(int n, double amp = 0.5, double freq = 440, int rate = 48000)
        => (Enumerable.Range(0, n).Select(i => (float)(amp * Math.Sin(2 * Math.PI * freq * i / rate))).ToArray(),
            Enumerable.Range(0, n).Select(i => (float)(amp * 0.7 * Math.Sin(2 * Math.PI * freq * 1.5 * i / rate + 0.5))).ToArray());

    static (float[] L, float[] R) Noise(int n, double amp = 0.5, int seed = 1)
    {
        var rnd = new Random(seed);
        return (Enumerable.Range(0, n).Select(_ => (float)((rnd.NextDouble() * 2 - 1) * amp)).ToArray(),
                Enumerable.Range(0, n).Select(_ => (float)((rnd.NextDouble() * 2 - 1) * amp)).ToArray());
    }

    static (float[] L, float[] R) Music(int n)
    {
        // 和音 + 少しの雑音 + 左右で似ているが違う (中央/差で得をする) 信号
        var rnd = new Random(7);
        var l = new float[n]; var r = new float[n];
        for (int i = 0; i < n; i++)
        {
            double t = i / 48000.0;
            double common = 0.3 * Math.Sin(2 * Math.PI * 220 * t) + 0.2 * Math.Sin(2 * Math.PI * 330 * t) + 0.1 * Math.Sin(2 * Math.PI * 1760 * t);
            double noise = (rnd.NextDouble() - 0.5) * 0.002;
            l[i] = (float)(common + 0.05 * Math.Sin(2 * Math.PI * 5 * t) + noise);
            r[i] = (float)(common - 0.03 * Math.Sin(2 * Math.PI * 7 * t) - noise);
        }
        return (l, r);
    }

    static SampleFormat Fmt(int bits) => bits == 24 ? SampleFormat.Int24 : SampleFormat.Int16;

    /// <summary>同じ入力を WAV と FLAC に書き、それぞれ整数に戻す (WAV は Pcm の丸め・ディザそのもの)。</summary>
    static (int[] WavL, int[] WavR, FlacTestDecoder Flac) RoundTrip(TempFolder dir, float[] l, float[] r, int bits, DitherMode mode,
        int level, int rate = 48000, int pieces = 1, string name = "t")
    {
        var fmt = Fmt(bits);
        using (var w = new WavWriter(dir.File(name + ".wav"), rate, fmt, mode)) w.Write(l, r);
        using (var f = new FlacWriter(dir.File(name + ".flac"), rate, fmt, mode, level))
        {
            int step = Math.Max(1, l.Length / pieces);
            for (int i = 0; i < l.Length; i += step)
            {
                int c = Math.Min(step, l.Length - i);
                f.Write(l.AsSpan(i, c), r.AsSpan(i, c));
            }
        }

        var wav = WavInfo.Read(dir.File(name + ".wav"));
        int[] wavInts = bits == 24 ? wav.AsInt24() : wav.AsInt16().Select(v => (int)v).ToArray();
        var wl = wavInts.Where((_, i) => i % 2 == 0).ToArray();
        var wr = wavInts.Where((_, i) => i % 2 == 1).ToArray();
        return (wl, wr, FlacTestDecoder.Decode(dir.File(name + ".flac")));
    }

    static void AssertSame(int[] wavL, int[] wavR, FlacTestDecoder flac)
    {
        Assert.Equal(wavL.Length, flac.Samples[0].Length);
        Assert.Equal(wavL, flac.Samples[0]);
        Assert.Equal(wavR, flac.Samples[1]);
        Assert.True(flac.Md5Matches, "MD5 が一致しません");
    }

    // ---- 完全に元に戻る ----

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    [InlineData(5)] [InlineData(6)] [InlineData(7)] [InlineData(8)]
    public void Every_compression_level_gives_back_exactly_the_samples_of_the_wav(int level)
    {
        using var dir = new TempFolder();
        var (l, r) = Music(20000);

        var (wl, wr, flac) = RoundTrip(dir, l, r, 24, DitherMode.Tpdf, level);

        AssertSame(wl, wr, flac);
    }

    [Theory]
    [InlineData(16, DitherMode.Off)]
    [InlineData(16, DitherMode.Tpdf)]
    [InlineData(16, DitherMode.HpTpdf)]
    [InlineData(24, DitherMode.Off)]
    [InlineData(24, DitherMode.HpTpdf)]
    [InlineData(16, DitherMode.Shaped)]
    [InlineData(24, DitherMode.Shaped)]
    public void Both_depths_and_all_dither_modes_give_the_same_values_as_the_wav(int bits, DitherMode mode)
    {
        using var dir = new TempFolder();
        var (l, r) = Music(15000);

        var (wl, wr, flac) = RoundTrip(dir, l, r, bits, mode, level: 5);

        AssertSame(wl, wr, flac);
        Assert.Equal(bits, flac.Bps);
    }

    [Theory]
    [InlineData(44100)] [InlineData(96000)] [InlineData(192000)]
    public void Shaped_dither_uses_the_filter_of_the_files_sample_rate_in_wav_and_flac_alike(int rate)
    {
        // WAV と FLAC は、同じレートの同じ係数でノイズシェイピングする (高いレートでは設計した 12 次)。値が完全に一致する
        using var dir = new TempFolder();
        var (l, r) = Music(20000);

        var (wl, wr, flac) = RoundTrip(dir, l, r, 16, DitherMode.Shaped, 5, rate);

        AssertSame(wl, wr, flac);
    }

    [Theory]
    [InlineData(44100)] [InlineData(48000)] [InlineData(88200)] [InlineData(96000)] [InlineData(176400)] [InlineData(192000)]
    public void Every_sample_rate_of_the_app_is_written_and_read_back(int rate)
    {
        using var dir = new TempFolder();
        var (l, r) = Sine(9000, rate: rate);

        var (wl, wr, flac) = RoundTrip(dir, l, r, 24, DitherMode.Off, 5, rate);

        AssertSame(wl, wr, flac);
        Assert.Equal(rate, flac.SampleRate);
    }

    [Theory]
    [InlineData("noise loud")]
    [InlineData("noise quiet")]
    [InlineData("silence")]
    [InlineData("dc")]
    [InlineData("identical channels")]
    [InlineData("opposite channels")]
    [InlineData("square clipping")]
    [InlineData("one channel silent")]
    [InlineData("impulse")]
    [InlineData("tiny")]
    public void Awkward_signals_come_back_exactly_at_a_low_and_a_high_level(string kind)
    {
        using var dir = new TempFolder();
        int n = 10000;
        (float[] l, float[] r) = kind switch
        {
            "noise loud" => Noise(n, 0.95),
            "noise quiet" => Noise(n, 0.0005),
            "silence" => (new float[n], new float[n]),
            "dc" => (Enumerable.Repeat(0.25f, n).ToArray(), Enumerable.Repeat(-0.5f, n).ToArray()),
            "identical channels" => (Sine(n).L, Sine(n).L),
            "opposite channels" => (Sine(n).L, Sine(n).L.Select(v => -v).ToArray()),
            "square clipping" => (Enumerable.Range(0, n).Select(i => (i / 50) % 2 == 0 ? 3f : -3f).ToArray(), Enumerable.Range(0, n).Select(i => (i / 37) % 2 == 0 ? 1f : -1f).ToArray()),
            "one channel silent" => (Sine(n).L, new float[n]),
            "impulse" => (Enumerable.Range(0, n).Select(i => i == 5000 ? 0.9f : 0f).ToArray(), new float[n]),
            _ => (Enumerable.Range(0, n).Select(i => (i % 3 - 1) * 1e-6f).ToArray(), new float[n]),
        };

        foreach (int level in new[] { 0, 8 })
            foreach (int bits in new[] { 16, 24 })
            {
                var (wl, wr, flac) = RoundTrip(dir, l, r, bits, DitherMode.Off, level, name: $"t{level}_{bits}");
                AssertSame(wl, wr, flac);
            }
    }

    [Theory]
    [InlineData(0)]            // 空のファイル
    [InlineData(1)]            // 1 サンプルだけ
    [InlineData(15)]           // 16 未満の最後のブロック
    [InlineData(1151)] [InlineData(1152)] [InlineData(1153)]       // レベル 0〜2 のブロック長の前後
    [InlineData(4095)] [InlineData(4096)] [InlineData(4097)]       // レベル 3〜8 のブロック長の前後
    [InlineData(8192)] [InlineData(12345)]
    public void Block_boundaries_and_short_files_are_handled(int n)
    {
        using var dir = new TempFolder();
        var (l, r) = n == 0 ? (new float[0], new float[0]) : Music(n);

        foreach (int level in new[] { 0, 5 })
        {
            var (wl, wr, flac) = RoundTrip(dir, l, r, 24, DitherMode.Off, level, name: $"b{level}");
            AssertSame(wl, wr, flac);
            Assert.Equal(n, flac.TotalSamples);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(100)]
    [InlineData(5000)]
    public void The_way_the_audio_is_split_into_writes_does_not_change_the_file(int pieces)
    {
        using var dir = new TempFolder();
        var (l, r) = Music(30000);
        var fmt = SampleFormat.Int24;
        using (var f = new FlacWriter(dir.File("one.flac"), 48000, fmt, DitherMode.Off, 5)) f.Write(l, r);

        RoundTrip(dir, l, r, 24, DitherMode.Off, 5, pieces: pieces, name: "many");

        Assert.Equal(File.ReadAllBytes(dir.File("one.flac")), File.ReadAllBytes(dir.File("many.flac")));
    }

    // ---- 圧縮の効果 ----

    [Fact]
    public void Tonal_music_gets_clearly_smaller_than_wav_and_higher_levels_are_not_worse()
    {
        using var dir = new TempFolder();
        var (l, r) = Music(96000);
        var sizes = new Dictionary<int, long>();
        foreach (int level in new[] { 0, 3, 5, 8 })
        {
            using (var f = new FlacWriter(dir.File($"l{level}.flac"), 48000, SampleFormat.Int24, DitherMode.Off, level)) f.Write(l, r);
            sizes[level] = new FileInfo(dir.File($"l{level}.flac")).Length;
        }
        long wav = 96000L * 6;

        Assert.True(sizes[5] < wav * 0.6, $"圧縮が効いていません: {sizes[5]} / {wav}");
        Assert.True(sizes[3] < sizes[0], "線形予測 (レベル 3) は、固定予測だけ (レベル 0) より小さいはず");
        Assert.True(sizes[5] <= sizes[3] * 1.02, "レベル 5 は 3 と同等以下のはず");
        Assert.True(sizes[8] <= sizes[5] * 1.02, "レベル 8 は 5 と同等以下のはず");
    }

    [Fact]
    public void White_noise_cannot_be_compressed_much_but_still_roundtrips()
    {
        using var dir = new TempFolder();
        var (l, r) = Noise(20000, 0.9);

        var (wl, wr, flac) = RoundTrip(dir, l, r, 24, DitherMode.Off, 5);

        AssertSame(wl, wr, flac);
        Assert.True(new FileInfo(dir.File("t.flac")).Length < new FileInfo(dir.File("t.wav")).Length * 1.05, "ほぼ無圧縮 (verbatim) でも、WAV より大きくなりすぎないはず");
    }

    [Fact]
    public void Silence_is_stored_as_constant_subframes_and_is_tiny()
    {
        using var dir = new TempFolder();
        var s = new float[48000];

        var (wl, wr, flac) = RoundTrip(dir, s, s, 24, DitherMode.Tpdf, 5);

        AssertSame(wl, wr, flac);
        Assert.All(flac.SubframeKinds, k => Assert.Equal("constant", k));
        Assert.True(new FileInfo(dir.File("t.flac")).Length < 1000);
    }

    [Fact]
    public void Linear_prediction_is_really_used_for_a_pure_tone_at_the_higher_levels()
    {
        using var dir = new TempFolder();
        var (l, r) = Sine(20000);

        var (_, _, flac) = RoundTrip(dir, l, r, 24, DitherMode.Off, 8);

        Assert.Contains(flac.SubframeKinds, k => k.StartsWith("lpc"));
        Assert.DoesNotContain("verbatim", flac.SubframeKinds);
    }

    [Fact]
    public void Level_0_to_2_use_only_fixed_prediction_and_level_3_and_up_use_linear_prediction()
    {
        using var dir = new TempFolder();
        var (l, r) = Music(20000);

        var low = RoundTrip(dir, l, r, 24, DitherMode.Off, 1, name: "low").Flac;
        var high = RoundTrip(dir, l, r, 24, DitherMode.Off, 4, name: "high").Flac;

        Assert.DoesNotContain(low.SubframeKinds, k => k.StartsWith("lpc"));
        Assert.Contains(high.SubframeKinds, k => k.StartsWith("lpc"));
    }

    [Theory]
    [InlineData(3, 6)] [InlineData(4, 8)] [InlineData(5, 8)] [InlineData(6, 8)] [InlineData(7, 12)] [InlineData(8, 12)]
    public void Levels_3_and_up_use_linear_prediction_within_the_official_order_limit(int level, int maxOrder)
    {
        // 公式の flac の -3 は次数 6、-4〜-6 は 8、-7 と -8 は 12 まで (-l)
        using var dir = new TempFolder();
        var (l, r) = Music(40000);

        var flac = RoundTrip(dir, l, r, 24, DitherMode.Off, level).Flac;

        var orders = flac.SubframeKinds.Where(k => k.StartsWith("lpc")).Select(k => int.Parse(k[3..])).ToList();
        Assert.NotEmpty(orders);
        Assert.True(orders.Max() <= maxOrder, $"レベル {level}: 次数 {orders.Max()} は上限 {maxOrder} を超えています");
    }

    [Fact]
    public void Stereo_decorrelation_is_used_when_the_channels_are_alike()
    {
        // L/R の差が小さい信号は、中央/差 (または左/差) で符号化したほうが小さい。レベル 5 (-m) は、それを選べるはず
        using var dir = new TempFolder();
        var (common, _) = Music(40000);
        var l = common;
        var r = common.Select((v, i) => v * 0.999f + (i % 5) * 1e-6f).ToArray();
        long independent, adaptive;
        using (var f = new FlacWriter(dir.File("lr.flac"), 48000, SampleFormat.Int24, DitherMode.Off, 3)) f.Write(l, r);      // レベル 3 は L/R のみ
        using (var f = new FlacWriter(dir.File("ms.flac"), 48000, SampleFormat.Int24, DitherMode.Off, 5)) f.Write(l, r);      // レベル 5 は全部試す
        independent = new FileInfo(dir.File("lr.flac")).Length;
        adaptive = new FileInfo(dir.File("ms.flac")).Length;

        Assert.True(adaptive < independent * 0.8, $"L/R のみ {independent} / 探索あり {adaptive}");
        var (wl, wr, flac) = RoundTrip(dir, l, r, 24, DitherMode.Off, 5, name: "chk");     // 中央/差で符号化しても、元の値に完全に戻る
        AssertSame(wl, wr, flac);
    }

    // ---- ヘッダ ----

    [Fact]
    public void The_streaminfo_describes_the_stream_and_the_vendor_is_recorded()
    {
        using var dir = new TempFolder();
        var (l, r) = Music(10000);

        var (_, _, flac) = RoundTrip(dir, l, r, 24, DitherMode.Off, 5, 96000);

        Assert.Equal(96000, flac.SampleRate);
        Assert.Equal(2, flac.Channels);
        Assert.Equal(24, flac.Bps);
        Assert.Equal(10000, flac.TotalSamples);
        Assert.Equal(4096, flac.MaxBlock);
        Assert.Equal(4096, flac.MinBlock);        // 最後の短いブロックは、最小の対象外
        Assert.True(flac.MinFrame > 0 && flac.MaxFrame >= flac.MinFrame);
        Assert.Equal(FlacWriter.Vendor, flac.Vendor);
        Assert.Equal(3, flac.FrameCount);         // 4096 + 4096 + 1808
    }

    [Fact]
    public void A_single_short_block_has_its_own_size_as_min_and_max()
    {
        using var dir = new TempFolder();
        var (l, r) = Music(300);

        var (_, _, flac) = RoundTrip(dir, l, r, 16, DitherMode.Off, 5);

        Assert.Equal(300, flac.MinBlock);
        Assert.Equal(300, flac.MaxBlock);
    }

    [Fact]
    public void The_subset_limits_are_respected()
    {
        // 44.1 / 48 kHz は、ブロック長 4608 以下。線形予測の次数は 12 以下
        using var dir = new TempFolder();
        var (l, r) = Music(30000);

        var flac = RoundTrip(dir, l, r, 24, DitherMode.Off, 8).Flac;

        Assert.True(flac.MaxBlock <= 4608);
        Assert.All(flac.SubframeKinds.Where(k => k.StartsWith("lpc")), k => Assert.InRange(int.Parse(k[3..]), 1, 12));
    }

    // ---- 途中でも読める・上書きしない ----

    [Fact]
    public void After_UpdateHeader_a_file_still_being_written_decodes_up_to_the_last_complete_block()
    {
        using var dir = new TempFolder();
        var (l, r) = Music(10000);
        using var w = new FlacWriter(dir.File("live.flac"), 48000, SampleFormat.Int24, DitherMode.Off, 5);
        w.Write(l, r);
        w.UpdateHeader();

        var flac = FlacTestDecoder.Decode(dir.File("live.flac"), partialOk: true);     // まだ閉じていない

        Assert.Equal(2 * 4096, flac.TotalSamples);              // 書き終えたブロックの分 (残り 1808 は保留中)
        Assert.Equal(2 * 4096, flac.Samples[0].Length);
        Assert.False(flac.Md5Matches);                          // MD5 は終了時に確定する
    }

    [Fact]
    public void A_file_cut_off_in_the_middle_still_decodes_the_complete_blocks()
    {
        // アプリが落ちたときのファイル: 最後のブロックが途中で切れている
        using var dir = new TempFolder();
        var (l, r) = Music(20000);
        using (var f = new FlacWriter(dir.File("c.flac"), 48000, SampleFormat.Int24, DitherMode.Off, 5)) f.Write(l, r);
        var bytes = File.ReadAllBytes(dir.File("c.flac"));
        File.WriteAllBytes(dir.File("cut.flac"), bytes[..(bytes.Length - 500)]);

        var flac = FlacTestDecoder.Decode(dir.File("cut.flac"), partialOk: true);

        Assert.InRange(flac.Samples[0].Length, 4096, 20000 - 1);
        Assert.Equal(0, flac.Samples[0].Length % 4096);
    }

    [Fact]
    public void An_existing_file_is_never_overwritten()
    {
        using var dir = new TempFolder();
        File.WriteAllText(dir.File("a.flac"), "大事なファイル");

        Assert.ThrowsAny<IOException>(() => new FlacWriter(dir.File("a.flac"), 48000, SampleFormat.Int24, DitherMode.Off));

        Assert.Equal("大事なファイル", File.ReadAllText(dir.File("a.flac")));
    }

    [Fact]
    public void Float32_and_unsupported_rates_are_rejected()
    {
        using var dir = new TempFolder();

        Assert.Throws<ArgumentException>(() => new FlacWriter(dir.File("a.flac"), 48000, SampleFormat.Float32, DitherMode.Off));
        Assert.Throws<ArgumentException>(() => new FlacWriter(dir.File("b.flac"), 48000, SampleFormat.Int32, DitherMode.Off));
        Assert.Throws<ArgumentException>(() => new FlacWriter(dir.File("c.flac"), 12345, SampleFormat.Int24, DitherMode.Off));
    }

    [Fact]
    public void An_out_of_range_level_is_clamped()
    {
        using var dir = new TempFolder();

        using var low = new FlacWriter(dir.File("a.flac"), 48000, SampleFormat.Int24, DitherMode.Off, -5);
        using var high = new FlacWriter(dir.File("b.flac"), 48000, SampleFormat.Int24, DitherMode.Off, 99);

        Assert.Equal(0, low.Level);
        Assert.Equal(8, high.Level);
    }

    [Fact]
    public void The_writer_reports_its_size_and_never_asks_to_split()
    {
        using var dir = new TempFolder();
        var (l, r) = Music(9000);
        using var w = new FlacWriter(dir.File("a.flac"), 48000, SampleFormat.Int24, DitherMode.Off, 5);

        long empty = w.TotalBytes;
        w.Write(l, r);

        Assert.True(empty > 40);                     // 先頭の情報欄の分
        Assert.True(w.TotalBytes > empty);
        Assert.Equal(long.MaxValue, w.RemainingFrames);
    }

    // ---- 公式の flac コマンドでの検査 ----

    static string? FlacOrNull() => File.Exists(FlacExe) ? FlacExe : null;

    static (int Exit, string Output) Run(string exe, string args)
    {
        // 標準入力を閉じる (flac が入力を待って止まらないように)。出力は非同期で読み、60 秒で打ち切る
        var psi = new ProcessStartInfo(exe, args)
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true,
        };
        using var p = Process.Start(psi)!;
        p.StandardInput.Close();
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(60_000))
        {
            p.Kill(entireProcessTree: true);
            return (-1, "flac が 60 秒で終わりませんでした");
        }
        return (p.ExitCode, stdout.Result + stderr.Result);
    }

    [SkippableTheory]
    [InlineData(0, 16)] [InlineData(3, 24)] [InlineData(5, 24)] [InlineData(5, 16)] [InlineData(8, 24)]
    public void The_official_flac_command_accepts_the_files_as_valid(int level, int bits)
    {
        string? flac = FlacOrNull();
        Skip.If(flac == null, $"flac コマンドがありません: {FlacExe}");
        using var dir = new TempFolder();
        var (l, r) = Music(50000);
        using (var w = new FlacWriter(dir.File("a.flac"), 44100, Fmt(bits), DitherMode.Tpdf, level)) w.Write(l, r);

        var (exit, output) = Run(flac!, $"-t \"{dir.File("a.flac")}\"");

        Assert.True(exit == 0 && output.Contains("ok"), $"flac -t が失敗しました (exit {exit}): {output}");
    }

    [SkippableFact]
    public void The_official_flac_command_decodes_to_exactly_the_wav_we_wrote()
    {
        string? flac = FlacOrNull();
        Skip.If(flac == null, $"flac コマンドがありません: {FlacExe}");
        using var dir = new TempFolder();
        var (l, r) = Music(50000);
        using (var w = new WavWriter(dir.File("a.wav"), 48000, SampleFormat.Int24, DitherMode.HpTpdf)) w.Write(l, r);
        using (var w = new FlacWriter(dir.File("a.flac"), 48000, SampleFormat.Int24, DitherMode.HpTpdf, 5)) w.Write(l, r);

        var (exit, output) = Run(flac!, $"-d -f -o \"{dir.File("back.wav")}\" \"{dir.File("a.flac")}\"");

        Assert.True(exit == 0, output);
        var ours = WavInfo.Read(dir.File("a.wav"));
        var back = WavInfo.Read(dir.File("back.wav"));
        Assert.Equal(ours.Data, back.Data);          // 公式の実装で復号した音が、WAV と 1 バイトも違わない
        Assert.Equal(24, back.Bits);
        Assert.Equal(48000u, back.SampleRate);
    }

    [SkippableFact]
    public void The_official_flac_command_also_verifies_a_file_with_a_short_last_block_and_silence()
    {
        string? flac = FlacOrNull();
        Skip.If(flac == null, $"flac コマンドがありません: {FlacExe}");
        using var dir = new TempFolder();
        var (m, _) = Music(10000);
        var l = m.Concat(new float[7777]).ToArray();          // 音のあとに無音
        var r = m.Select(v => -v).Concat(new float[7777]).ToArray();
        using (var w = new FlacWriter(dir.File("a.flac"), 96000, SampleFormat.Int16, DitherMode.Off, 7)) w.Write(l, r);

        var (exit, output) = Run(flac!, $"-t \"{dir.File("a.flac")}\"");

        Assert.True(exit == 0 && output.Contains("ok"), output);
    }

    [SkippableFact]
    public void Our_files_are_close_in_size_to_what_the_official_encoder_makes()
    {
        // 公式と同じビット列にはならないが、大きさは近いはず (線形予測・ステレオの探索が働いている裏付け)
        string? flac = FlacOrNull();
        Skip.If(flac == null, $"flac コマンドがありません: {FlacExe}");
        using var dir = new TempFolder();
        var (l, r) = Music(200000);
        using (var w = new WavWriter(dir.File("a.wav"), 48000, SampleFormat.Int24, DitherMode.Off)) w.Write(l, r);
        using (var w = new FlacWriter(dir.File("ours.flac"), 48000, SampleFormat.Int24, DitherMode.Off, 5)) w.Write(l, r);
        var (exit, output) = Run(flac!, $"-5 -f -o \"{dir.File("official.flac")}\" \"{dir.File("a.wav")}\"");
        Assert.Equal(0, exit);

        long ours = new FileInfo(dir.File("ours.flac")).Length;
        long official = new FileInfo(dir.File("official.flac")).Length;

        Assert.True(ours <= official * 1.15, $"自前 {ours} バイト / 公式 {official} バイト");
    }
}
