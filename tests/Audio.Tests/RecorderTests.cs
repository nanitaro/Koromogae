using System.Diagnostics;
using Koromogae;
using Koromogae.Audio;
using Xunit;

namespace Audio.Tests;

/// <summary>音声スレッドからファイルへ音を渡すキュー。満杯のときの扱い (捨てる・無音で埋め直す・順序)。</summary>
public class RecordQueueTests
{
    static float[] Const(float v, int n) => Enumerable.Repeat(v, n).ToArray();

    static (float[] L, float[] R) ReadAll(RecordQueue q)
    {
        var l = new List<float>();
        var r = new List<float>();
        var bl = new float[1000];
        var br = new float[1000];
        int n;
        while ((n = q.Read(bl, br)) > 0)
        {
            l.AddRange(bl.Take(n));
            r.AddRange(br.Take(n));
        }
        return (l.ToArray(), r.ToArray());
    }

    [Fact]
    public void Pushed_audio_comes_out_in_order_on_both_channels()
    {
        var q = new RecordQueue();
        q.Push(Const(1f, 300), Const(-1f, 300));
        q.Push(Const(2f, 200), Const(-2f, 200));

        var (l, r) = ReadAll(q);

        Assert.Equal(Const(1f, 300).Concat(Const(2f, 200)), l);
        Assert.Equal(Const(-1f, 300).Concat(Const(-2f, 200)), r);
        Assert.Equal(0, q.DroppedFrames);
        Assert.Equal(0, q.Pending);
    }

    [Fact]
    public void A_read_returns_at_most_the_size_of_the_buffer()
    {
        var q = new RecordQueue();
        q.Push(Const(1f, 5000), Const(1f, 5000));

        int n = q.Read(new float[1234], new float[1234]);

        Assert.Equal(1234, n);
        Assert.Equal(5000 - 1234, q.Pending);
    }

    [Fact]
    public void Reading_an_empty_queue_returns_zero()
    {
        Assert.Equal(0, new RecordQueue().Read(new float[100], new float[100]));
    }

    [Fact]
    public void A_full_queue_drops_new_audio_instead_of_overwriting_and_counts_it()
    {
        var q = new RecordQueue();
        var block = Const(1f, 65536);
        for (int i = 0; i < RecordQueue.Frames / 65536; i++) Assert.True(q.Push(block, block));

        bool accepted = q.Push(Const(9f, 500), Const(9f, 500));

        Assert.False(accepted);
        Assert.Equal(500, q.DroppedFrames);
        Assert.Equal(RecordQueue.Frames, q.Pending);
        var (l, _) = ReadAll(q);
        Assert.All(l, v => Assert.Equal(1f, v));      // 古い音は上書きされていない
    }

    [Fact]
    public void The_dropped_length_is_filled_with_silence_so_the_timeline_stays_correct()
    {
        var q = new RecordQueue();
        var block = Const(1f, 65536);
        for (int i = 0; i < RecordQueue.Frames / 65536; i++) q.Push(block, block);
        q.Push(Const(9f, 100), Const(9f, 100));      // 捨てられる
        ReadAll(q);                                   // 書き込みスレッドが追いついた

        bool accepted = q.Push(Const(2f, 50), Const(2f, 50));
        var (l, r) = ReadAll(q);

        Assert.True(accepted);
        Assert.Equal(150, l.Length);                  // 捨てた 100 を無音で埋め、続けて新しい 50
        Assert.All(l.Take(100), v => Assert.Equal(0f, v));
        Assert.All(l.Skip(100), v => Assert.Equal(2f, v));
        Assert.All(r.Take(100), v => Assert.Equal(0f, v));
        Assert.Equal(100, q.DroppedFrames);
    }

    [Fact]
    public void While_the_gap_cannot_be_filled_new_audio_is_dropped_too_to_keep_the_order()
    {
        var q = new RecordQueue();
        var block = Const(1f, 65536);
        for (int i = 0; i < RecordQueue.Frames / 65536; i++) q.Push(block, block);
        q.Push(Const(9f, 100), Const(9f, 100));      // 捨てられる (gap = 100)

        bool accepted = q.Push(Const(2f, 50), Const(2f, 50));    // まだ満杯: 埋められないので、これも捨てる

        Assert.False(accepted);
        Assert.Equal(150, q.DroppedFrames);
        ReadAll(q);
        Assert.True(q.Push(Const(3f, 10), Const(3f, 10)));       // 空きができたら、無音 150 + 新しい 10
        var (l, _) = ReadAll(q);
        Assert.Equal(160, l.Length);
        Assert.All(l.Take(150), v => Assert.Equal(0f, v));
        Assert.All(l.Skip(150), v => Assert.Equal(3f, v));
    }

    [Fact]
    public void Wrapping_around_the_ring_keeps_the_data_intact()
    {
        var q = new RecordQueue();
        long next = 0, expected = 0;
        var buf = new float[777];
        for (int round = 0; round < 3000; round++)         // リングの何周ぶんも流す
        {
            var data = Enumerable.Range(0, 500).Select(i => (float)(next + i)).ToArray();
            Assert.True(q.Push(data, data));
            next += 500;
            int n;
            while ((n = q.Read(buf, new float[777])) > 0)
                for (int i = 0; i < n; i++) Assert.Equal((float)(expected++), buf[i]);
        }
        Assert.Equal(next, expected);
    }
}

/// <summary>録音全体: 書き込みスレッド、加工前と加工後、ファイル名、分割、ディスクの扱い。</summary>
public class RecorderTests
{
    static Recorder Make(string folder, bool dry = false, SampleFormat format = SampleFormat.Int24, DitherMode mode = DitherMode.Off,
        long maxBytes = WavWriter.DefaultMaxDataBytes, Func<long?>? free = null, string name = "20260101-120000")
        => new(folder, name, 48000, format, mode, dry, maxBytes, free ?? (() => 100L * 1024 * 1024 * 1024));

    static float[] Ramp(int n, float scale = 0.5f) => Enumerable.Range(0, n).Select(i => (i % 100 - 50) / 50f * scale).ToArray();

    // ---- 基本 ----

    [Fact]
    public void Wet_audio_is_written_to_a_file_named_after_the_start_time()
    {
        using var dir = new TempFolder();
        using var rec = Make(dir.Path);
        rec.Start();
        var l = Ramp(4800);

        for (int i = 0; i < 10; i++) Assert.True(rec.PushWet(l, l));
        rec.Stop();

        var files = Directory.GetFiles(dir.Path);
        Assert.Equal(new[] { dir.File("20260101-120000.wav") }, files);
        var info = WavInfo.Read(files[0]);
        Assert.Equal(48000, info.Frames);                // 1 秒ぶん
        Assert.Equal(24, info.Bits);
        Assert.Equal(48000u, info.SampleRate);
    }

    [Fact]
    public void The_folder_is_created_when_it_does_not_exist()
    {
        using var dir = new TempFolder();
        string sub = Path.Combine(dir.Path, "a", "b");
        using var rec = Make(sub);

        rec.Start();
        rec.Stop();

        Assert.True(Directory.Exists(sub));
        Assert.Single(Directory.GetFiles(sub));
    }

    [Fact]
    public void Everything_pushed_before_Stop_is_written()
    {
        using var dir = new TempFolder();
        using var rec = Make(dir.Path);
        rec.Start();
        var l = Ramp(5000);
        for (int i = 0; i < 100; i++) Assert.True(rec.PushWet(l, l));     // 50 万フレーム (キューのほぼ満杯) を一気に渡す

        rec.Stop();     // 書き込みスレッドが追いつく前に止めても、溜まっている分はすべて書く

        Assert.Equal(500_000, WavInfo.Read(dir.File("20260101-120000.wav")).Frames);
    }

    [Fact]
    public void After_Stop_pushing_is_refused_and_the_status_says_not_recording()
    {
        using var dir = new TempFolder();
        using var rec = Make(dir.Path);
        rec.Start();
        Assert.True(rec.IsActive);

        rec.Stop();

        Assert.False(rec.IsActive);
        Assert.False(rec.PushWet(Ramp(100), Ramp(100)));
        Assert.False(rec.Status.IsRecording);
        Assert.Null(rec.Status.Error);
    }

    [Fact]
    public void Nothing_is_accepted_before_Start()
    {
        using var dir = new TempFolder();
        using var rec = Make(dir.Path);

        Assert.False(rec.PushWet(Ramp(100), Ramp(100)));
        Assert.Empty(Directory.GetFiles(dir.Path));
    }

    // ---- 加工前 ----

    [Fact]
    public void Dry_audio_goes_to_a_second_file_with_its_own_content_and_the_same_length()
    {
        using var dir = new TempFolder();
        using var rec = Make(dir.Path, dry: true);
        rec.Start();
        var dry = Ramp(2000, 0.4f);
        var wet = Ramp(2000, 0.2f);       // プラグインで小さくなった想定

        for (int i = 0; i < 5; i++)
        {
            rec.PushDry(dry, dry);
            rec.PushWet(wet, wet);
        }
        rec.Stop();

        var d = WavInfo.Read(dir.File("20260101-120000_dry.wav"));
        var w = WavInfo.Read(dir.File("20260101-120000.wav"));
        Assert.Equal(10000, d.Frames);
        Assert.Equal(w.Frames, d.Frames);
        Assert.NotEqual(w.Data, d.Data);
        Assert.True(d.AsInt24().Max() > w.AsInt24().Max());
    }

    [Fact]
    public void Without_the_dry_option_no_dry_file_is_made_and_dry_pushes_are_ignored()
    {
        using var dir = new TempFolder();
        using var rec = Make(dir.Path, dry: false);
        rec.Start();

        Assert.False(rec.PushDry(Ramp(100), Ramp(100)));
        rec.PushWet(Ramp(100), Ramp(100));
        rec.Stop();

        Assert.Single(Directory.GetFiles(dir.Path));
        Assert.Null(rec.Status.DryPath);
        Assert.Equal(0, rec.Status.DryBytes);
    }

    [Fact]
    public void With_dither_the_wet_and_dry_files_use_independent_random_streams()
    {
        // 同じ入力でも、ディザの乱数は別。ビット単位では一致しない (長さは同じ)
        using var dir = new TempFolder();
        using var rec = Make(dir.Path, dry: true, format: SampleFormat.Int16, mode: DitherMode.Tpdf);
        rec.Start();
        var quiet = Enumerable.Repeat(0.3f / 32768f, 4000).ToArray();

        rec.PushDry(quiet, quiet);
        rec.PushWet(quiet, quiet);
        rec.Stop();

        var d = WavInfo.Read(dir.File("20260101-120000_dry.wav"));
        var w = WavInfo.Read(dir.File("20260101-120000.wav"));
        Assert.Equal(w.Frames, d.Frames);
        Assert.NotEqual(w.Data, d.Data);
    }

    [Fact]
    public void Without_dither_the_wet_and_dry_files_are_identical_for_the_same_input()
    {
        using var dir = new TempFolder();
        using var rec = Make(dir.Path, dry: true, format: SampleFormat.Int16, mode: DitherMode.Off);
        rec.Start();
        var l = Ramp(3000);

        rec.PushDry(l, l);
        rec.PushWet(l, l);
        rec.Stop();

        Assert.Equal(File.ReadAllBytes(dir.File("20260101-120000.wav")), File.ReadAllBytes(dir.File("20260101-120000_dry.wav")));
    }

    // ---- ファイル名 ----

    [Fact]
    public void An_existing_recording_is_not_overwritten_a_new_name_is_used()
    {
        using var dir = new TempFolder();
        File.WriteAllText(dir.File("20260101-120000.wav"), "前の録音");
        using var rec = Make(dir.Path);

        rec.Start();
        rec.PushWet(Ramp(100), Ramp(100));
        rec.Stop();

        Assert.Equal("前の録音", File.ReadAllText(dir.File("20260101-120000.wav")));
        Assert.Equal(100, WavInfo.Read(dir.File("20260101-120000-2.wav")).Frames);
    }

    [Fact]
    public void An_existing_dry_file_alone_also_makes_the_recorder_choose_a_new_name()
    {
        using var dir = new TempFolder();
        File.WriteAllText(dir.File("20260101-120000_dry.wav"), "前の加工前");
        using var rec = Make(dir.Path, dry: true);

        rec.Start();
        rec.Stop();

        Assert.Equal("前の加工前", File.ReadAllText(dir.File("20260101-120000_dry.wav")));
        Assert.True(File.Exists(dir.File("20260101-120000-2.wav")));
        Assert.True(File.Exists(dir.File("20260101-120000-2_dry.wav")));
    }

    [Fact]
    public void A_name_is_not_reused_when_a_split_file_of_an_earlier_recording_exists()
    {
        using var dir = new TempFolder();
        File.WriteAllText(dir.File("20260101-120000_002.wav"), "分割された前の録音");
        using var rec = Make(dir.Path);

        rec.Start();
        rec.Stop();

        Assert.True(File.Exists(dir.File("20260101-120000-2.wav")));
    }

    // ---- 分割 ----

    [Fact]
    public void Recording_moves_on_to_numbered_files_before_reaching_the_size_limit()
    {
        using var dir = new TempFolder();
        long max = 6 * 5000;     // 24bit ステレオ 5000 フレームぶん
        using var rec = Make(dir.Path, dry: true, maxBytes: max);
        rec.Start();
        var l = Ramp(1000);

        for (int i = 0; i < 12; i++) { rec.PushDry(l, l); rec.PushWet(l, l); }
        rec.Stop();

        var wet = Directory.GetFiles(dir.Path, "20260101-120000*.wav").Where(f => !f.Contains("_dry")).OrderBy(f => f, StringComparer.Ordinal).ToArray();
        var dry = Directory.GetFiles(dir.Path, "*_dry*.wav").OrderBy(f => f, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "20260101-120000.wav", "20260101-120000_002.wav", "20260101-120000_003.wav" },
            wet.Select(Path.GetFileName));
        Assert.Equal(new[] { "20260101-120000_dry.wav", "20260101-120000_dry_002.wav", "20260101-120000_dry_003.wav" },
            dry.Select(Path.GetFileName));
        Assert.All(wet.Concat(dry), f => Assert.True(WavInfo.Read(f).DataSize <= max));
        Assert.Equal(12000, wet.Sum(f => WavInfo.Read(f).Frames));    // 切り替えで欠けない
        Assert.Equal(12000, dry.Sum(f => WavInfo.Read(f).Frames));
        Assert.Equal(12000L * 6, rec.Status.WetBytes);                // 状態の合計も、分かれた分を足したもの
    }

    [Fact]
    public void Samples_are_continuous_across_the_split()
    {
        using var dir = new TempFolder();
        using var rec = Make(dir.Path, maxBytes: 6 * 1500);
        rec.Start();
        var data = Enumerable.Range(0, 4096).Select(i => (i % 2000) / 4000f).ToArray();     // 全部ちがう値が続く
        rec.PushWet(data, data);
        rec.Stop();

        var joined = new List<int>();
        foreach (var f in Directory.GetFiles(dir.Path).OrderBy(f => f, StringComparer.Ordinal))
            joined.AddRange(WavInfo.Read(f).AsInt24().Where((_, i) => i % 2 == 0));    // 左だけ

        Assert.Equal(4096, joined.Count);
        Assert.Equal(data.Select(v => (int)MathF.Floor(v * 8388608f + 0.5f)), joined);   // 四捨五入 (半分は上へ)
    }

    // ---- 途中でも読める・状態 ----

    [Fact]
    public void While_recording_the_header_is_refreshed_about_every_second()
    {
        using var dir = new TempFolder();
        using var rec = Make(dir.Path);
        rec.Start();
        rec.PushWet(Ramp(3000), Ramp(3000));

        Thread.Sleep(1500);     // 1 秒ごとのヘッダ更新を待つ (録音はまだ続いている)

        var info = WavInfo.Read(dir.File("20260101-120000.wav"));
        Assert.Equal(3000, info.Frames);
        Assert.Equal(info.FileLength - 8, info.RiffSize);
        rec.Stop();
    }

    [Fact]
    public void The_status_reports_elapsed_time_bytes_and_the_current_file()
    {
        using var dir = new TempFolder();
        using var rec = Make(dir.Path);
        rec.Start();
        rec.PushWet(Ramp(1000), Ramp(1000));
        Thread.Sleep(200);

        var status = rec.Status;
        rec.Stop();

        Assert.True(status.IsRecording);
        Assert.True(status.Elapsed >= TimeSpan.FromMilliseconds(150));
        Assert.Equal(dir.File("20260101-120000.wav"), status.WetPath);
        Assert.Equal(1000L * 6, rec.Status.WetBytes);
        Assert.Equal(0, status.DroppedMs);
    }

    // ---- 失敗 ----

    [Fact]
    public void Start_fails_with_an_exception_when_the_folder_cannot_be_created()
    {
        using var dir = new TempFolder();
        File.WriteAllText(dir.File("blocker"), "x");     // フォルダの名前にファイルがある
        using var rec = Make(Path.Combine(dir.File("blocker"), "sub"));

        Assert.ThrowsAny<Exception>(() => rec.Start());

        Assert.False(rec.IsActive);
    }

    [Fact]
    public void Start_is_refused_when_the_disk_is_nearly_full()
    {
        using var dir = new TempFolder();
        using var rec = Make(dir.Path, free: () => 1024 * 1024);      // 1 MB

        Assert.Throws<IOException>(() => rec.Start());

        Assert.Empty(Directory.GetFiles(dir.Path));
    }

    [Fact]
    public void A_low_disk_warning_is_raised_but_recording_continues()
    {
        using var dir = new TempFolder();
        using var rec = Make(dir.Path, free: () => 100L * 1024 * 1024);     // 100 MB: 警告の下限 (500MB) は下回り、停止の下限 (20MB) は上回る
        rec.Start();
        rec.PushWet(Ramp(1000), Ramp(1000));

        Thread.Sleep(1500);

        Assert.True(rec.Status.LowDisk);
        Assert.True(rec.Status.IsRecording);
        Assert.Null(rec.Status.Error);
        rec.Stop();
    }

    [Fact]
    public void Recording_stops_by_itself_when_the_disk_runs_out_and_the_audio_so_far_is_kept()
    {
        using var dir = new TempFolder();
        long free = 100L * 1024 * 1024 * 1024;
        using var rec = Make(dir.Path, free: () => Volatile.Read(ref free));
        rec.Start();
        rec.PushWet(Ramp(2000), Ramp(2000));
        Thread.Sleep(300);

        Volatile.Write(ref free, 1024 * 1024);     // 空きが 1 MB に
        var deadline = Stopwatch.StartNew();
        while (rec.Status.Error == null && deadline.Elapsed < TimeSpan.FromSeconds(5)) Thread.Sleep(50);

        var status = rec.Status;
        Assert.NotNull(status.Error);
        Assert.Contains("ディスクの空き", status.Error);
        Assert.False(status.IsRecording);
        Assert.False(rec.PushWet(Ramp(10), Ramp(10)));       // 以後は受け付けない (再生は影響を受けない)
        Assert.Equal(2000, WavInfo.Read(dir.File("20260101-120000.wav")).Frames);      // それまでの音は読める
    }

    [Fact]
    public void The_disk_messages_are_english_on_an_english_screen_and_say_there_is_not_enough_space()
    {
        // 空きが 20MB を下回って止まるときの文は、「0 になった」ではなく「続けるには足りない」(MB の数字と矛盾しない)
        using (Loc.Override(Loc.English))
        {
            using var startDir = new TempFolder();
            using var refused = Make(startDir.Path, free: () => 1024 * 1024);
            var ex = Assert.Throws<IOException>(() => refused.Start());
            Assert.Equal("Not enough free space on the destination disk (1 MB)", ex.Message);

            using var dir = new TempFolder();
            long free = 100L * 1024 * 1024 * 1024;
            using var rec = Make(dir.Path, free: () => Volatile.Read(ref free));
            rec.Start();
            rec.PushWet(Ramp(2000), Ramp(2000));
            Thread.Sleep(300);
            Volatile.Write(ref free, 3L * 1024 * 1024);
            var deadline = Stopwatch.StartNew();
            while (rec.Status.Error == null && deadline.Elapsed < TimeSpan.FromSeconds(5)) Thread.Sleep(50);

            Assert.Equal("Recording was stopped: Not enough free space to continue recording (3 MB available)", rec.Status.Error);
        }
    }
}