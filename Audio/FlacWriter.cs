using System.IO;
using System.Security.Cryptography;

namespace Koromogae.Audio;

/// <summary>
/// ステレオ float を FLAC ファイル (可逆圧縮) へ追記する。16bit / 24bit 整数のみ (FLAC は float を保存できない)。
/// 整数への丸めとディザは <see cref="Pcm"/> と同じ (四捨五入 + 画面で選んだ方式) で、WAV の 24bit / 16bit と同じ値になる。
/// 形式は RFC 9639 の Subset (固定ブロック長 1152 または 4096、線形予測の次数 12 以下)。外部のライブラリは使わない。
/// 圧縮レベル 0〜8 は、公式の flac コマンドの定義に合わせる (-l 次数 / -b ブロック長 / -r Rice 分割の次数 / -M -m ステレオの探索)。
/// ただし窓関数は tukey(0.5) だけで、公式と同じビット列にはならない (復号した音は、元と完全に一致する)。
/// 先頭の情報欄 (STREAMINFO) は開いたらすぐ書き、<see cref="UpdateHeader"/> で総サンプル数を書き直す (落ちても、それまでの音は再生できる)。
/// 終了時に MD5 を書く。
/// このファイルは PortAudio にも画面にも依存しない (単体テストでソースごとリンクしている)。
/// </summary>
internal sealed unsafe class FlacWriter : IAudioFileWriter
{
    public const int MinLevel = 0;
    public const int MaxLevel = 8;
    public const int DefaultLevel = 5;
    public const string Vendor = "Koromogae";

    // 公式の flac コマンドの -0〜-8 の定義 (flac 1.5.0 の --help): ブロック長 / LPC の最大次数 / Rice 分割の最大次数 / ステレオ (0 = L/R のみ、1 = -M 推定で選ぶ、2 = -m 全部試す)
    static readonly (int Block, int Lpc, int Rice, int Stereo)[] Levels =
    {
        (1152, 0, 3, 0),
        (1152, 0, 3, 1),
        (1152, 0, 3, 2),
        (4096, 6, 4, 0),
        (4096, 8, 4, 1),
        (4096, 8, 5, 2),
        (4096, 8, 6, 2),
        (4096, 12, 6, 2),
        (4096, 12, 6, 2),
    };

    readonly FileStream _stream;
    readonly SampleFormat _format;
    readonly int _sampleRate;
    readonly int _rateCode;      // フレームのヘッダに書くサンプルレートの符号 (表にあるものだけ対応)
    readonly int _bps;
    readonly int _bytesPerSample;
    readonly int _blockSize;
    readonly int _maxLpcOrder;
    readonly int _maxRice;
    readonly int _stereoMode;
    readonly DitherMode _ditherMode;
    Pcm.DitherState _dither;
    readonly IncrementalHash _md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);

    readonly int[] _pendL, _pendR;
    int _pending;
    readonly int[][] _signals;           // 0 = L、1 = R、2 = 中央 (L+R)/2、3 = 差 L-R
    readonly Plan[] _plans;
    readonly bool[] _planned = new bool[4];
    readonly Plan _tmp;
    byte[] _pcm = Array.Empty<byte>();
    readonly BitWriter _bits = new();
    readonly double[] _window;
    readonly double[] _windowed;
    readonly double[] _autoc;
    readonly double[][] _lp;
    readonly double[] _lpErr;
    readonly long[] _partSums = new long[1 << 8];
    readonly long[] _partCounts = new long[1 << 8];
    bool _disposed;

    long _frameNumber;
    long _totalSamples;
    long _fileBytes;
    int _minFrame = int.MaxValue, _maxFrame;

    public string Path { get; }
    public int Level { get; }
    public long TotalBytes => _fileBytes;
    public long RemainingFrames => long.MaxValue;
    /// <summary>ファイルに書き終えたフレーム数 (まだブロックに満たず、保留している分は含まない)。</summary>
    public long Frames => _totalSamples;

    public FlacWriter(string path, int sampleRate, SampleFormat format, DitherMode mode, int level = DefaultLevel, uint seed = 0x2545F491)
    {
        if (format != SampleFormat.Int24 && format != SampleFormat.Int16)
            throw new ArgumentException("FLAC に保存できるのは 24bit / 16bit 整数だけです (float は保存できません)", nameof(format));   // i18n:internal
        _rateCode = sampleRate switch { 88200 => 1, 176400 => 2, 192000 => 3, 8000 => 4, 16000 => 5, 22050 => 6, 24000 => 7, 32000 => 8, 44100 => 9, 48000 => 10, 96000 => 11, _ => 0 };
        if (_rateCode == 0) throw new ArgumentException($"サンプルレート {sampleRate} Hz は FLAC の録音に対応していません", nameof(sampleRate));   // i18n:internal
        level = Math.Clamp(level, MinLevel, MaxLevel);

        Path = path;
        Level = level;
        _sampleRate = sampleRate;
        _format = format;
        _bps = format == SampleFormat.Int24 ? 24 : 16;
        _bytesPerSample = _bps / 8;
        _ditherMode = mode;
        _dither = Pcm.DitherState.Create(seed, sampleRate);
        var p = Levels[level];
        _blockSize = p.Block;
        _maxLpcOrder = p.Lpc;
        _maxRice = p.Rice;
        _stereoMode = p.Stereo;

        _pendL = new int[_blockSize];
        _pendR = new int[_blockSize];
        _signals = new[] { _pendL, _pendR, new int[_blockSize], new int[_blockSize] };
        _plans = Enumerable.Range(0, 4).Select(_ => new Plan(_blockSize)).ToArray();
        _tmp = new Plan(_blockSize);
        _window = new double[_blockSize];
        _windowed = new double[_blockSize];
        _autoc = new double[_maxLpcOrder + 1];
        _lp = Enumerable.Range(0, Math.Max(1, _maxLpcOrder)).Select(_ => new double[Math.Max(1, _maxLpcOrder)]).ToArray();
        _lpErr = new double[Math.Max(1, _maxLpcOrder)];

        _stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        try
        {
            WriteMetadata(finalizing: false);
        }
        catch
        {
            _stream.Dispose();
            try { File.Delete(path); } catch { /* 作りかけのファイルが残ってもよい */ }
            throw;
        }
    }

    // ---- 入力 ----

    public void Write(ReadOnlySpan<float> left, ReadOnlySpan<float> right)
    {
        if (left.Length != right.Length) throw new ArgumentException("左右の長さが違います");   // i18n:internal
        int n = left.Length;
        if (n == 0) return;

        // WAV と同じ丸め・ディザで整数にする (整数の列は、MD5 の対象でもある: 左右交互・リトルエンディアン)
        int bytes = n * 2 * _bytesPerSample;
        if (_pcm.Length < bytes) _pcm = new byte[Math.Max(bytes, 16 * 1024)];
        fixed (float* l = left, r = right)
        fixed (byte* dst = _pcm)
            Pcm.WriteStereo(l, r, _format, 2, dst, n, ref _dither, _ditherMode);
        _md5.AppendData(_pcm, 0, bytes);

        for (int i = 0; i < n; i++)
        {
            int o = i * 2 * _bytesPerSample;
            _pendL[_pending] = ReadSample(o);
            _pendR[_pending] = ReadSample(o + _bytesPerSample);
            if (++_pending == _blockSize)
            {
                EncodeFrame(_blockSize);
                _pending = 0;
            }
        }
    }

    int ReadSample(int offset) => _bytesPerSample == 3
        ? ((_pcm[offset] | (_pcm[offset + 1] << 8) | (_pcm[offset + 2] << 16)) << 8) >> 8
        : (short)(_pcm[offset] | (_pcm[offset + 1] << 8));

    // ---- ヘッダ ----

    /// <summary>情報欄 (STREAMINFO) と、作成ソフトの名前 (Vorbis コメント) を書く。先頭に書く (finalizing = false) か、書き直す。</summary>
    void WriteMetadata(bool finalizing)
    {
        var w = new BitWriter();
        w.Write(0x664C6143, 32);                         // "fLaC"
        w.Write(0, 1); w.Write(0, 7); w.Write(34, 24);   // 最後のブロックではない・STREAMINFO・長さ 34

        // 最小/最大ブロック長: 固定ブロック長の流れ。最後だけ短いブロックがあってもよい (最小の対象外)
        int minBlock = _frameNumber == 0 ? 0 : (_frameNumber == 1 ? (int)Math.Min(_totalSamples, _blockSize) : _blockSize);
        int maxBlock = _frameNumber == 0 ? 0 : (_frameNumber == 1 ? (int)Math.Min(_totalSamples, _blockSize) : _blockSize);
        w.Write((uint)minBlock, 16);
        w.Write((uint)maxBlock, 16);
        w.Write(_frameNumber == 0 ? 0u : (uint)_minFrame, 24);
        w.Write(_frameNumber == 0 ? 0u : (uint)_maxFrame, 24);
        w.Write((uint)_sampleRate, 20);
        w.Write(1, 3);                                   // チャンネル数 - 1
        w.Write((uint)(_bps - 1), 5);
        w.Write((uint)(_totalSamples >> 32), 4);         // 総サンプル数 (36 bit)
        w.Write((uint)(_totalSamples & 0xFFFFFFFF), 32);
        byte[] md5 = finalizing ? _md5.GetHashAndReset() : new byte[16];   // 終了するまで 0 (不明)
        foreach (byte b in md5) w.Write(b, 8);

        // Vorbis コメント (最後のブロック): 作成ソフトの名前だけ、タグなし
        byte[] vendor = System.Text.Encoding.UTF8.GetBytes(Vendor);
        w.Write(1, 1); w.Write(4, 7); w.Write((uint)(4 + vendor.Length + 4), 24);
        w.Write(LittleEndian((uint)vendor.Length), 32);
        foreach (byte b in vendor) w.Write(b, 8);
        w.Write(0, 32);                                  // コメントの数 0

        _stream.Position = 0;
        _stream.Write(w.Buffer, 0, w.Length);
        if (_fileBytes == 0) _fileBytes = w.Length;
    }

    static uint LittleEndian(uint v) => ((v & 0xFF) << 24) | ((v & 0xFF00) << 8) | ((v >> 8) & 0xFF00) | (v >> 24);

    public void UpdateHeader()
    {
        _stream.Flush();
        long end = _stream.Position;
        WriteMetadata(finalizing: false);
        _stream.Position = end;
        _stream.Flush();
    }

    // ---- フレーム ----

    sealed class BitWriter
    {
        byte[] _buf = new byte[1 << 16];
        int _len;
        ulong _acc;
        int _accBits;

        public byte[] Buffer => _buf;
        public int Length => _len;
        public int BitCount => _len * 8 + _accBits;

        public void Reset() { _len = 0; _acc = 0; _accBits = 0; }

        public void Write(uint value, int bits)
        {
            if (bits == 0) return;
            _acc = (_acc << bits) | (value & (bits == 32 ? 0xFFFFFFFFu : (1u << bits) - 1));
            _accBits += bits;
            while (_accBits >= 8)
            {
                _accBits -= 8;
                if (_len == _buf.Length) Array.Resize(ref _buf, _buf.Length * 2);
                _buf[_len++] = (byte)(_acc >> _accBits);
            }
            _acc &= (1UL << _accBits) - 1;
        }

        public void WriteSigned(int value, int bits) => Write((uint)value, bits);

        /// <summary>q 個の 0 に続けて 1。</summary>
        public void WriteUnary(uint q)
        {
            while (q >= 32) { Write(0, 32); q -= 32; }
            Write(0, (int)q);
            Write(1, 1);
        }

        public void PadToByte()
        {
            if (_accBits > 0) Write(0, 8 - _accBits);
        }
    }

    enum SubframeType { Constant, Verbatim, Fixed, Lpc }

    sealed class Plan
    {
        public SubframeType Type;
        public int Order;
        public int Bps;
        public int[] Signal = Array.Empty<int>();
        public int N;
        public int Constant;
        public int Precision, Shift;
        public readonly int[] Coef = new int[32];
        public readonly int[] Residual;
        public int PartOrder, Method;
        public readonly int[] Params = new int[1 << 8];
        public long Bits;

        public Plan(int blockSize) => Residual = new int[blockSize];

        public void CopyFrom(Plan o)
        {
            Type = o.Type; Order = o.Order; Bps = o.Bps; Signal = o.Signal; N = o.N; Constant = o.Constant;
            Precision = o.Precision; Shift = o.Shift; PartOrder = o.PartOrder; Method = o.Method; Bits = o.Bits;
            Array.Copy(o.Coef, Coef, Coef.Length);
            Array.Copy(o.Residual, Residual, Math.Max(0, o.N - o.Order));
            Array.Copy(o.Params, Params, 1 << Math.Max(0, o.PartOrder));
        }
    }

    void EncodeFrame(int n)
    {
        Array.Clear(_planned);

        // 中央 (L+R)/2 と差 (L-R) は、ステレオの探索をする水準だけ作る
        if (_stereoMode != 0)
        {
            for (int i = 0; i < n; i++)
            {
                _signals[2][i] = (_pendL[i] + _pendR[i]) >> 1;
                _signals[3][i] = _pendL[i] - _pendR[i];
            }
        }

        // チャンネルの割り当て: 0 = L/R、1 = L/差、2 = 差/R、3 = 中央/差
        int assignment = 0;
        if (_stereoMode == 1)
        {
            long sl = FixedScore(_signals[0], n), sr = FixedScore(_signals[1], n), sm = FixedScore(_signals[2], n), ss = FixedScore(_signals[3], n);
            long best = sl + sr; assignment = 0;
            if (sl + ss < best) { best = sl + ss; assignment = 1; }
            if (ss + sr < best) { best = ss + sr; assignment = 2; }
            if (sm + ss < best) { assignment = 3; }
        }
        else if (_stereoMode == 2)
        {
            for (int c = 0; c < 4; c++) EnsurePlan(c, n);
            long best = _plans[0].Bits + _plans[1].Bits; assignment = 0;
            if (_plans[0].Bits + _plans[3].Bits < best) { best = _plans[0].Bits + _plans[3].Bits; assignment = 1; }
            if (_plans[3].Bits + _plans[1].Bits < best) { best = _plans[3].Bits + _plans[1].Bits; assignment = 2; }
            if (_plans[2].Bits + _plans[3].Bits < best) { assignment = 3; }
        }

        (int a, int b) = assignment switch { 0 => (0, 1), 1 => (0, 3), 2 => (3, 1), _ => (2, 3) };
        EnsurePlan(a, n);
        EnsurePlan(b, n);

        _bits.Reset();
        WriteFrameHeader(n, assignment);
        WriteSubframe(_plans[a]);
        WriteSubframe(_plans[b]);
        _bits.PadToByte();
        uint crc16 = Crc16(_bits.Buffer, _bits.Length);
        _bits.Write(crc16, 16);

        _stream.Write(_bits.Buffer, 0, _bits.Length);
        _fileBytes += _bits.Length;
        _minFrame = Math.Min(_minFrame, _bits.Length);
        _maxFrame = Math.Max(_maxFrame, _bits.Length);
        _totalSamples += n;
        _frameNumber++;
    }

    void EnsurePlan(int channel, int n)
    {
        if (_planned[channel]) return;
        _planned[channel] = true;
        BuildPlan(_plans[channel], _signals[channel], n, channel == 3 ? _bps + 1 : _bps);
    }

    void WriteFrameHeader(int n, int assignment)
    {
        _bits.Write(0xFFF8, 16);                                           // 同期コード + 固定ブロック長
        int blockCode = n switch { 576 => 2, 1152 => 3, 2304 => 4, 4608 => 5, 256 => 8, 512 => 9, 1024 => 10, 2048 => 11, 4096 => 12, 8192 => 13, 16384 => 14, 32768 => 15, _ => n <= 256 ? 6 : 7 };
        _bits.Write((uint)(blockCode << 4 | _rateCode), 8);
        int channelCode = assignment switch { 0 => 1, 1 => 8, 2 => 9, _ => 10 };
        int sizeCode = _bps == 16 ? 4 : 6;
        _bits.Write((uint)(channelCode << 4 | sizeCode << 1), 8);
        WriteUtf8(_frameNumber);
        if (blockCode == 6) _bits.Write((uint)(n - 1), 8);
        else if (blockCode == 7) _bits.Write((uint)(n - 1), 16);
        _bits.Write(Crc8(_bits.Buffer, _bits.Length), 8);
    }

    void WriteUtf8(long v)
    {
        // フレーム番号: UTF-8 と同じ符号化 (最大 36 bit)
        if (v < 0x80) { _bits.Write((uint)v, 8); return; }
        int extra = v < 0x800 ? 1 : v < 0x10000 ? 2 : v < 0x200000 ? 3 : v < 0x4000000 ? 4 : v < 0x80000000L ? 5 : 6;
        uint lead = (uint)((0xFF00 >> (extra + 1)) & 0xFF) | (uint)(v >> (6 * extra));
        _bits.Write(lead, 8);
        for (int i = extra - 1; i >= 0; i--) _bits.Write((uint)(0x80 | ((v >> (6 * i)) & 0x3F)), 8);
    }

    // ---- 予測 ----

    static long FixedScore(int[] x, int n)
    {
        // 固定予測 (次数 0〜4) の残差の絶対値の和の最小値。ステレオの組み合わせを、速く見積もるために使う
        long best = long.MaxValue;
        for (int order = 0; order <= 4 && order < n; order++)
        {
            long sum = 0;
            for (int i = order; i < n; i++) sum += Math.Abs(FixedResidual(x, i, order));
            if (sum < best) best = sum;
        }
        return best;
    }

    static long FixedResidual(int[] x, int i, int order) => order switch
    {
        0 => x[i],
        1 => (long)x[i] - x[i - 1],
        2 => (long)x[i] - 2L * x[i - 1] + x[i - 2],
        3 => (long)x[i] - 3L * x[i - 1] + 3L * x[i - 2] - x[i - 3],
        _ => (long)x[i] - 4L * x[i - 1] + 6L * x[i - 2] - 4L * x[i - 3] + x[i - 4],
    };

    void BuildPlan(Plan best, int[] x, int n, int bps)
    {
        best.Signal = x; best.N = n; best.Bps = bps; best.Order = 0;

        bool constant = true;
        for (int i = 1; i < n; i++) if (x[i] != x[0]) { constant = false; break; }
        if (constant)
        {
            best.Type = SubframeType.Constant; best.Constant = x[0]; best.Bits = 8 + bps;
            return;
        }

        best.Type = SubframeType.Verbatim;
        best.Bits = 8 + (long)n * bps;

        // 固定予測: 残差の絶対値の和が最小の次数
        int bestFixed = -1;
        long bestSum = long.MaxValue;
        for (int order = 0; order <= 4 && order < n; order++)
        {
            long sum = 0;
            for (int i = order; i < n; i++) sum += Math.Abs(FixedResidual(x, i, order));
            if (sum < bestSum) { bestSum = sum; bestFixed = order; }
        }
        if (bestFixed >= 0)
        {
            _tmp.Signal = x; _tmp.N = n; _tmp.Bps = bps;
            if (TryFixed(_tmp, x, n, bps, bestFixed) && _tmp.Bits < best.Bits) best.CopyFrom(_tmp);
        }

        // 線形予測
        if (_maxLpcOrder > 0 && n > 32) TryLpc(best, x, n, bps);
    }

    bool TryFixed(Plan p, int[] x, int n, int bps, int order)
    {
        p.Type = SubframeType.Fixed; p.Order = order;
        for (int i = order; i < n; i++)
        {
            long r = FixedResidual(x, i, order);
            if (r > (1 << 30) || r < -(1 << 30)) return false;
            p.Residual[i - order] = (int)r;
        }
        p.Bits = 8 + (long)order * bps + ResidualBits(p, n, order);
        return true;
    }

    void TryLpc(Plan best, int[] x, int n, int bps)
    {
        int maxOrder = Math.Min(_maxLpcOrder, n - 1);
        // 窓をかけて自己相関を求める
        MakeWindow(n);
        for (int i = 0; i < n; i++) _windowed[i] = x[i] * _window[i];
        for (int lag = 0; lag <= maxOrder; lag++)
        {
            double sum = 0;
            for (int i = lag; i < n; i++) sum += _windowed[i] * _windowed[i - lag];
            _autoc[lag] = sum;
        }
        if (_autoc[0] <= 0) return;
        for (int lag = 0; lag <= maxOrder; lag++) _autoc[lag] *= 1.0 - 1e-9 * lag * lag;   // 数値を安定させる

        // Levinson-Durbin 法で、次数ごとの係数と誤差を求める
        var lpc = new double[maxOrder];
        double err = _autoc[0];
        int usable = 0;
        for (int i = 0; i < maxOrder; i++)
        {
            if (err <= 1e-12 * _autoc[0]) break;      // これ以上は予測が良くならない (割り算が不安定になる)
            double r = -_autoc[i + 1];
            for (int j = 0; j < i; j++) r -= lpc[j] * _autoc[i - j];
            r /= err;
            lpc[i] = r;
            int j2 = 0;
            for (; j2 < (i >> 1); j2++)
            {
                double tmp = lpc[j2];
                lpc[j2] += r * lpc[i - 1 - j2];
                lpc[i - 1 - j2] += r * tmp;
            }
            if ((i & 1) != 0) lpc[j2] += lpc[j2] * r;
            err *= 1.0 - r * r;
            for (int j = 0; j <= i; j++) _lp[i][j] = -lpc[j];
            _lpErr[i] = err;
            usable = i + 1;
            if (err <= 0) break;
        }
        if (usable == 0) return;

        int precision = bps <= 16 ? 12 : 14;
        if (Level >= 6)
        {
            // 全部の次数を、実際に符号化して比べる
            for (int order = 1; order <= usable; order++) TryLpcOrder(best, x, n, bps, order, precision);
        }
        else
        {
            // 見積もりの最も小さい次数だけを符号化する
            int bestOrder = 1;
            double bestBits = double.MaxValue;
            for (int order = 1; order <= usable; order++)
            {
                double perSample = 0.5 * Math.Log2(Math.Max(_lpErr[order - 1], 1e-9) / (n - order));
                double bits = Math.Max(0, perSample) * (n - order) + order * (bps + precision);
                if (bits < bestBits) { bestBits = bits; bestOrder = order; }
            }
            TryLpcOrder(best, x, n, bps, bestOrder, precision);
        }
    }

    void TryLpcOrder(Plan best, int[] x, int n, int bps, int order, int precision)
    {
        var t = _tmp;
        t.Signal = x; t.N = n; t.Bps = bps; t.Type = SubframeType.Lpc; t.Order = order; t.Precision = precision;

        double cmax = 0;
        for (int j = 0; j < order; j++) cmax = Math.Max(cmax, Math.Abs(_lp[order - 1][j]));
        if (cmax <= 0) return;
        int log2cmax = (int)Math.Floor(Math.Log2(cmax)) + 1;
        int shift = Math.Clamp(precision - 1 - log2cmax, 0, 15);
        int qmax = (1 << (precision - 1)) - 1, qmin = -qmax - 1;
        double error = 0;
        for (int j = 0; j < order; j++)
        {
            error += _lp[order - 1][j] * (1 << shift);
            int q = (int)Math.Round(error, MidpointRounding.AwayFromZero);
            q = Math.Clamp(q, qmin, qmax);
            t.Coef[j] = q;
            error -= q;
        }
        t.Shift = shift;

        for (int i = order; i < n; i++)
        {
            long sum = 0;
            for (int j = 0; j < order; j++) sum += (long)t.Coef[j] * x[i - 1 - j];
            long r = x[i] - (sum >> shift);
            if (r > (1 << 30) || r < -(1 << 30)) return;
            t.Residual[i - order] = (int)r;
        }
        t.Bits = 8 + (long)order * bps + 4 + 5 + (long)order * precision + ResidualBits(t, n, order);
        if (t.Bits < best.Bits) best.CopyFrom(t);
    }

    void MakeWindow(int n)
    {
        // tukey(0.5): 両端の 1/4 ずつを余弦でなだらかにし、中央は 1
        int np = (int)(0.5 / 2.0 * n) - 1;
        for (int i = 0; i < n; i++) _window[i] = 1.0;
        if (np <= 0) return;
        for (int i = 0; i <= np; i++)
        {
            double v = 0.5 - 0.5 * Math.Cos(Math.PI * (i + 1) / (np + 1));
            _window[i] = v;
            _window[n - 1 - i] = v;
        }
    }

    // ---- 残差の符号化 (Rice) ----

    static uint ZigZag(int v) => (uint)((v << 1) ^ (v >> 31));

    /// <summary>残差を Rice 符号にするときの、最小のビット数を決める (分割の次数・各区画のパラメーター)。結果は plan に入れる。</summary>
    long ResidualBits(Plan p, int n, int order)
    {
        int maxP = _maxRice;
        while (maxP > 0 && (n % (1 << maxP) != 0 || (n >> maxP) <= order)) maxP--;

        // いちばん細かい分割の、区画ごとの合計
        int parts = 1 << maxP, size = n >> maxP;
        int idx = 0;
        for (int j = 0; j < parts; j++)
        {
            int count = j == 0 ? size - order : size;
            long sum = 0;
            for (int c = 0; c < count; c++) sum += ZigZag(p.Residual[idx++]);
            _partSums[j] = sum;
            _partCounts[j] = count;
        }

        long bestBits = long.MaxValue;
        int bestOrder = 0;
        var sums = new long[parts];
        var counts = new long[parts];
        Array.Copy(_partSums, sums, parts);
        Array.Copy(_partCounts, counts, parts);
        for (int po = maxP; po >= 0; po--)
        {
            int np = 1 << po;
            long total = 0;
            int maxK = 0;
            for (int j = 0; j < np; j++)
            {
                int k = PickK(sums[j], counts[j], out long cost);
                total += cost;
                if (k > maxK) maxK = k;
            }
            total += (maxK > 14 ? 5 : 4) * (long)np;
            if (total < bestBits) { bestBits = total; bestOrder = po; }
            // ひとつ粗い分割へ
            if (po > 0)
            {
                for (int j = 0; j < np / 2; j++)
                {
                    sums[j] = sums[2 * j] + sums[2 * j + 1];
                    counts[j] = counts[2 * j] + counts[2 * j + 1];
                }
            }
        }

        // 選んだ分割の次数で、パラメーターを確定する
        int parts2 = 1 << bestOrder, size2 = n >> bestOrder;
        idx = 0;
        int maxK2 = 0;
        for (int j = 0; j < parts2; j++)
        {
            int count = j == 0 ? size2 - order : size2;
            long sum = 0;
            for (int c = 0; c < count; c++) sum += ZigZag(p.Residual[idx++]);
            p.Params[j] = PickK(sum, count, out _);
            if (p.Params[j] > maxK2) maxK2 = p.Params[j];
        }
        p.PartOrder = bestOrder;
        p.Method = maxK2 > 14 ? 1 : 0;
        return 2 + 4 + bestBits;
    }

    /// <summary>区画の Rice パラメーターを決める。見積もりのビット数 (区画ごとのパラメーターの分は含まない) も返す。</summary>
    static int PickK(long sum, long count, out long cost)
    {
        if (count == 0) { cost = 0; return 0; }
        int k = 0;
        while (k < 30 && (count << (k + 1)) <= sum) k++;
        long c = count * (k + 1) + (sum >> k);
        if (k > 0)
        {
            long c2 = count * k + (sum >> (k - 1));
            if (c2 < c) { c = c2; k--; }
        }
        cost = c;
        return k;
    }

    // ---- サブフレームの書き出し ----

    void WriteSubframe(Plan p)
    {
        _bits.Write(0, 1);                       // 先頭の 0
        switch (p.Type)
        {
            case SubframeType.Constant:
                _bits.Write(0, 6); _bits.Write(0, 1);
                _bits.WriteSigned(p.Constant, p.Bps);
                break;
            case SubframeType.Verbatim:
                _bits.Write(1, 6); _bits.Write(0, 1);
                for (int i = 0; i < p.N; i++) _bits.WriteSigned(p.Signal[i], p.Bps);
                break;
            case SubframeType.Fixed:
                _bits.Write((uint)(8 + p.Order), 6); _bits.Write(0, 1);
                for (int i = 0; i < p.Order; i++) _bits.WriteSigned(p.Signal[i], p.Bps);
                WriteResidual(p);
                break;
            default:
                _bits.Write((uint)(32 + p.Order - 1), 6); _bits.Write(0, 1);
                for (int i = 0; i < p.Order; i++) _bits.WriteSigned(p.Signal[i], p.Bps);
                _bits.Write((uint)(p.Precision - 1), 4);
                _bits.WriteSigned(p.Shift, 5);
                for (int j = 0; j < p.Order; j++) _bits.WriteSigned(p.Coef[j], p.Precision);
                WriteResidual(p);
                break;
        }
    }

    void WriteResidual(Plan p)
    {
        _bits.Write((uint)p.Method, 2);
        _bits.Write((uint)p.PartOrder, 4);
        int parts = 1 << p.PartOrder, size = p.N >> p.PartOrder;
        int paramBits = p.Method == 0 ? 4 : 5;
        int idx = 0;
        for (int j = 0; j < parts; j++)
        {
            int k = p.Params[j];
            _bits.Write((uint)k, paramBits);
            int count = j == 0 ? size - p.Order : size;
            for (int c = 0; c < count; c++)
            {
                uint u = ZigZag(p.Residual[idx++]);
                _bits.WriteUnary(u >> k);
                if (k > 0) _bits.Write(u & ((1u << k) - 1), k);
            }
        }
    }

    // ---- CRC ----

    static readonly byte[] Crc8Table = MakeCrc8();
    static readonly ushort[] Crc16Table = MakeCrc16();

    static byte[] MakeCrc8()
    {
        var t = new byte[256];
        for (int i = 0; i < 256; i++)
        {
            int c = i;
            for (int b = 0; b < 8; b++) c = (c & 0x80) != 0 ? (c << 1) ^ 0x07 : c << 1;
            t[i] = (byte)c;
        }
        return t;
    }

    static ushort[] MakeCrc16()
    {
        var t = new ushort[256];
        for (int i = 0; i < 256; i++)
        {
            int c = i << 8;
            for (int b = 0; b < 8; b++) c = (c & 0x8000) != 0 ? (c << 1) ^ 0x8005 : c << 1;
            t[i] = (ushort)c;
        }
        return t;
    }

    static uint Crc8(byte[] data, int length)
    {
        int crc = 0;
        for (int i = 0; i < length; i++) crc = Crc8Table[(crc ^ data[i]) & 0xFF];
        return (uint)crc;
    }

    static uint Crc16(byte[] data, int length)
    {
        int crc = 0;
        for (int i = 0; i < length; i++) crc = ((crc << 8) ^ Crc16Table[((crc >> 8) ^ data[i]) & 0xFF]) & 0xFFFF;
        return (uint)crc;
    }

    // ---- 終了 ----

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            if (_pending > 0)
            {
                EncodeFrame(_pending);        // ブロックに満たない最後の分
                _pending = 0;
            }
            _stream.Flush();
            WriteMetadata(finalizing: true);   // 総サンプル数と MD5 を確定する
            _stream.Flush();
        }
        finally
        {
            _stream.Dispose();
            _md5.Dispose();
        }
    }
}
