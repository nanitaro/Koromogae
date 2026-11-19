using Koromogae.Audio;
using Xunit;

namespace Audio.Tests;

/// <summary>録音を FLAC で行うとき: ファイル名・書き込みスレッド・分割しないこと・WAV と同じ値になること。</summary>
public class RecorderFlacTests
{
    static Recorder Make(string folder, SampleFormat format = SampleFormat.Int24, DitherMode mode = DitherMode.Off, bool dry = false,
        RecordFormat file = RecordFormat.Flac, int level = 5, long maxBytes = WavWriter.DefaultMaxDataBytes, string name = "20260101-120000")
        => new(folder, name, 48000, format, mode, dry, maxBytes, () => 100L * 1024 * 1024 * 1024, file, level);

    static float[] Music(int n, double amp = 0.4)
        => Enumerable.Range(0, n).Select(i => (float)(amp * Math.Sin(i * 0.05) + 0.1 * Math.Sin(i * 0.31))).ToArray();

    [Fact]
    public void A_flac_recording_writes_a_dot_flac_file_that_decodes_to_the_pushed_audio()
    {
        using var dir = new TempFolder();
        var l = Music(30000);
        var r = l.Select(v => -v * 0.5f).ToArray();
        using (var rec = Make(dir.Path))
        {
            rec.Start();
            for (int i = 0; i < l.Length; i += 3000) rec.PushWet(l.AsSpan(i, 3000), r.AsSpan(i, 3000));
            rec.Stop();
        }

        Assert.Equal(new[] { dir.File("20260101-120000.flac") }, Directory.GetFiles(dir.Path));
        var flac = FlacTestDecoder.Decode(dir.File("20260101-120000.flac"));
        Assert.Equal(30000, flac.Samples[0].Length);
        Assert.True(flac.Md5Matches);
        Assert.Equal(24, flac.Bps);
        Assert.Equal(48000, flac.SampleRate);
    }

    [Fact]
    public void The_flac_file_has_the_same_values_as_a_wav_recording_of_the_same_audio()
    {
        using var dir = new TempFolder();
        var l = Music(20000);
        var r = Music(20000, 0.2);
        foreach (var (file, name) in new[] { (RecordFormat.Wav, "w"), (RecordFormat.Flac, "f") })
        {
            using var rec = Make(dir.Path, mode: DitherMode.HpTpdf, file: file, name: name);
            rec.Start();
            rec.PushWet(l, r);
            rec.Stop();
        }

        var wav = WavInfo.Read(dir.File("w.wav")).AsInt24();
        var flac = FlacTestDecoder.Decode(dir.File("f.flac"));
        Assert.Equal(wav.Where((_, i) => i % 2 == 0), flac.Samples[0]);
        Assert.Equal(wav.Where((_, i) => i % 2 == 1), flac.Samples[1]);
    }

    [Fact]
    public void Int16_and_the_dry_file_work_too_and_both_files_have_the_same_length()
    {
        using var dir = new TempFolder();
        var dry = Music(15000, 0.5);
        var wet = Music(15000, 0.1);
        using (var rec = Make(dir.Path, SampleFormat.Int16, DitherMode.Tpdf, dry: true))
        {
            rec.Start();
            rec.PushDry(dry, dry);
            rec.PushWet(wet, wet);
            rec.Stop();
        }

        Assert.Equal(new[] { "20260101-120000.flac", "20260101-120000_dry.flac" },
            Directory.GetFiles(dir.Path).Select(Path.GetFileName).OrderBy(f => f, StringComparer.Ordinal).Reverse().Reverse());
        var w = FlacTestDecoder.Decode(dir.File("20260101-120000.flac"));
        var d = FlacTestDecoder.Decode(dir.File("20260101-120000_dry.flac"));
        Assert.Equal(16, w.Bps);
        Assert.Equal(w.Samples[0].Length, d.Samples[0].Length);
        Assert.True(d.Samples[0].Max() > w.Samples[0].Max());          // 加工前のほうが大きい
        Assert.True(w.Md5Matches && d.Md5Matches);
    }

    [Fact]
    public void Float32_cannot_be_recorded_as_flac()
    {
        using var dir = new TempFolder();

        Assert.Throws<ArgumentException>(() => Make(dir.Path, SampleFormat.Float32));
    }

    [Fact]
    public void A_flac_recording_never_splits_into_numbered_files_whatever_the_wav_size_limit()
    {
        using var dir = new TempFolder();
        using (var rec = Make(dir.Path, maxBytes: 6 * 1000))     // WAV なら 1000 フレームごとに分かれる上限
        {
            rec.Start();
            for (int i = 0; i < 10; i++) rec.PushWet(Music(5000), Music(5000));
            rec.Stop();
        }

        Assert.Single(Directory.GetFiles(dir.Path));
        Assert.Equal(50000, FlacTestDecoder.Decode(dir.File("20260101-120000.flac")).Samples[0].Length);
    }

    [Fact]
    public void An_existing_wav_or_flac_of_the_same_name_makes_the_recorder_pick_a_new_name()
    {
        using var dir = new TempFolder();
        File.WriteAllText(dir.File("20260101-120000.wav"), "前の WAV");
        File.WriteAllText(dir.File("20260101-120000-2.flac"), "前の FLAC");
        using var rec = Make(dir.Path);

        rec.Start();
        rec.PushWet(Music(100), Music(100));
        rec.Stop();

        Assert.Equal("前の WAV", File.ReadAllText(dir.File("20260101-120000.wav")));
        Assert.Equal("前の FLAC", File.ReadAllText(dir.File("20260101-120000-2.flac")));
        Assert.True(File.Exists(dir.File("20260101-120000-3.flac")));
    }

    [Fact]
    public void And_a_wav_recording_avoids_an_existing_flac_of_the_same_name()
    {
        using var dir = new TempFolder();
        File.WriteAllText(dir.File("20260101-120000.flac"), "前の FLAC");
        using var rec = Make(dir.Path, file: RecordFormat.Wav);

        rec.Start();
        rec.Stop();

        Assert.True(File.Exists(dir.File("20260101-120000-2.wav")));
    }

    [Fact]
    public void While_recording_the_flac_file_is_refreshed_and_readable_and_the_status_counts_compressed_bytes()
    {
        using var dir = new TempFolder();
        using var rec = Make(dir.Path);
        rec.Start();
        rec.PushWet(Music(20000), Music(20000));

        Thread.Sleep(1500);       // 1 秒ごとのヘッダ更新を待つ (録音は続いている)

        var flac = FlacTestDecoder.Decode(dir.File("20260101-120000.flac"), partialOk: true);
        Assert.Equal(4 * 4096, flac.TotalSamples);                    // 書き終えたブロックの分 (残りの 3616 は保留中)
        Assert.True(rec.Status.WetBytes > 100 && rec.Status.WetBytes < 20000L * 6, $"圧縮後のサイズが出ていません: {rec.Status.WetBytes}");
        rec.Stop();
        Assert.Equal(20000, FlacTestDecoder.Decode(dir.File("20260101-120000.flac")).Samples[0].Length);       // 止めたら、保留の分も書く
    }

    [Fact]
    public void Everything_pushed_before_Stop_is_written_including_the_partial_last_block()
    {
        using var dir = new TempFolder();
        using (var rec = Make(dir.Path, level: 8))
        {
            rec.Start();
            rec.PushWet(Music(4096 * 3 + 123), Music(4096 * 3 + 123));
            rec.Stop();
        }

        var flac = FlacTestDecoder.Decode(dir.File("20260101-120000.flac"));
        Assert.Equal(4096 * 3 + 123, flac.Samples[0].Length);
        Assert.Equal(4, flac.FrameCount);
    }
}