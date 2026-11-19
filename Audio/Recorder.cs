using System.Diagnostics;
using System.IO;

namespace Koromogae.Audio;

/// <summary>録音の状態 (画面に出す)。</summary>
public sealed record RecorderStatus(
    bool IsRecording,
    TimeSpan Elapsed,
    long WetBytes,           // 加工後のファイルに書いたバイト数 (連番で分かれた分も合計)
    long DryBytes,           // 加工前のファイル (録音しないときは 0)
    double DroppedMs,        // 書き込みが追いつかず捨てた長さ (加工後と加工前のうち長い方)
    bool LowDisk,            // ディスクの空きが少ない (警告)
    string? Error,           // 録音を止めた理由 (止まっていなければ null)
    string WetPath,          // いま書いているファイル
    string? DryPath);

/// <summary>
/// 加工後 (と、任意で加工前) の音を WAV ファイルへ録音する。
/// 音声スレッドは <see cref="PushWet"/> / <see cref="PushDry"/> でキューへコピーするだけで、ディスクには触れない。
/// ファイルへ書くのは専用のスレッド。書き込みの失敗・ディスクが満杯のときは、録音だけを止める (再生は止めない)。
/// ファイルは 1 秒ごとにヘッダを書き直す (落ちても読める)。データが 4GB に近づくと、連番の次のファイルへ切り替える。
/// このファイルは PortAudio にも画面にも依存しない (単体テストでソースごとリンクしている)。
/// </summary>
internal sealed class Recorder : IDisposable
{
    /// <summary>空きがこれを下回ったら警告 (録音は続ける)。</summary>
    public const long LowDiskBytes = 500L * 1024 * 1024;
    /// <summary>空きがこれを下回ったら録音を止める。</summary>
    public const long StopDiskBytes = 20L * 1024 * 1024;

    static readonly TimeSpan HeaderInterval = TimeSpan.FromSeconds(1);

    readonly string _folder;
    readonly string _baseName;
    readonly int _sampleRate;
    readonly SampleFormat _format;
    readonly DitherMode _mode;
    readonly bool _includeDry;
    readonly RecordFormat _fileFormat;
    readonly int _flacLevel;
    readonly long _maxDataBytes;
    readonly Func<long?> _freeSpace;

    readonly RecordQueue _wetQueue = new();
    readonly RecordQueue? _dryQueue;
    Sink? _wet;
    Sink? _dry;
    Thread? _thread;
    readonly Stopwatch _clock = new();
    volatile bool _active;
    volatile bool _stopRequested;
    volatile string? _error;
    volatile bool _lowDisk;

    /// <param name="baseName">拡張子なしの名前 (例: 20260101-120000)。加工前は <c>{baseName}_dry</c>。既にあれば <c>-2</c> のように足して避ける。</param>
    /// <param name="freeSpace">保存先の空き容量 (バイト)。測れなければ null。テストで差し替える。</param>
    public Recorder(string folder, string baseName, int sampleRate, SampleFormat format, DitherMode mode, bool includeDry,
        long maxDataBytes = WavWriter.DefaultMaxDataBytes, Func<long?>? freeSpace = null,
        RecordFormat fileFormat = RecordFormat.Wav, int flacLevel = FlacWriter.DefaultLevel)
    {
        if (fileFormat == RecordFormat.Flac && format != SampleFormat.Int24 && format != SampleFormat.Int16)
            throw new ArgumentException("FLAC に録音できるのは 24bit / 16bit 整数だけです (Float32 は保存できません)", nameof(format));   // i18n:internal
        _fileFormat = fileFormat;
        _flacLevel = flacLevel;
        _folder = folder;
        _baseName = baseName;
        _sampleRate = sampleRate;
        _format = format;
        _mode = mode;
        _includeDry = includeDry;
        _maxDataBytes = maxDataBytes;
        _freeSpace = freeSpace ?? DefaultFreeSpace;
        if (includeDry) _dryQueue = new RecordQueue();
    }

    /// <summary>加工前も録音するか。</summary>
    public bool IncludesDry => _includeDry;

    /// <summary>音声スレッドが書き込んでよいか (開始してから、止まるまで)。</summary>
    public bool IsActive => _active;

    long? DefaultFreeSpace()
    {
        try
        {
            string root = Path.GetPathRoot(Path.GetFullPath(_folder)) ?? "";
            return root.Length == 0 ? null : new DriveInfo(root).AvailableFreeSpace;
        }
        catch
        {
            return null;   // ネットワーク上のフォルダなど、測れない場合は確認しない
        }
    }

    /// <summary>フォルダを作り、ファイルを開いて、書き込みスレッドを始める。開けなければ例外 (録音は始まらない)。</summary>
    public void Start()
    {
        Directory.CreateDirectory(_folder);
        if (_freeSpace() is long free && free < StopDiskBytes)
            throw new IOException(Loc.F("Rec.Err.DiskLow", free / 1024 / 1024));

        string name = UniqueName();
        _wet = new Sink(this, name);
        try
        {
            if (_includeDry) _dry = new Sink(this, name + "_dry", seed: 0x9E3779B9);
        }
        catch
        {
            _wet.Dispose();
            throw;
        }

        _clock.Restart();
        _active = true;
        _thread = new Thread(WriterLoop) { Name = "録音の書き込み", IsBackground = true, Priority = ThreadPriority.BelowNormal };   // i18n:internal
        _thread.Start();
    }

    /// <summary>既存のファイルを上書きしない名前を決める (加工後・加工前のどちらも、連番も含めて空いているもの)。</summary>
    string UniqueName()
    {
        for (int i = 1; ; i++)
        {
            string candidate = i == 1 ? _baseName : $"{_baseName}-{i}";
            // WAV でも FLAC でも、同じ名前 (連番・加工前を含む) があれば避ける
            bool taken = Directory.EnumerateFiles(_folder, candidate + "*.*")
                .Where(f => f.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".flac", StringComparison.OrdinalIgnoreCase))
                .Select(f => Path.GetFileNameWithoutExtension(f))
                .Any(f => f == candidate || f!.StartsWith(candidate + "_"));
            if (!taken) return candidate;
        }
    }

    // ---- 音声スレッド ----

    /// <summary>加工後の音を渡す。入らなかった (または録音が止まっている) ときは false。</summary>
    public bool PushWet(ReadOnlySpan<float> l, ReadOnlySpan<float> r) => _active && _wetQueue.Push(l, r);

    /// <summary>加工前の音を渡す (加工前を録音しない設定のときは何もしない)。</summary>
    public bool PushDry(ReadOnlySpan<float> l, ReadOnlySpan<float> r) => _active && _dryQueue != null && _dryQueue.Push(l, r);

    // ---- 書き込みスレッド ----

    void WriterLoop()
    {
        var l = new float[4096];
        var r = new float[4096];
        var lastHeader = Stopwatch.StartNew();
        try
        {
            while (true)
            {
                bool any = Drain(_wetQueue, _wet!, l, r);
                if (_dry != null) any |= Drain(_dryQueue!, _dry, l, r);

                if (lastHeader.Elapsed >= HeaderInterval)
                {
                    lastHeader.Restart();
                    _wet!.UpdateHeader();
                    _dry?.UpdateHeader();
                    CheckDisk();
                }

                if (_stopRequested && !any) break;
                if (!any) Thread.Sleep(5);
            }
        }
        catch (Exception ex)
        {
            // 書き込みの失敗に限らず、書き込みスレッドの例外でアプリを落とさない。録音だけを止める
            _error = Loc.F("Rec.Err.Stopped", ex.Message);
        }
        finally
        {
            _active = false;
            try { _wet?.Dispose(); } catch (Exception ex) { _error ??= Loc.F("Rec.Err.Close", ex.Message); }
            try { _dry?.Dispose(); } catch (Exception ex) { _error ??= Loc.F("Rec.Err.Close", ex.Message); }
        }
    }

    bool Drain(RecordQueue queue, Sink sink, float[] l, float[] r)
    {
        bool any = false;
        int n;
        while ((n = queue.Read(l, r)) > 0)
        {
            sink.Write(l.AsSpan(0, n), r.AsSpan(0, n));
            any = true;
        }
        return any;
    }

    void CheckDisk()
    {
        if (_freeSpace() is not long free) return;
        _lowDisk = free < LowDiskBytes;
        if (free < StopDiskBytes)
            throw new IOException(Loc.F("Rec.Err.DiskFull", free / 1024 / 1024));
    }

    // ---- 終了・状態 ----

    /// <summary>録音を終える。溜まっている音を書き切って、ファイルを閉じる。</summary>
    public void Stop()
    {
        _stopRequested = true;
        _active = false;   // これ以上は受け付けない (残りは書き切る)
        _thread?.Join(TimeSpan.FromSeconds(15));
        _thread = null;
    }

    public RecorderStatus Status => new(
        IsRecording: _active,
        Elapsed: _clock.Elapsed,
        WetBytes: _wet?.TotalBytes ?? 0,
        DryBytes: _dry?.TotalBytes ?? 0,
        DroppedMs: Math.Max(_wetQueue.DroppedFrames, _dryQueue?.DroppedFrames ?? 0) * 1000.0 / _sampleRate,
        LowDisk: _lowDisk,
        Error: _error,
        WetPath: _wet?.CurrentPath ?? "",
        DryPath: _dry?.CurrentPath);

    public void Dispose()
    {
        Stop();
    }

    /// <summary>1 つのファイル系列 (連番で分かれる)。書き込みスレッドだけが書く。</summary>
    sealed class Sink : IDisposable
    {
        readonly Recorder _owner;
        readonly string _name;
        readonly uint _seed;
        IAudioFileWriter _writer;
        int _index = 1;
        long _closedBytes;
        volatile string _currentPath;

        public Sink(Recorder owner, string name, uint seed = 0x2545F491)
        {
            _owner = owner;
            _name = name;
            _seed = seed;
            _writer = Open();
            _currentPath = _writer.Path;
        }

        public string CurrentPath => _currentPath;
        public long TotalBytes => _closedBytes + _writer.TotalBytes;

        IAudioFileWriter Open()
        {
            bool flac = _owner._fileFormat == RecordFormat.Flac;
            string ext = flac ? ".flac" : ".wav";
            string file = _index == 1 ? $"{_name}{ext}" : $"{_name}_{_index:D3}{ext}";
            string path = Path.Combine(_owner._folder, file);
            return flac
                ? new FlacWriter(path, _owner._sampleRate, _owner._format, _owner._mode, _owner._flacLevel, _seed + (uint)_index)
                : new WavWriter(path, _owner._sampleRate, _owner._format, _owner._mode, _seed + (uint)_index, _owner._maxDataBytes);
        }

        public void Write(ReadOnlySpan<float> l, ReadOnlySpan<float> r)
        {
            // 上限に達するところで、次のファイルへ切り替える (1 回の書き込みが上限をまたぐときは、入る分だけ書いて続ける)
            while (l.Length > 0)
            {
                int fit = (int)Math.Min(l.Length, _writer.RemainingFrames);
                if (fit == 0)
                {
                    if (_writer.TotalBytes == 0) throw new InvalidOperationException("1 フレームも書けない大きさの上限です");   // i18n:internal
                    _closedBytes += _writer.TotalBytes;
                    _writer.Dispose();
                    _index++;
                    _writer = Open();
                    _currentPath = _writer.Path;
                    continue;
                }
                _writer.Write(l[..fit], r[..fit]);
                l = l[fit..];
                r = r[fit..];
            }
        }

        public void UpdateHeader() => _writer.UpdateHeader();

        public void Dispose() => _writer.Dispose();
    }
}
