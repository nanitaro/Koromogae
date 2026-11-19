namespace VstCore;

public enum PluginBitness { Unknown, X86, X64 }

public static class PeInfo
{
    public static PluginBitness Current => Environment.Is64BitProcess ? PluginBitness.X64 : PluginBitness.X86;

    /// <summary>PE ヘッダの Machine フィールドから DLL のビット数を判定する。VST3 のバンドル(フォルダ)にも対応。</summary>
    public static PluginBitness GetBitness(string path)
    {
        string? binary = PluginPaths.ResolveBinary(path, PluginBitness.X64) ?? PluginPaths.ResolveBinary(path, PluginBitness.X86);
        if (binary == null) return PluginBitness.Unknown;

        using var fs = File.OpenRead(binary);
        using var br = new BinaryReader(fs);
        if (fs.Length < 0x40) return PluginBitness.Unknown;
        fs.Position = 0x3C;
        int pe = br.ReadInt32();
        if (pe <= 0 || pe + 6 > fs.Length) return PluginBitness.Unknown;
        fs.Position = pe;
        if (br.ReadUInt32() != 0x00004550) return PluginBitness.Unknown;
        return br.ReadUInt16() switch
        {
            0x014C => PluginBitness.X86,
            0x8664 => PluginBitness.X64,
            _ => PluginBitness.Unknown,
        };
    }
}

/// <summary>プラグインに一度に渡せるサンプル数などの上限。本体・プラグインホスト・各プラグイン実装で共通。</summary>
public static class PluginLimits
{
    /// <summary>1 回の処理 (Process / ProcessCore) で渡せる最大フレーム数。共有メモリの大きさもこれで決まる。</summary>
    public const int MaxBlock = 4096;
}

public static class PluginPaths
{
    public static bool IsVst3(string path) => path.EndsWith(".vst3", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// ロード対象の実体 DLL を返す。VST3 バンドル(Foo.vst3\Contents\x86_64-win\Foo.vst3)は指定ビット数のものを探す。
    /// 見つからなければ null。
    /// </summary>
    public static string? ResolveBinary(string path, PluginBitness bitness)
    {
        if (File.Exists(path)) return path;
        if (!Directory.Exists(path)) return null;

        string arch = bitness == PluginBitness.X86 ? "x86-win" : "x86_64-win";
        string name = Path.GetFileName(path.TrimEnd('\\', '/'));
        string candidate = Path.Combine(path, "Contents", arch, name);
        return File.Exists(candidate) ? candidate : null;
    }
}

/// <summary>ホストから見たエフェクト。同一プロセス内のプラグインも、別プロセス(プラグインホスト)上のプラグインも同じ形で扱う。</summary>
public interface IVstEffect : IDisposable
{
    string Name { get; }
    string Path { get; }
    PluginBitness Bitness { get; }
    bool HasEditor { get; }
    /// <summary>プラグインホストの異常終了などで処理できなくなった場合 true。</summary>
    bool IsFaulted { get; }

    void Prepare(int sampleRate, int maxBlock);

    /// <summary>ステレオ入力をその場で処理する。frames は <see cref="PluginLimits.MaxBlock"/> 以下。</summary>
    void Process(float[] left, float[] right, int frames);

    void OpenEditor();

    /// <summary>
    /// 開いているエディタを閉じる (開いていなければ何もしない)。プラグインの中の閉じる処理は待たない (メッセージスレッドへ回す)。
    /// ただし <b>別プロセスのホストへは応答を待つ呼び出し</b> (<see cref="RemotePlugin"/>) なので、ホストが応答しないと、呼び出しの期限まで戻らない。
    /// UI スレッドから呼ぶときは、呼び出し側がバックグラウンドへ出すこと。
    /// </summary>
    void CloseEditor();

    byte[]? GetState();
    void SetState(byte[] data);

    /// <summary>エディタを持たないプラグインでも操作できるよう、ホストが汎用 UI を作るためのパラメータ一覧。</summary>
    ParameterInfo[] GetParameters();

    /// <summary>正規化値 (0..1) と、プラグインが表示用に返す文字列 (例: "-6.0 dB")。</summary>
    ParameterValue GetParameterValue(int index);

    void SetParameter(int index, double normalized);

    /// <summary>プラグイン内蔵のプリセット (VST2 のプログラム / VST3 のプログラム切替パラメータ)。無ければ空。</summary>
    ProgramList GetPrograms();

    void SetProgram(int index);

    /// <summary>
    /// プラグインが申告する処理遅延 (サンプル数。先読み式のリミッターなど)。画面に出す実遅延の合算に使う。
    /// 値はロード時と <see cref="Prepare"/> 時に取得してキャッシュする (画面の更新のたびに別プロセスと通信しないため)。
    /// </summary>
    int LatencySamples { get; }
}

public sealed record ProgramList(string[] Names, int Current);

public sealed record ParameterInfo(int Index, string Name, string Unit, int StepCount, double DefaultValue);

public readonly record struct ParameterValue(double Normalized, string Display);

/// <summary>このプロセス内にロードされたプラグイン (VST2 / VST3)。プラグインホストサーバーが共有メモリ上で直接処理するために使う。</summary>
public unsafe interface ILocalPlugin : IVstEffect
{
    /// <summary>入力と出力が同一ポインタでも安全。</summary>
    void ProcessCore(float* inL, float* inR, float* outL, float* outR, int frames);
}

public static class PluginFactory
{
    /// <summary>拡張子から VST2 / VST3 を判別し、このプロセスにロードする。</summary>
    public static ILocalPlugin LoadLocal(string path, int sampleRate)
        => PluginPaths.IsVst3(path) ? Vst3Plugin.Load(path, sampleRate) : Vst2Plugin.Load(path, sampleRate);
}
