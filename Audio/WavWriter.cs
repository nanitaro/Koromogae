using System.IO;

namespace Koromogae.Audio;

/// <summary>
/// ステレオ float を WAV ファイルへ追記する。形式は 16bit / 24bit 整数 (PCM) と 32bit float (IEEE float)。
/// 整数への丸めとディザは <see cref="Pcm"/> と同じ (四捨五入 + 画面で選んだ方式)。
/// ヘッダは開いたらすぐに書き、<see cref="UpdateHeader"/> で長さを書き直す (落ちても、直前までの音が読めるファイルが残る)。
/// 既存のファイルは上書きしない (<see cref="FileMode.CreateNew"/>)。他のプログラムが読めるよう、読み取りの共有を許す。
/// このファイルは PortAudio にも画面にも依存しない (単体テストでソースごとリンクしている)。
/// </summary>
internal sealed unsafe class WavWriter : IAudioFileWriter
{
    /// <summary>RIFF のサイズ欄が 32bit なので、データ部は 4GB 未満。余裕を見て、これを超える前に次のファイルへ切り替える。</summary>
    public const long DefaultMaxDataBytes = 4_000_000_000L;

    readonly FileStream _stream;
    readonly SampleFormat _format;
    readonly int _sampleRate;
    readonly int _bytesPerFrame;
    readonly long _maxDataBytes;
    readonly int _dataOffset;
    readonly bool _isFloat;
    readonly DitherMode _mode;
    Pcm.DitherState _dither;
    byte[] _buffer = new byte[0];
    bool _disposed;

    public string Path { get; }
    public long TotalBytes => DataBytes;
    /// <summary>これまでに書いたデータ部のバイト数。</summary>
    public long DataBytes { get; private set; }
    public long Frames => DataBytes / _bytesPerFrame;

    public WavWriter(string path, int sampleRate, SampleFormat format, DitherMode mode, uint seed = 0x2545F491,
        long maxDataBytes = DefaultMaxDataBytes)
    {
        Path = path;
        _sampleRate = sampleRate;
        _format = format;
        _mode = mode;
        _maxDataBytes = maxDataBytes;
        _isFloat = format == SampleFormat.Float32;
        _bytesPerFrame = Pcm.BytesPerSample(format) * 2;
        _dither = Pcm.DitherState.Create(seed, sampleRate);
        _dataOffset = _isFloat ? 56 : 44;

        if (format == SampleFormat.Int32) throw new ArgumentException("32bit 整数の WAV には対応しません", nameof(format));   // i18n:internal

        _stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        try
        {
            WriteHeader();
        }
        catch
        {
            _stream.Dispose();
            try { File.Delete(path); } catch { /* 作りかけのファイルが残ってもよい */ }
            throw;
        }
    }

    /// <summary>このフレーム数を足しても、データ部が上限を超えないか。超えるなら、次のファイルへ切り替える。</summary>
    public bool CanAppend(int frames) => DataBytes + (long)frames * _bytesPerFrame <= _maxDataBytes;

    /// <summary>あと何フレーム書けるか (データ部の上限まで)。</summary>
    public long RemainingFrames => Math.Max(0, (_maxDataBytes - DataBytes) / _bytesPerFrame);

    /// <summary>ステレオ float を、ファイルの形式に変換して追記する。</summary>
    public void Write(ReadOnlySpan<float> left, ReadOnlySpan<float> right)
    {
        if (left.Length != right.Length) throw new ArgumentException("左右の長さが違います");   // i18n:internal
        int n = left.Length;
        if (n == 0) return;
        if (!CanAppend(n)) throw new InvalidOperationException("データ部が上限を超えます (CanAppend を確認してください)");   // i18n:internal

        int bytes = n * _bytesPerFrame;
        if (_buffer.Length < bytes) _buffer = new byte[Math.Max(bytes, 16 * 1024)];

        fixed (float* l = left, r = right)
        fixed (byte* dst = _buffer)
            Pcm.WriteStereo(l, r, _format, 2, dst, n, ref _dither, _mode);

        _stream.Write(_buffer, 0, bytes);
        DataBytes += bytes;
    }

    /// <summary>ヘッダの長さの欄を、いまの長さに書き直す (書いた分はディスクへ流す)。</summary>
    public void UpdateHeader()
    {
        _stream.Flush();
        long end = _stream.Position;
        WriteHeader();
        _stream.Position = end;
        _stream.Flush();
    }

    void WriteHeader()
    {
        _stream.Position = 0;
        using var w = new BinaryWriter(_stream, System.Text.Encoding.ASCII, leaveOpen: true);
        uint dataSize = (uint)Math.Min(DataBytes, uint.MaxValue);
        uint riffSize = (uint)(_dataOffset - 8 + DataBytes);
        int blockAlign = _bytesPerFrame;

        w.Write("RIFF"u8);
        w.Write(riffSize);
        w.Write("WAVE"u8);
        w.Write("fmt "u8);
        w.Write(16u);
        w.Write((ushort)(_isFloat ? 3 : 1));          // 1 = PCM、3 = IEEE float
        w.Write((ushort)2);                           // チャンネル数
        w.Write((uint)_sampleRate);
        w.Write((uint)(_sampleRate * blockAlign));    // 1 秒あたりのバイト数
        w.Write((ushort)blockAlign);
        w.Write((ushort)(Pcm.BytesPerSample(_format) * 8));
        if (_isFloat)
        {
            w.Write("fact"u8);                        // PCM 以外では必須のチャンク
            w.Write(4u);
            w.Write((uint)Frames);
        }
        w.Write("data"u8);
        w.Write(dataSize);
        w.Flush();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { UpdateHeader(); }
        finally { _stream.Dispose(); }
    }
}
