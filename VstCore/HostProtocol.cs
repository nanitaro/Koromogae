namespace VstCore;

/// <summary>
/// ホスト(64bit)とプラグインホスト(32bit)間のプロトコル定義。
/// 制御コマンドは名前付きパイプ、オーディオブロックは共有メモリ + 名前付きイベントで受け渡す。
/// </summary>
internal static class HostProtocol
{
    public const int HeaderSize = 64;
    public const long MemorySize = HeaderSize + 4L * PluginLimits.MaxBlock * sizeof(float);

    // 共有メモリ内レイアウト: [header 64B][inL][inR][outL][outR]、header 先頭 4B がフレーム数 (-1 で終了)
    public const int InLOffset = HeaderSize;
    public const int InROffset = InLOffset + PluginLimits.MaxBlock * sizeof(float);
    public const int OutLOffset = InROffset + PluginLimits.MaxBlock * sizeof(float);
    public const int OutROffset = OutLOffset + PluginLimits.MaxBlock * sizeof(float);

    public static string MemoryName(string id) => $"VstHost_{id}_mem";
    public static string RequestName(string id) => $"VstHost_{id}_req";
    public static string AckName(string id) => $"VstHost_{id}_ack";
    public static string PipeName(string id) => $"VstHost_{id}_pipe";

    public const int CmdLoad = 1;
    public const int CmdPrepare = 2;
    public const int CmdOpenEditor = 3;
    public const int CmdGetState = 4;
    public const int CmdSetState = 5;
    public const int CmdExit = 6;
    public const int CmdGetParameters = 7;
    public const int CmdGetParameterValue = 8;
    public const int CmdSetParameter = 9;
    public const int CmdGetPrograms = 10;
    public const int CmdSetProgram = 11;
    public const int CmdGetLatency = 12;
    public const int CmdCloseEditor = 13;

    // ---- 受信データの上限 (プロセス境界を越えた値は信用せず、範囲外なら接続を打ち切る) ----

    /// <summary>プラグイン状態データの最大バイト数。実測の最大は約 630KB (SOUND Canvas VA)。サンプラー系の余裕を見て 64MB。</summary>
    public const int MaxStateBytes = 64 * 1024 * 1024;

    /// <summary>パラメータ数・プログラム数の最大。実測の最大は 2,082 個 (FluidSynthPlugin)。</summary>
    public const int MaxItemCount = 65536;

    /// <summary>文字列 1 つの最大バイト数 (UTF-8)。パス・プラグイン名・パラメータ名などに十分な長さ。</summary>
    public const int MaxStringBytes = 64 * 1024;

    /// <summary>プラグインが申告する遅延の最大 (サンプル数)。192kHz で約 20 秒。</summary>
    public const int MaxLatencySamples = 4_000_000;

    /// <summary>パラメータのステップ数の最大 (これを超える申告は、この値に丸める)。実測の最大は数千。</summary>
    public const int MaxStepCount = 1_000_000;

    /// <summary>
    /// 1 回の応答 (パラメータ一覧・プログラム一覧) で読む文字列の合計の上限 (バイト)。個数 × 文字列の上限だけでは、理論上は十数 GiB になるため。
    /// 実測の最大 (2,082 個・短い名前) は数百 KB。
    /// </summary>
    public const int MaxResponseBytes = 16 * 1024 * 1024;

    /// <summary>応答に添える、文字列 1 つぶんの管理用の見積もり (長さの表現・オブジェクトの重さ)。</summary>
    public const int PerStringOverhead = 16;

    public const int MinSampleRate = 8000;
    public const int MaxSampleRate = 768000;

    /// <summary>0 以上 <paramref name="max"/> 以下の個数/長さを読む。範囲外なら <see cref="HostProtocolException"/>。</summary>
    public static int ReadBounded(BinaryReader reader, int max, string what)
        => Check(reader.ReadInt32(), 0, max, what);

    public static int Check(int value, int min, int max, string what)
        => value >= min && value <= max
            ? value
            : throw new HostProtocolException($"{what} の値が範囲外です: {value} (許容 {min}..{max})");

    /// <summary>長さを検証してから読む。ストリームが途中で終わった場合は <see cref="EndOfStreamException"/>。</summary>
    public static byte[] ReadBlob(BinaryReader reader, int length)
    {
        if (length == 0) return Array.Empty<byte>();
        byte[] data = reader.ReadBytes(length);
        if (data.Length != length) throw new EndOfStreamException();
        return data;
    }

    /// <summary><see cref="BinaryWriter.Write(string)"/> と同じ形式 (7bit 長 + UTF-8) の文字列を、長さを検証して読む。</summary>
    /// <param name="budget">1 回の応答で読む合計の上限。渡すと、この文字列の分を数えて、超えたら <see cref="HostProtocolException"/> (読む前に判断するので、メモリを確保しない)。</param>
    public static string ReadString(BinaryReader reader, string what, ResponseBudget? budget = null)
    {
        int length = Check(reader.Read7BitEncodedInt(), 0, MaxStringBytes, what + " の文字列長");
        budget?.Take(length + PerStringOverhead, what);
        return System.Text.Encoding.UTF8.GetString(ReadBlob(reader, length));
    }

    // ---- 数値の検証 (パラメータの値は、有限で 0〜1。ホストは、送る前に SanitizeUnit で必ずこの範囲にするので、範囲外は異常なホスト) ----

    /// <summary>有限で 0〜1 の値だけを受け入れる。それ以外は <see cref="HostProtocolException"/>。</summary>
    public static double CheckUnit(double value, string what)
        => double.IsFinite(value) && value >= 0.0 && value <= 1.0
            ? value
            : throw new HostProtocolException($"{what} が 0〜1 の有限の数ではありません: {value}");

    /// <summary>
    /// プラグインが返した 0〜1 の値を、送る前に整える: 有限でなければ 0、範囲外は 0〜1 に丸める。
    /// (実プラグインが 1.0000001 のような値を返すことがあり、厳密に拒否すると、動いているプラグインを止めてしまうため。受け取る側は CheckUnit で厳密に検査する)
    /// </summary>
    public static double SanitizeUnit(double value)
        => double.IsFinite(value) ? Math.Clamp(value, 0.0, 1.0) : 0.0;

    /// <summary>ステップ数を、送る前に 0〜<see cref="MaxStepCount"/> に整える。</summary>
    public static int SanitizeStepCount(int value) => Math.Clamp(value, 0, MaxStepCount);

    /// <summary>文字列を、受け取る側の上限 (<see cref="MaxStringBytes"/> バイト) に必ず収まる長さに切り詰める (UTF-8 は 1 文字が最大 3 バイト。サロゲートペアは 2 文字で 4 バイト)。</summary>
    public static string Clip(string? text)
    {
        text ??= "";
        int max = MaxStringBytes / 3;
        return text.Length <= max ? text : text[..max];
    }

    /// <summary>文字列を書いたときのバイト数の見積もり (<see cref="ResponseBudget"/> と同じ数え方)。</summary>
    public static int Cost(string text) => System.Text.Encoding.UTF8.GetByteCount(text) + PerStringOverhead;
}

/// <summary>1 回の応答で読む (または送る) 文字列の合計の予算。超えたら <see cref="HostProtocolException"/>。</summary>
internal sealed class ResponseBudget
{
    long _remaining;

    public ResponseBudget(long limit = HostProtocol.MaxResponseBytes) => _remaining = limit;

    public long Remaining => _remaining;

    /// <summary><paramref name="bytes"/> を使う。予算を超えるなら例外 (何も確保する前に呼ぶこと)。</summary>
    public void Take(long bytes, string what)
    {
        if (bytes > _remaining) throw new HostProtocolException($"{what} の応答の合計が上限 ({HostProtocol.MaxResponseBytes} バイト) を超えました");
        _remaining -= bytes;
    }

    /// <summary>送る側: 予算に収まるなら使って true、収まらなければ false (何も使わない)。</summary>
    public bool TryTake(long bytes)
    {
        if (bytes > _remaining) return false;
        _remaining -= bytes;
        return true;
    }
}

/// <summary>相手が送ってきた値が不正 (範囲外・未知のコマンドなど) だった。ストリームの同期が取れなくなるため、接続を打ち切る。</summary>
public sealed class HostProtocolException : Exception
{
    public HostProtocolException(string message) : base(message) { }
}
