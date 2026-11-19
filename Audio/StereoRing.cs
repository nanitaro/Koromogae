namespace Koromogae.Audio;

/// <summary>
/// 入力コールバック (書き手) と出力コールバック (読み手) をつなぐ、ステレオ float のリングバッファ。
/// 書き手と読み手はそれぞれ 1 スレッドで、ロックはしない (オーディオスレッドで待たないため)。
/// 余裕 (読み出しを始める量) と、溜まりすぎたとき古い音を捨てる閾値は、ここで決める:
/// <code>
/// 余裕     cushion = n + 直前の入力コールバックのフレーム数 + 上乗せ
/// 閾値     high    = 3*(n + 直前の入力コールバックのフレーム数) + rate/50 + 上乗せ
/// </code>
/// これは滑らかなコールバックを前提にした値で、バーストや揺れには足りない (既知の問題)。上乗せは利用者が手動で選ぶ。
/// このファイルは PortAudio にも画面にも依存しない (単体テストでソースごとリンクしている。仮想時間の模擬で方針を検証できる)。
/// </summary>
public sealed class StereoRing
{
    public const int Frames = 1 << 16;

    readonly float[] _l = new float[Frames];
    readonly float[] _r = new float[Frames];
    readonly int _sampleRate;
    readonly int _extraFrames;

    long _writePos;
    long _readPos;
    bool _primed;
    volatile int _inputBlock;

    // ---- 診断 (読み手のスレッドだけが書く) ----
    long _underruns, _silentChunks, _trims, _trimmedFrames, _minSlack = long.MaxValue;
    double _avgFrames;

    /// <param name="sampleRate">閾値の計算 (rate/50 = 20ms) に使う。</param>
    /// <param name="extraFrames">余裕と閾値に上乗せするフレーム数 (0 = 自動)。</param>
    public StereoRing(int sampleRate, int extraFrames)
    {
        _sampleRate = sampleRate;
        _extraFrames = Math.Max(0, extraFrames);
    }

    /// <summary>直前の入力コールバックが届けたフレーム数 (全体)。書き手が、書き始める前に知らせる。</summary>
    public int InputBlock
    {
        get => _inputBlock;
        set => _inputBlock = value;
    }

    /// <summary>書き込み: リングに入れる。溜まりすぎて消費側が止まっている場合は、新しい入力を捨てる (古い音は上書きしない)。</summary>
    public void Write(ReadOnlySpan<float> l, ReadOnlySpan<float> r)
    {
        int n = l.Length;
        long w = Volatile.Read(ref _writePos);
        long used = w - Volatile.Read(ref _readPos);
        if (used + n > Frames) n = (int)Math.Max(0, Frames - used);

        for (int i = 0; i < n; i++)
        {
            int idx = (int)((w + i) & (Frames - 1));
            _l[idx] = l[i];
            _r[idx] = r[i];
        }
        Volatile.Write(ref _writePos, w + n);
    }

    /// <summary>
    /// 読み出し: <paramref name="l"/>.Length フレームを取り出す。
    /// デバイス間のクロック差で溜まりすぎたら古いデータを捨てて遅延を抑え、足りなければ再プライミング (余裕が溜まるまで無音) する。
    /// 読み出せなかった (無音にすべき) ときは false。
    /// </summary>
    public bool Read(Span<float> l, Span<float> r)
    {
        int n = l.Length;
        long read = Volatile.Read(ref _readPos);
        long avail = Volatile.Read(ref _writePos) - read;
        int baseCushion = n + _inputBlock;
        int cushion = baseCushion + _extraFrames;

        long high = 3L * baseCushion + _sampleRate / 50 + _extraFrames;
        if (avail > high)
        {
            _trims++;
            _trimmedFrames += avail - cushion;
            read += avail - cushion;
            avail = cushion;
        }

        _avgFrames += (avail - _avgFrames) * 0.02;

        if (!_primed)
        {
            if (avail < cushion)
            {
                Volatile.Write(ref _readPos, read);
                _silentChunks++;
                return false;
            }
            _primed = true;
        }
        else if (avail < n)
        {
            _underruns++;
            _primed = false;
            _silentChunks++;
            Volatile.Write(ref _readPos, read);
            return false;
        }

        for (int i = 0; i < n; i++)
        {
            int idx = (int)((read + i) & (Frames - 1));
            l[i] = _l[idx];
            r[i] = _r[idx];
        }
        if (avail - n < _minSlack) _minSlack = avail - n;
        Volatile.Write(ref _readPos, read + n);
        return true;
    }

    /// <summary>再生中にリングが空になり、無音にして再充填待ちに戻った回数 (= 音飛び)。</summary>
    public long Underruns => _underruns;
    /// <summary>無音で出力した回数 (開始直後の充填待ちを含む)。</summary>
    public long SilentChunks => _silentChunks;
    /// <summary>溜まりすぎて古い音をまとめて捨てた回数と、その合計フレーム数。</summary>
    public long Trims => _trims;
    public long TrimmedFrames => _trimmedFrames;
    /// <summary>再生中に、読み出した後に残っていたフレーム数の最小値 (0 に近いほど余裕がない)。まだ読み出していなければ -1。</summary>
    public long MinSlackFrames => _minSlack == long.MaxValue ? -1 : _minSlack;
    /// <summary>読み出し時の溜まり具合の移動平均 (フレーム、約 0.5 秒)。実遅延の「内部バッファ」。</summary>
    public double AverageFrames => _avgFrames;
    /// <summary>入力から受け取った音の累計 (フレーム)。</summary>
    public long WrittenFrames => Volatile.Read(ref _writePos);
}
