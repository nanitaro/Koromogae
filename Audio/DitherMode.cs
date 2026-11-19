namespace Koromogae.Audio;

/// <summary>
/// 整数出力 (Int24 / Int16) へ丸める前に加えるディザの方式。Float32 / Int32 では、どれを選んでも働かない。
/// 既定 (0) は <see cref="Tpdf"/>。設定ファイルに値が無い古い設定は、これで読み込まれる。
/// </summary>
public enum DitherMode
{
    /// <summary>TPDF (一様乱数 2 つの差 = ±1 LSB の三角分布)。丸め誤差を信号に依存しない白色ノイズにする。</summary>
    Tpdf,
    /// <summary>HP-TPDF (チャンネルごとに、今回と前回の一様乱数の差)。分布は TPDF と同じで、ノイズが高域に寄る。</summary>
    HpTpdf,
    /// <summary>ディザなし (四捨五入のみ)。</summary>
    Off,
    /// <summary>
    /// ノイズシェイピング付き (TPDF + 誤差のフィードバック)。丸め誤差とディザのノイズを、耳に聞こえやすい中域 (特に 3〜4kHz) から減らし、
    /// 聞こえにくい高域へ移す。全体のノイズの量は増える (高域に溜まる)。係数はサンプルレートで決まる (<see cref="NoiseShaping.TapsFor"/>: 48kHz 以下は発表されている 9 次の F 特性、88.2kHz 以上は聴感の重みから設計した 12 次)。
    /// </summary>
    Shaped,
}