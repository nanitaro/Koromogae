using Koromogae.Audio;
using Xunit;

namespace Audio.Tests;

/// <summary>WAV の書き出し: ヘッダ、サンプルの変換、途中でも読めること、上書きしないこと。</summary>
public class WavWriterTests
{
    static float[] Ramp(int n, float scale = 1f) => Enumerable.Range(0, n).Select(i => (i % 200 - 100) / 100f * scale).ToArray();

    // ---- ヘッダ ----

    [Theory]
    [InlineData(SampleFormat.Int16, 16, 1, 4)]
    [InlineData(SampleFormat.Int24, 24, 1, 6)]
    [InlineData(SampleFormat.Float32, 32, 3, 8)]
    public void The_header_describes_a_stereo_file_in_the_chosen_format(SampleFormat format, int bits, int tag, int blockAlign)
    {
        using var dir = new TempFolder();
        var l = Ramp(1000);
        using (var w = new WavWriter(dir.File("a.wav"), 48000, format, DitherMode.Off))
            w.Write(l, l);

        var info = WavInfo.Read(dir.File("a.wav"));

        Assert.Equal(tag, info.FormatTag);          // 1 = PCM、3 = IEEE float
        Assert.Equal(2, info.Channels);
        Assert.Equal(48000u, info.SampleRate);
        Assert.Equal(bits, info.Bits);
        Assert.Equal(blockAlign, info.BlockAlign);
        Assert.Equal((uint)(48000 * blockAlign), info.ByteRate);
        Assert.Equal((uint)(1000 * blockAlign), info.DataSize);
        Assert.Equal(1000, info.Frames);
        Assert.Equal(info.FileLength - 8, info.RiffSize);
    }

    [Fact]
    public void A_float_file_has_a_fact_chunk_with_the_frame_count()
    {
        using var dir = new TempFolder();
        var l = Ramp(777);
        using (var w = new WavWriter(dir.File("f.wav"), 44100, SampleFormat.Float32, DitherMode.Off))
            w.Write(l, l);

        Assert.Equal(777u, WavInfo.Read(dir.File("f.wav")).FactFrames);
    }

    [Fact]
    public void A_pcm_file_has_no_fact_chunk()
    {
        using var dir = new TempFolder();
        using (var w = new WavWriter(dir.File("p.wav"), 44100, SampleFormat.Int16, DitherMode.Off)) { }

        Assert.Null(WavInfo.Read(dir.File("p.wav")).FactFrames);
    }

    [Fact]
    public void An_empty_file_is_still_a_valid_wav()
    {
        using var dir = new TempFolder();
        using (var w = new WavWriter(dir.File("e.wav"), 44100, SampleFormat.Int24, DitherMode.Off)) { }

        var info = WavInfo.Read(dir.File("e.wav"));

        Assert.Equal(0u, info.DataSize);
        Assert.Equal(36u, info.RiffSize);
    }

    // ---- サンプル ----

    [Fact]
    public void Float32_samples_are_written_unchanged_left_then_right()
    {
        using var dir = new TempFolder();
        var l = Ramp(500);
        var r = l.Select(v => -v * 0.5f).ToArray();
        using (var w = new WavWriter(dir.File("a.wav"), 44100, SampleFormat.Float32, DitherMode.Tpdf))   // ディザを指定しても Float32 には効かない
            w.Write(l, r);

        var data = WavInfo.Read(dir.File("a.wav")).AsFloat();

        for (int i = 0; i < 500; i++)
        {
            Assert.Equal(l[i], data[i * 2]);
            Assert.Equal(r[i], data[i * 2 + 1]);
        }
    }

    [Fact]
    public void Int24_samples_are_rounded_to_the_nearest_value_without_dither()
    {
        using var dir = new TempFolder();
        var l = new[] { 0.5f, -0.5f, 1.0f, -1.0f, 0f, 1f / 8388608f * 2.4f, 1f / 8388608f * 2.6f };
        var r = l.Select(v => -v).ToArray();
        using (var w = new WavWriter(dir.File("a.wav"), 44100, SampleFormat.Int24, DitherMode.Off))
            w.Write(l, r);

        var data = WavInfo.Read(dir.File("a.wav")).AsInt24();

        Assert.Equal(4194304, data[0]);                   // 0.5
        Assert.Equal(-4194304, data[2]);                  // -0.5
        Assert.Equal(8388607, data[4]);                   // 1.0 はクリップして最大値
        Assert.Equal(-8388608, data[6]);                  // -1.0 は最小値
        Assert.Equal(0, data[8]);
        Assert.Equal(2, data[10]);                        // 2.4 → 2
        Assert.Equal(3, data[12]);                        // 2.6 → 3 (切り捨てではなく四捨五入)
        Assert.Equal(-4194304, data[1]);                  // 右チャンネルが別の値
    }

    [Fact]
    public void Int16_values_beyond_full_scale_are_clipped_not_wrapped()
    {
        using var dir = new TempFolder();
        var l = new[] { 2.5f, -2.5f, float.PositiveInfinity, float.NaN };
        using (var w = new WavWriter(dir.File("a.wav"), 44100, SampleFormat.Int16, DitherMode.Off))
            w.Write(l, l);

        var data = WavInfo.Read(dir.File("a.wav")).AsInt16();

        Assert.Equal(32767, data[0]);
        Assert.Equal(-32768, data[2]);
        Assert.Equal(32767, data[4]);
        Assert.Equal(0, data[6]);                         // 非数は無音
    }

    [Fact]
    public void The_dither_mode_changes_the_integer_output_for_a_quiet_signal()
    {
        using var dir = new TempFolder();
        var quiet = Enumerable.Repeat(0.3f / 32768f, 4000).ToArray();   // 0.3 LSB
        foreach (var (name, mode) in new[] { ("off", DitherMode.Off), ("tpdf", DitherMode.Tpdf), ("hp", DitherMode.HpTpdf) })
            using (var w = new WavWriter(dir.File(name + ".wav"), 44100, SampleFormat.Int16, mode))
                w.Write(quiet, quiet);

        double Mean(string n) => WavInfo.Read(dir.File(n + ".wav")).AsInt16().Average(v => (double)v);

        Assert.Equal(0.0, Mean("off"), 6);                // 四捨五入だけだと 0.3 LSB は消える
        Assert.InRange(Mean("tpdf"), 0.27, 0.33);         // ディザがあれば、平均として残る
        Assert.InRange(Mean("hp"), 0.27, 0.33);
    }

    [Fact]
    public void Silence_stays_exactly_zero_even_with_dither()
    {
        using var dir = new TempFolder();
        var silence = new float[5000];
        using (var w = new WavWriter(dir.File("s.wav"), 44100, SampleFormat.Int24, DitherMode.Tpdf))
            w.Write(silence, silence);

        Assert.All(WavInfo.Read(dir.File("s.wav")).AsInt24(), v => Assert.Equal(0, v));
    }

    [Fact]
    public void Writing_in_several_pieces_gives_the_same_file_as_one_piece()
    {
        using var dir = new TempFolder();
        var l = Ramp(3000, 0.9f);
        using (var w = new WavWriter(dir.File("one.wav"), 44100, SampleFormat.Int24, DitherMode.Off)) w.Write(l, l);
        using (var w = new WavWriter(dir.File("many.wav"), 44100, SampleFormat.Int24, DitherMode.Off))
            for (int i = 0; i < 3000; i += 700)
            {
                int n = Math.Min(700, 3000 - i);
                w.Write(l.AsSpan(i, n), l.AsSpan(i, n));
            }

        Assert.Equal(File.ReadAllBytes(dir.File("one.wav")), File.ReadAllBytes(dir.File("many.wav")));
    }

    // ---- 途中でも読める ----

    [Fact]
    public void After_UpdateHeader_a_file_still_being_written_already_reads_correctly()
    {
        // アプリが落ちても、最後にヘッダを更新した時点までの音は読める
        using var dir = new TempFolder();
        var l = Ramp(2000);
        using var w = new WavWriter(dir.File("live.wav"), 44100, SampleFormat.Int16, DitherMode.Off);
        w.Write(l, l);
        w.UpdateHeader();

        var info = WavInfo.Read(dir.File("live.wav"));      // まだ閉じていない

        Assert.Equal(2000, info.Frames);
        Assert.Equal(8000u, info.DataSize);
        Assert.Equal(info.FileLength - 8, info.RiffSize);
    }

    [Fact]
    public void Writing_continues_correctly_after_a_header_update()
    {
        using var dir = new TempFolder();
        var l = Ramp(1000);
        using (var w = new WavWriter(dir.File("c.wav"), 44100, SampleFormat.Int16, DitherMode.Off))
        {
            w.Write(l, l);
            w.UpdateHeader();
            w.Write(l, l);      // 更新のあとも、末尾に追記される
        }

        var info = WavInfo.Read(dir.File("c.wav"));

        Assert.Equal(2000, info.Frames);
        var data = info.AsInt16();
        Assert.Equal(data[0], data[2000]);                // 2 回目の先頭 = 1 回目の先頭 (同じ入力)
    }

    // ---- 上限・上書き ----

    [Fact]
    public void CanAppend_stops_at_the_data_size_limit()
    {
        using var dir = new TempFolder();
        using var w = new WavWriter(dir.File("a.wav"), 44100, SampleFormat.Int16, DitherMode.Off, maxDataBytes: 4000);   // 1000 フレーム

        Assert.True(w.CanAppend(1000));
        Assert.False(w.CanAppend(1001));
        var l = new float[600];
        w.Write(l, l);
        Assert.True(w.CanAppend(400));
        Assert.False(w.CanAppend(401));
        Assert.Throws<InvalidOperationException>(() => w.Write(new float[401], new float[401]));
    }

    [Fact]
    public void The_default_limit_leaves_room_below_the_4GB_riff_limit()
    {
        Assert.True(WavWriter.DefaultMaxDataBytes < uint.MaxValue - 100);
    }

    [Fact]
    public void An_existing_file_is_never_overwritten()
    {
        using var dir = new TempFolder();
        File.WriteAllText(dir.File("a.wav"), "大事なファイル");

        Assert.ThrowsAny<IOException>(() => new WavWriter(dir.File("a.wav"), 44100, SampleFormat.Int16, DitherMode.Off));

        Assert.Equal("大事なファイル", File.ReadAllText(dir.File("a.wav")));
    }

    [Fact]
    public void The_file_can_be_read_by_another_program_while_it_is_being_written()
    {
        using var dir = new TempFolder();
        using var w = new WavWriter(dir.File("a.wav"), 44100, SampleFormat.Int16, DitherMode.Off);

        using var other = new FileStream(dir.File("a.wav"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        Assert.True(other.Length >= 44);
    }

    [Fact]
    public void Unsupported_formats_and_mismatched_channels_are_rejected()
    {
        using var dir = new TempFolder();
        Assert.Throws<ArgumentException>(() => new WavWriter(dir.File("x.wav"), 44100, SampleFormat.Int32, DitherMode.Off));

        using var w = new WavWriter(dir.File("y.wav"), 44100, SampleFormat.Int16, DitherMode.Off);
        Assert.Throws<ArgumentException>(() => w.Write(new float[10], new float[9]));
    }

    [Fact]
    public void The_frame_and_byte_counters_follow_what_was_written()
    {
        using var dir = new TempFolder();
        using var w = new WavWriter(dir.File("a.wav"), 44100, SampleFormat.Int24, DitherMode.Off);

        w.Write(new float[123], new float[123]);

        Assert.Equal(123 * 6, w.DataBytes);
        Assert.Equal(123, w.Frames);
    }
}
