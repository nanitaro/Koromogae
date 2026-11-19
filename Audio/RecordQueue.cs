namespace Koromogae.Audio;

/// <summary>
/// 音声スレッド (書き手) から、ファイルへ書くスレッド (読み手) へ音を渡す、ロックなしのリングバッファ。書き手も読み手も 1 スレッド。
/// 音声スレッドで待たない・ディスクに触れないための仕組み。書き込みが追いつかず満杯のときは、新しい音を捨てる (再生は止めない)。
/// 捨てた長さは、あとで空きができたときに無音として入れ直す。ファイルの長さが実時間とずれず、前後の順序も保たれる。
/// このファイルは他に依存しない (単体テストでソースごとリンクしている)。
/// </summary>
public sealed class RecordQueue
{
    /// <summary>約 12 秒 (44.1kHz) ぶん。ディスクが一時的に詰まっても、再生に影響させないための余裕。</summary>
    public const int Frames = 1 << 19;

    const int SilenceChunk = 4096;

    readonly float[] _l = new float[Frames];
    readonly float[] _r = new float[Frames];
    long _writePos;
    long _readPos;
    long _dropped;
    long _gap;   // 捨てたぶんのうち、まだ無音で入れ直していない長さ (書き手のスレッドだけが使う)

    /// <summary>捨てた音の合計 (フレーム)。</summary>
    public long DroppedFrames => Volatile.Read(ref _dropped);

    /// <summary>書き手 (音声スレッド): 音を入れる。入らなければ捨てて false。</summary>
    public bool Push(ReadOnlySpan<float> l, ReadOnlySpan<float> r)
    {
        int n = l.Length;

        // 先に、捨てたぶんを無音で埋め直す (空きがあれば)。順序を保つため、埋め切れないうちは新しい音も入れない
        while (_gap > 0)
        {
            int chunk = (int)Math.Min(_gap, SilenceChunk);
            if (!TryWrite(default, default, chunk)) break;
            _gap -= chunk;
        }

        if (_gap == 0 && TryWrite(l, r, n)) return true;

        _gap += n;
        Interlocked.Add(ref _dropped, n);
        return false;
    }

    /// <summary>全部入るときだけ書く。入力が空 (default) なら、無音を frames ぶん書く。</summary>
    bool TryWrite(ReadOnlySpan<float> l, ReadOnlySpan<float> r, int frames)
    {
        long w = Volatile.Read(ref _writePos);
        long used = w - Volatile.Read(ref _readPos);
        if (used + frames > Frames) return false;

        bool silence = l.IsEmpty;
        for (int i = 0; i < frames; i++)
        {
            int idx = (int)((w + i) & (Frames - 1));
            _l[idx] = silence ? 0f : l[i];
            _r[idx] = silence ? 0f : r[i];
        }
        Volatile.Write(ref _writePos, w + frames);
        return true;
    }

    /// <summary>読み手 (書き込みスレッド): 溜まっている音を、最大 l.Length フレームまで取り出す。取り出したフレーム数を返す。</summary>
    public int Read(Span<float> l, Span<float> r)
    {
        long read = Volatile.Read(ref _readPos);
        long avail = Volatile.Read(ref _writePos) - read;
        int n = (int)Math.Min(avail, Math.Min(l.Length, r.Length));
        for (int i = 0; i < n; i++)
        {
            int idx = (int)((read + i) & (Frames - 1));
            l[i] = _l[idx];
            r[i] = _r[idx];
        }
        Volatile.Write(ref _readPos, read + n);
        return n;
    }

    /// <summary>まだ読まれていないフレーム数。</summary>
    public long Pending => Volatile.Read(ref _writePos) - Volatile.Read(ref _readPos);
}
