namespace Koromogae.Audio;

/// <summary>
/// 画面に出す「実遅延」の内訳。端から端までの遅延 = 入力デバイス + 内部バッファ + プラグイン + 出力デバイス。
/// 入出力は PortAudio が報告する値で、USB 機器など、デバイス内部の遅延は含まない。
/// このファイルは他に依存しない (単体テストでソースごとリンクしている)。
/// </summary>
public readonly record struct LatencyInfo(double InputMs, double RingMs, double PluginMs, double OutputMs)
{
    public double TotalMs => InputMs + RingMs + PluginMs + OutputMs;

    static int Round(double ms) => (int)Math.Round(Math.Max(0, ms), MidpointRounding.AwayFromZero);

    /// <summary>
    /// 表示用の文字列。各項目を整数 ms に丸め、合計は「丸めた値の和」にする
    /// (別々に丸めると、画面の数字同士が足し合わず、不自然に見えるため)。
    /// </summary>
    public string Format()
    {
        int input = Round(InputMs), ring = Round(RingMs), plugin = Round(PluginMs), output = Round(OutputMs);
        return Loc.F("Latency.Format", input + ring + plugin + output, input, ring, plugin, output);
    }
}
