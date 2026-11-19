using System.Text;
using Xunit;

namespace Audio.Tests;

/// <summary>テスト用に WAV ファイルを読む (書き出し側のコードを使わず、仕様どおりにヘッダを解析する)。</summary>
internal sealed record WavInfo(
    ushort FormatTag, ushort Channels, uint SampleRate, uint ByteRate, ushort BlockAlign, ushort Bits,
    uint RiffSize, uint DataSize, uint? FactFrames, byte[] Data, long FileLength)
{
    public int Frames => Data.Length / BlockAlign;

    public static WavInfo Read(string path)
    {
        // 書き込み中のファイルも読めるよう、共有を許す
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var r = new BinaryReader(fs, Encoding.ASCII);
        Assert.Equal("RIFF", new string(r.ReadChars(4)));
        uint riffSize = r.ReadUInt32();
        Assert.Equal("WAVE", new string(r.ReadChars(4)));

        ushort tag = 0, ch = 0, align = 0, bits = 0;
        uint rate = 0, byteRate = 0, dataSize = 0;
        uint? fact = null;
        byte[] data = Array.Empty<byte>();
        while (fs.Position + 8 <= fs.Length)
        {
            string id = new string(r.ReadChars(4));
            uint size = r.ReadUInt32();
            if (id == "fmt ")
            {
                tag = r.ReadUInt16(); ch = r.ReadUInt16(); rate = r.ReadUInt32();
                byteRate = r.ReadUInt32(); align = r.ReadUInt16(); bits = r.ReadUInt16();
                fs.Position += size - 16;
            }
            else if (id == "fact")
            {
                fact = r.ReadUInt32();
                fs.Position += size - 4;
            }
            else if (id == "data")
            {
                dataSize = size;
                // 書き込み中のヘッダは長さが実際より小さいことがある。ヘッダの値までを読む
                data = r.ReadBytes((int)Math.Min(size, (uint)(fs.Length - fs.Position)));
                break;
            }
            else
            {
                fs.Position += size;
            }
        }
        return new WavInfo(tag, ch, rate, byteRate, align, bits, riffSize, dataSize, fact, data, fs.Length);
    }

    /// <summary>24bit PCM のデータ部を整数 (符号つき) の列にする。左右が交互。</summary>
    public int[] AsInt24()
    {
        var result = new int[Data.Length / 3];
        for (int i = 0; i < result.Length; i++)
            result[i] = ((Data[i * 3] | (Data[i * 3 + 1] << 8) | (Data[i * 3 + 2] << 16)) << 8) >> 8;
        return result;
    }

    public short[] AsInt16()
    {
        var result = new short[Data.Length / 2];
        Buffer.BlockCopy(Data, 0, result, 0, Data.Length);
        return result;
    }

    public float[] AsFloat()
    {
        var result = new float[Data.Length / 4];
        Buffer.BlockCopy(Data, 0, result, 0, Data.Length);
        return result;
    }
}

/// <summary>テストごとの一時フォルダ。破棄すると消える。</summary>
internal sealed class TempFolder : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "KoromogaeTest_" + Guid.NewGuid().ToString("N")[..8]);

    public TempFolder() => Directory.CreateDirectory(Path);

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch { /* 残っても害はない */ }
    }
}
