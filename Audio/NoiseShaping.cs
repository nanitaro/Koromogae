namespace Koromogae.Audio;

/// <summary>
/// ノイズシェイピング付きディザのフィルター (ノイズ伝達関数 NTF(z) = 1 + Σ a_k z^-k) の係数を、サンプルレートごとに決める。
/// <list type="bullet">
/// <item><b>48 kHz 以下</b>: Wannamaker らが発表した 9 次の F 特性 (<see cref="Published"/>)。可聴域の外に逃がせる余地が小さく (20 kHz〜ナイキストが狭い)、
/// 設計し直しても、聴感の評価を全部良くすることはできない (トレードオフ。<c>NoiseShapingTests</c> に数値)。実績のある係数をそのまま使う。</item>
/// <item><b>88.2 kHz 以上</b>: 聴感の重みから設計する (<see cref="Design"/>)。20 kHz より上は聞こえないので、ノイズをそこへ大きく逃がせる。
/// 設計の目標は 3 つの重み (A 特性・ITU-R 468・絶対聴取閾値) の平均で、12 次。F 特性をそのまま使うより、
/// どの重みで測っても 10 dB 以上小さく、全体のノイズも同じかそれ以下 (サンプルレートが高いほど少ない)。</item>
/// </list>
/// 設計は、聴感の重み W(f) を全極モデルに当てはめる (LPC): 重みの自己相関から Levinson-Durbin 法で A(z) を求めると、|1/A|² ≒ W となり、
/// NTF = A(z) は |NTF|² ∝ 1/W (聞こえやすい所ほどノイズが少ない) で、必ず最小位相 (Gerzon-Craven の定理を満たす) の FIR になる。
/// このファイルは他に依存しない (単体テストでソースごとリンクしている)。
/// </summary>
internal static class NoiseShaping
{
    /// <summary>この値以下のサンプルレートは、発表されている F 特性を使う。</summary>
    public const int PublishedMaxRate = 48000;

    /// <summary>設計する場合の次数。</summary>
    public const int DesignedOrder = 12;

    /// <summary>
    /// Wannamaker らの 9 次 F 特性 (44.1 kHz 用) の係数 (2.412, -3.370, 3.937, -4.174, 3.353, -2.205, 1.281, -0.569, 0.0847) の符号を反転したもの
    /// (NTF = 1 - 2.412 z^-1 + 3.370 z^-2 - ...)。最小位相になる向きを数値で確かめて決めた (周波数平均の ln|NTF| = 0)。
    /// </summary>
    public static readonly double[] Published = { -2.412, 3.370, -3.937, 4.174, -3.353, 2.205, -1.281, 0.569, -0.0847 };

    static readonly Dictionary<int, double[]> Cache = new();

    /// <summary>このサンプルレートで使う係数 a_k (k = 1..N)。同じレートでは同じ配列を返す (読み取り専用として扱うこと)。</summary>
    public static double[] TapsFor(int sampleRate)
    {
        if (sampleRate <= PublishedMaxRate) return Published;
        lock (Cache)
        {
            if (!Cache.TryGetValue(sampleRate, out var taps))
                Cache[sampleRate] = taps = Design(sampleRate, DesignedOrder, DesignWeight());
            return taps;
        }
    }

    /// <summary>設計の目標とする重み: A 特性・ITU-R 468・絶対聴取閾値 (それぞれ最大を 1 に正規化し、20 kHz より上をほぼ 0 にしたもの) の平均。</summary>
    public static Func<double, double> DesignWeight()
    {
        double pa = PerceptualWeights.Peak(PerceptualWeights.A);
        double p468 = PerceptualWeights.Peak(PerceptualWeights.Itu468);
        double pt = PerceptualWeights.Peak(PerceptualWeights.Threshold);
        return f => (PerceptualWeights.Clamped(PerceptualWeights.A, pa, f)
                     + PerceptualWeights.Clamped(PerceptualWeights.Itu468, p468, f)
                     + PerceptualWeights.Clamped(PerceptualWeights.Threshold, pt, f)) / 3;
    }

    /// <summary>
    /// 重み W(f) (電力) に当てはめて、NTF の係数 a_1..a_order を求める。|NTF(f)|² ∝ 1/W(f) の最小位相 FIR。
    /// </summary>
    public static double[] Design(int sampleRate, int order, Func<double, double> weight)
    {
        const int grid = 8192;
        var r = new double[order + 1];
        for (int m = 0; m <= order; m++)
        {
            double sum = 0;
            for (int k = 0; k <= grid / 2; k++)
            {
                double f = (double)k / grid * sampleRate;
                double w = weight(f) * (k == 0 || k == grid / 2 ? 0.5 : 1.0);
                sum += w * Math.Cos(2 * Math.PI * k * m / grid);
            }
            r[m] = sum;
        }
        r[0] *= 1 + 1e-9;      // 数値を安定させる

        var a = new double[order + 1];
        a[0] = 1;
        double err = r[0];
        for (int i = 1; i <= order; i++)
        {
            double acc = r[i];
            for (int j = 1; j < i; j++) acc += a[j] * r[i - j];
            double k = -acc / err;
            var prev = (double[])a.Clone();
            for (int j = 1; j < i; j++) a[j] = prev[j] + k * prev[i - j];
            a[i] = k;
            err *= 1 - k * k;
        }
        return a.Skip(1).ToArray();
    }

    // ---- 評価 ----

    /// <summary>周波数 f でのノイズ伝達関数の電力 |NTF(f)|²。</summary>
    public static double NtfPower(double[] taps, int sampleRate, double f)
    {
        double w = 2 * Math.PI * f / sampleRate, re = 1, im = 0;
        for (int k = 0; k < taps.Length; k++)
        {
            re += taps[k] * Math.Cos((k + 1) * w);
            im -= taps[k] * Math.Sin((k + 1) * w);
        }
        return re * re + im * im;
    }

    /// <summary>
    /// 聴感の重みをかけたノイズの量を、ノイズシェイピングなしの TPDF を 0 dB として、dB で返す (負なら、聞こえるノイズが減る)。
    /// 丸め誤差とディザを含めた誤差の電力は、TPDF でもシェイピングでも同じ (1/4 LSB²) で、違うのは NTF だけ。
    /// 範囲は 20 Hz〜20 kHz (サンプルレートの半分が下回るならそこまで)。
    /// </summary>
    public static double WeightedGainDb(double[] taps, int sampleRate, Func<double, double> weight)
        => WeightedGainDb(f => NtfPower(taps, sampleRate, f), sampleRate, weight);

    /// <summary>周波数ごとのノイズの電力の比 (TPDF = 1) が <paramref name="power"/> のときの、聴感の重みをかけたノイズの量 (TPDF = 0 dB)。</summary>
    public static double WeightedGainDb(Func<double, double> power, int sampleRate, Func<double, double> weight)
    {
        double hi = Math.Min(PerceptualWeights.HighHz, sampleRate / 2.0), lo = PerceptualWeights.LowHz;
        const int steps = 4000;
        double num = 0, den = 0;
        for (int i = 0; i < steps; i++)
        {
            double f = lo + (hi - lo) * (i + 0.5) / steps, w = weight(f);
            num += power(f) * w;
            den += w;
        }
        return 10 * Math.Log10(num / den);
    }

    /// <summary>
    /// HP-TPDF のノイズの電力の比 (TPDF = 1)。HP-TPDF のディザは「今回と前回の一様乱数の差」= 一様乱数に (1 - z^-1) をかけたもので、
    /// 丸め誤差 (白色、1/12) と合わせた全体の電力密度は (1/12)(3 - 2cos ω)。TPDF (白色、1/4) との比は (3 - 2cos ω)/3。
    /// 全体の電力は TPDF と同じで、直流で 1/3 (-4.8dB)、ナイキストで 5/3 (+2.2dB)。形は固定で、サンプルレートの半分までの全体に広がるだけ。
    /// </summary>
    public static double HpTpdfPower(double f, int sampleRate) => (3 - 2 * Math.Cos(2 * Math.PI * f / sampleRate)) / 3;

    /// <summary>全体のノイズ電力の増え方 (dB)。= 1 + Σ a² の dB。</summary>
    public static double TotalGainDb(double[] taps) => 10 * Math.Log10(1 + taps.Sum(a => a * a));

    /// <summary>ノイズ伝達関数の最大 (dB)。</summary>
    public static double PeakGainDb(double[] taps, int sampleRate)
    {
        double peak = 0;
        const int steps = 4000;
        for (int i = 0; i < steps; i++) peak = Math.Max(peak, NtfPower(taps, sampleRate, (i + 0.5) / steps * sampleRate / 2));
        return 10 * Math.Log10(peak);
    }

    /// <summary>周波数にわたる ln|NTF| の平均。最小位相の FIR では 0 (Gerzon-Craven)。係数の誤りや、最小位相でない係数の検出に使う。</summary>
    public static double MeanLogGain(double[] taps, int sampleRate)
    {
        double sum = 0;
        const int steps = 4000;
        for (int i = 0; i < steps; i++) sum += 0.5 * Math.Log(NtfPower(taps, sampleRate, (i + 0.5) / steps * sampleRate / 2));
        return sum / steps;
    }

    /// <summary>ノイズ電力のうち、20 kHz より上 (聞こえない側) にある割合 (%)。20 kHz がナイキスト以上なら NaN。</summary>
    public static double UltrasonicPercent(double[] taps, int sampleRate)
    {
        if (sampleRate / 2.0 <= PerceptualWeights.HighHz) return double.NaN;
        double total = 0, high = 0;
        const int steps = 8000;
        for (int i = 0; i < steps; i++)
        {
            double f = (i + 0.5) / steps * sampleRate / 2, p = NtfPower(taps, sampleRate, f);
            total += p;
            if (f > PerceptualWeights.HighHz) high += p;
        }
        return 100 * high / total;
    }
}
