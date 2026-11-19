namespace Koromogae.Audio;

/// <summary>
/// ノイズの聞こえやすさを評価するための、聴感の重み (電力の比)。ノイズシェイピングのフィルターの設計と、その評価に使う。
/// どれも「1 kHz 付近を基準にした、周波数ごとの感度」で、値が大きいほど、その周波数のノイズが耳に付きやすい。
/// <list type="bullet">
/// <item><see cref="A"/>: IEC 61672 の A 特性。騒音計で最も広く使われる。低い音量 (約 40 phon) の等ラウドネス曲線に近い。</item>
/// <item><see cref="Itu468"/>: ITU-R BS.468 (CCIR 468)。オーディオ機器のノイズ測定用。6.3 kHz 付近を +12 dB 強調し、高域のノイズに敏感。</item>
/// <item><see cref="Threshold"/>: 聞こえる最小の音の大きさ (絶対聴取閾値、Terhardt の式) の逆数。ごく小さいノイズ (曲の終わりの無音に近い部分) の聞こえやすさ。</item>
/// </list>
/// 評価は 20 Hz〜20 kHz の範囲で行う (それ以外は聞こえない)。このファイルは他に依存しない (単体テストでソースごとリンクしている)。
/// </summary>
internal static class PerceptualWeights
{
    public const double LowHz = 20;
    public const double HighHz = 20000;

    /// <summary>A 特性 (電力)。1 kHz で 1。</summary>
    public static double A(double f)
    {
        static double Ra(double f)
        {
            double f2 = f * f;
            return 12194.0 * 12194.0 * f2 * f2
                   / ((f2 + 20.6 * 20.6) * Math.Sqrt((f2 + 107.7 * 107.7) * (f2 + 737.9 * 737.9)) * (f2 + 12194.0 * 12194.0));
        }
        double r = Ra(f) / Ra(1000);
        return r * r;
    }

    /// <summary>ITU-R BS.468 の重み (電力)。1 kHz でほぼ 1 (0 dB)、6.3 kHz で約 +12.2 dB。</summary>
    public static double Itu468(double f)
    {
        double f2 = f * f, f4 = f2 * f2, f6 = f4 * f2;
        double h1 = -4.737338981378384e-24 * f6 + 2.043828333606125e-15 * f4 - 1.363894795463638e-7 * f2 + 1;
        double h2 = 1.306612257412824e-19 * f4 * f - 2.118150887518656e-11 * f2 * f + 5.559488023498642e-4 * f;
        double r = 1.246332637532143e-4 * f / Math.Sqrt(h1 * h1 + h2 * h2);
        double g = Math.Pow(10, 18.2 / 20) * r;
        return g * g;
    }

    /// <summary>絶対聴取閾値 (Terhardt の式、dB SPL)。3〜4 kHz で最も低く (約 -5 dB)、10 kHz で約 11 dB、16 kHz で約 66 dB、20 kHz で 160 dB 以上。</summary>
    public static double ThresholdDb(double f)
    {
        double k = Math.Max(f, 20) / 1000.0;
        return 3.64 * Math.Pow(k, -0.8) - 6.5 * Math.Exp(-0.6 * (k - 3.3) * (k - 3.3)) + 1e-3 * Math.Pow(k, 4);
    }

    /// <summary>閾値の逆数 (電力)。閾値が高い (聞こえにくい) 周波数ほど小さい。式が急に大きくなる高域は 70 dB で打ち切る。</summary>
    public static double Threshold(double f) => Math.Pow(10, -Math.Min(ThresholdDb(f), 70) / 10);

    /// <summary>20 kHz までは重み、それより上は聞こえないので重みをほぼ 0 (-40 dB) にする。設計用。</summary>
    public static double Clamped(Func<double, double> weight, double peak, double f)
        => f <= HighHz ? weight(f) / peak : weight(HighHz) / peak * 1e-4;

    /// <summary>20 Hz〜20 kHz での重みの最大値 (正規化用)。</summary>
    public static double Peak(Func<double, double> weight)
        => Enumerable.Range(0, 2000).Max(i => weight(LowHz + (HighHz - LowHz) * i / 1999.0));
}
