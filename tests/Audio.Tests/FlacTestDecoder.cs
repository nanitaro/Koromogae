using System.Security.Cryptography;
using Xunit;

namespace Audio.Tests;

/// <summary>
/// テスト用の FLAC 復号器。書き出し側 (FlacWriter) のコードを一切使わず、RFC 9639 の仕様どおりに読む。
/// ヘッダ・各フレームの CRC-8 / CRC-16・MD5 も検査し、不正があれば例外にする。
/// </summary>
internal sealed class FlacTestDecoder
{
    public int SampleRate, Channels, Bps;
    public long TotalSamples;
    public byte[] Md5 = new byte[16];
    public int MinBlock, MaxBlock, MinFrame, MaxFrame;
    public string? Vendor;
    public int[][] Samples = Array.Empty<int[]>();       // [チャンネル][サンプル]
    public int FrameCount;
    public bool Md5Matches;
    public readonly List<string> SubframeKinds = new();

    byte[] _d = Array.Empty<byte>();
    int _pos;      // バイト位置
    int _bit;      // 現在のバイト内のビット位置 (0 = 先頭)

    public static FlacTestDecoder Decode(string path, bool partialOk = false)
    {
        byte[] data;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))     // 書き込み中のファイルも読めるように
        {
            data = new byte[fs.Length];
            fs.ReadExactly(data);
        }
        var dec = new FlacTestDecoder { _d = data };
        dec.Run(partialOk);
        return dec;
    }

    // ---- ビット読み ----

    uint Bits(int n)
    {
        ulong v = 0;
        while (n > 0)
        {
            if (_pos >= _d.Length) throw new InvalidDataException("データの終わりを越えました");
            int avail = 8 - _bit;
            int take = Math.Min(avail, n);
            int shifted = (_d[_pos] >> (avail - take)) & ((1 << take) - 1);
            v = (v << take) | (uint)shifted;
            _bit += take; n -= take;
            if (_bit == 8) { _bit = 0; _pos++; }
        }
        return (uint)v;
    }

    int Signed(int n)
    {
        uint v = Bits(n);
        if (n == 32) return (int)v;
        return (int)(v << (32 - n)) >> (32 - n);
    }

    void AlignByte() { if (_bit != 0) { _bit = 0; _pos++; } }

    // ---- 全体 ----

    void Run(bool partialOk)
    {
        Assert.Equal("fLaC", System.Text.Encoding.ASCII.GetString(_d, 0, 4));
        _pos = 4;
        bool last = false;
        bool sawStreamInfo = false;
        while (!last)
        {
            last = Bits(1) == 1;
            int type = (int)Bits(7);
            int len = (int)Bits(24);
            int start = _pos;
            if (type == 0)
            {
                Assert.Equal(34, len);
                MinBlock = (int)Bits(16); MaxBlock = (int)Bits(16);
                MinFrame = (int)Bits(24); MaxFrame = (int)Bits(24);
                SampleRate = (int)Bits(20); Channels = (int)Bits(3) + 1; Bps = (int)Bits(5) + 1;
                TotalSamples = ((long)Bits(4) << 32) | Bits(32);
                for (int i = 0; i < 16; i++) Md5[i] = (byte)Bits(8);
                sawStreamInfo = true;
            }
            else if (type == 4)
            {
                uint vlen = (uint)(_d[_pos] | _d[_pos + 1] << 8 | _d[_pos + 2] << 16 | _d[_pos + 3] << 24);
                Vendor = System.Text.Encoding.UTF8.GetString(_d, _pos + 4, (int)vlen);
            }
            _pos = start + len; _bit = 0;
        }
        Assert.True(sawStreamInfo, "STREAMINFO がありません");
        Assert.Equal(2, Channels);

        var chans = new List<int>[] { new(), new() };
        long total = 0;
        while (_pos < _d.Length)
        {
            int frameStart = _pos;
            try
            {
                var (l, r) = ReadFrame(frameStart);
                chans[0].AddRange(l); chans[1].AddRange(r);
                total += l.Length;
                FrameCount++;
            }
            catch (Exception) when (partialOk)
            {
                break;     // 途中で切れたファイル: 読めたところまで
            }
        }
        Samples = new[] { chans[0].ToArray(), chans[1].ToArray() };
        if (!partialOk) Assert.Equal(TotalSamples, total);

        // MD5: 左右交互・リトルエンディアン
        if (Md5.Any(b => b != 0))
        {
            int bytes = Bps / 8;
            var raw = new byte[Samples[0].Length * 2 * bytes];
            for (int i = 0; i < Samples[0].Length; i++)
                for (int c = 0; c < 2; c++)
                    for (int b = 0; b < bytes; b++)
                        raw[(i * 2 + c) * bytes + b] = (byte)(Samples[c][i] >> (8 * b));
            Md5Matches = MD5.HashData(raw).SequenceEqual(Md5);
        }
    }

    // ---- フレーム ----

    (int[] L, int[] R) ReadFrame(int frameStart)
    {
        _bit = 0;
        uint sync = Bits(14);
        if (sync != 0x3FFE) throw new InvalidDataException("同期コードがありません");
        Assert.Equal(0u, Bits(1));            // 予約
        Assert.Equal(0u, Bits(1));            // 固定ブロック長
        int blockCode = (int)Bits(4);
        int rateCode = (int)Bits(4);
        int channelCode = (int)Bits(4);
        int sizeCode = (int)Bits(3);
        Assert.Equal(0u, Bits(1));
        ReadUtf8();                            // フレーム番号
        int blockSize = blockCode switch
        {
            1 => 192, 2 => 576, 3 => 1152, 4 => 2304, 5 => 4608,
            6 => (int)Bits(8) + 1, 7 => (int)Bits(16) + 1,
            >= 8 => 256 << (blockCode - 8),
            _ => throw new InvalidDataException("ブロック長の符号が不正"),
        };
        if (rateCode is 12 or 13 or 14) throw new NotSupportedException("サンプルレートの直接指定はテスト用復号器の対象外");
        int headerLen = _pos - frameStart;
        uint crc8 = Bits(8);
        Assert.Equal(Crc8(_d, frameStart, headerLen), crc8);
        Assert.NotEqual(0, rateCode);

        int bps = sizeCode switch { 1 => 8, 2 => 12, 4 => 16, 5 => 20, 6 => 24, 7 => 32, _ => Bps };
        Assert.Equal(Bps, bps);

        int[] a, b;
        switch (channelCode)
        {
            case 1:
                a = Subframe(blockSize, bps); b = Subframe(blockSize, bps);
                break;
            case 8:    // 左 / 差
                {
                    var left = Subframe(blockSize, bps); var side = Subframe(blockSize, bps + 1);
                    a = left; b = new int[blockSize];
                    for (int i = 0; i < blockSize; i++) b[i] = left[i] - side[i];
                    break;
                }
            case 9:    // 差 / 右
                {
                    var side = Subframe(blockSize, bps + 1); var right = Subframe(blockSize, bps);
                    b = right; a = new int[blockSize];
                    for (int i = 0; i < blockSize; i++) a[i] = side[i] + right[i];
                    break;
                }
            case 10:   // 中央 / 差
                {
                    var mid = Subframe(blockSize, bps); var side = Subframe(blockSize, bps + 1);
                    a = new int[blockSize]; b = new int[blockSize];
                    for (int i = 0; i < blockSize; i++)
                    {
                        long m = ((long)mid[i] << 1) | (uint)(side[i] & 1);
                        a[i] = (int)((m + side[i]) >> 1);
                        b[i] = (int)((m - side[i]) >> 1);
                    }
                    break;
                }
            default: throw new InvalidDataException($"チャンネル割り当て {channelCode}");
        }
        AlignByte();
        int bodyEnd = _pos;
        uint crc16 = Bits(16);
        Assert.Equal(Crc16(_d, frameStart, bodyEnd - frameStart), crc16);
        return (a, b);
    }

    void ReadUtf8()
    {
        uint first = Bits(8);
        if (first < 0x80) return;
        int extra = first >= 0xFE ? 6 : first >= 0xFC ? 5 : first >= 0xF8 ? 4 : first >= 0xF0 ? 3 : first >= 0xE0 ? 2 : 1;
        for (int i = 0; i < extra; i++) Assert.Equal(0x2u, Bits(2));     // 10xxxxxx の先頭 2 ビット
        for (int i = 0; i < extra; i++) Bits(6);
    }

    int[] Subframe(int n, int bps)
    {
        Assert.Equal(0u, Bits(1));
        int type = (int)Bits(6);
        bool wasted = Bits(1) == 1;
        int wastedBits = 0;
        if (wasted) { wastedBits = 1; while (Bits(1) == 0) wastedBits++; }
        int sbps = bps - wastedBits;
        var x = new int[n];

        if (type == 0)
        {
            SubframeKinds.Add("constant");
            int v = Signed(sbps);
            Array.Fill(x, v);
        }
        else if (type == 1)
        {
            SubframeKinds.Add("verbatim");
            for (int i = 0; i < n; i++) x[i] = Signed(sbps);
        }
        else if (type >= 8 && type <= 12)
        {
            int order = type - 8;
            SubframeKinds.Add($"fixed{order}");
            for (int i = 0; i < order; i++) x[i] = Signed(sbps);
            var res = Residual(n, order);
            for (int i = order; i < n; i++)
            {
                long pred = order switch
                {
                    0 => 0,
                    1 => x[i - 1],
                    2 => 2L * x[i - 1] - x[i - 2],
                    3 => 3L * x[i - 1] - 3L * x[i - 2] + x[i - 3],
                    _ => 4L * x[i - 1] - 6L * x[i - 2] + 4L * x[i - 3] - x[i - 4],
                };
                x[i] = (int)(pred + res[i - order]);
            }
        }
        else if (type >= 32)
        {
            int order = type - 32 + 1;
            SubframeKinds.Add($"lpc{order}");
            for (int i = 0; i < order; i++) x[i] = Signed(sbps);
            int precision = (int)Bits(4) + 1;
            int shift = Signed(5);
            var coef = new int[order];
            for (int j = 0; j < order; j++) coef[j] = Signed(precision);
            var res = Residual(n, order);
            for (int i = order; i < n; i++)
            {
                long sum = 0;
                for (int j = 0; j < order; j++) sum += (long)coef[j] * x[i - 1 - j];
                x[i] = (int)((sum >> shift) + res[i - order]);
            }
        }
        else throw new InvalidDataException($"サブフレームの種類 {type}");

        if (wastedBits > 0) for (int i = 0; i < n; i++) x[i] <<= wastedBits;
        return x;
    }

    int[] Residual(int n, int order)
    {
        int method = (int)Bits(2);
        if (method > 1) throw new InvalidDataException("Rice の方式が不正");
        int partOrder = (int)Bits(4);
        int parts = 1 << partOrder;
        var res = new int[n - order];
        int idx = 0;
        for (int p = 0; p < parts; p++)
        {
            int k = (int)Bits(method == 0 ? 4 : 5);
            int count = (n >> partOrder) - (p == 0 ? order : 0);
            if (k == (method == 0 ? 15 : 31))
            {
                int bits = (int)Bits(5);
                for (int i = 0; i < count; i++) res[idx++] = bits == 0 ? 0 : Signed(bits);
                continue;
            }
            for (int i = 0; i < count; i++)
            {
                uint q = 0;
                while (Bits(1) == 0) q++;
                uint u = (q << k) | (k > 0 ? Bits(k) : 0);
                res[idx++] = (int)(u >> 1) ^ -(int)(u & 1);
            }
        }
        return res;
    }

    // ---- CRC ----

    static uint Crc8(byte[] d, int start, int len)
    {
        int crc = 0;
        for (int i = 0; i < len; i++)
        {
            crc ^= d[start + i];
            for (int b = 0; b < 8; b++) crc = (crc & 0x80) != 0 ? ((crc << 1) ^ 0x07) & 0xFF : (crc << 1) & 0xFF;
        }
        return (uint)crc;
    }

    static uint Crc16(byte[] d, int start, int len)
    {
        int crc = 0;
        for (int i = 0; i < len; i++)
        {
            crc ^= d[start + i] << 8;
            for (int b = 0; b < 8; b++) crc = (crc & 0x8000) != 0 ? ((crc << 1) ^ 0x8005) & 0xFFFF : (crc << 1) & 0xFFFF;
        }
        return (uint)crc;
    }
}
