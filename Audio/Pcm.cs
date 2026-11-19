using System.Runtime.CompilerServices;

namespace Koromogae.Audio;

public enum SampleFormat { Float32, Int32, Int24, Int16 }

/// <summary>
/// インターリーブされたデバイス形式 (float / 整数) とステレオ float の相互変換。
/// このファイルは音声ライブラリ (PortAudio) に依存しない。単体でテストできるよう、その状態を保つこと。
/// </summary>
internal static unsafe class Pcm
{
    // 読み込みと書き込みで同じ尺度 (2^(bit-1)) を使うので、整数 → float → 整数 は、ディザなしなら完全に元へ戻る。
    const float Scale16 = 32768f;
    const float Scale24 = 8388608f;
    const float Scale32 = 2147483648f;

    // float で表せる、int の範囲内の最大値 (2147483647 は float で表せず 2^31 に丸まり、int にすると桁あふれするため)
    const float Max32 = 2147483520f;

    public static int BytesPerSample(SampleFormat f) => f switch
    {
        SampleFormat.Float32 or SampleFormat.Int32 => 4,
        SampleFormat.Int24 => 3,
        _ => 2,
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static float Read(byte* p, SampleFormat f)
    {
        switch (f)
        {
            case SampleFormat.Float32: return *(float*)p;
            case SampleFormat.Int32: return *(int*)p / Scale32;
            case SampleFormat.Int24: return (((p[0] << 8) | (p[1] << 16) | (p[2] << 24)) >> 8) / Scale24;
            default: return *(short*)p / Scale16;
        }
    }

    /// <summary>xorshift32 による一様乱数 [0, 1)。オーディオスレッドで割り当てもロックもしない。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static float NextUniform(ref uint state)
    {
        state ^= state << 13;
        state ^= state >> 17;
        state ^= state << 5;
        return (state >> 8) * (1f / 16777216f);
    }

    /// <summary>
    /// ディザの乱数の状態。出力コールバックの 1 スレッドだけが使う。
    /// HP-TPDF は「前回の一様乱数」をチャンネルごとに持つ (左右を交互に書くので、共通にすると左右が混ざる)。
    /// </summary>
    public struct DitherState
    {
        public uint Rng;
        public float Prev0;   // 1 ch 目 (モノラルのときもこちら)
        public float Prev1;   // 2 ch 目
        /// <summary>ノイズシェイピングの誤差の履歴 (チャンネルごと。[0] = 1 サンプル前)。</summary>
        public float[] Hist0, Hist1;
        /// <summary>ノイズシェイピングの係数 a_k (サンプルレートで決まる。<see cref="NoiseShaping.TapsFor"/>)。</summary>
        public double[] Taps;

        /// <param name="seed">0 にしてはいけない (xorshift が止まる)。</param>
        /// <param name="sampleRate">出力 (ファイル) のサンプルレート。ノイズシェイピングの係数を決める。</param>
        public static DitherState Create(uint seed, int sampleRate = 44100)
        {
            var taps = NoiseShaping.TapsFor(sampleRate);
            return new()
            {
                Rng = seed, Prev0 = 0.5f, Prev1 = 0.5f,
                Taps = taps, Hist0 = new float[taps.Length], Hist1 = new float[taps.Length],
            };
        }
    }
    /// <summary>フィードバックする誤差の上限 (LSB)。クリップで誤差が大きくなっても、ループが暴走しないように抑える。通常の誤差は ±1.5 LSB 以内。</summary>
    const float MaxShapedError = 2f;

    /// <summary>この形式への書き出しで、<see cref="DitherMode"/> が働くか (Int24 / Int16 のみ)。</summary>
    public static bool UsesDither(SampleFormat f) => f is SampleFormat.Int24 or SampleFormat.Int16;

    /// <summary>画面に出すディザの状態。実際に開いた形式で決まる (Float32 / Int32 では、設定が TPDF でも働かないので「オフ」)。</summary>
    public static string DitherLabel(SampleFormat f, DitherMode mode, System.Globalization.CultureInfo? culture = null) => !UsesDither(f) ? Loc.T("Dither.Label.Off", culture) : mode switch
    {
        DitherMode.Tpdf => Loc.T("Dither.Label.Tpdf", culture),
        DitherMode.HpTpdf => Loc.T("Dither.Label.HpTpdf", culture),
        DitherMode.Shaped => Loc.T("Dither.Label.Shaped", culture),
        _ => Loc.T("Dither.Label.Off", culture),
    };

    /// <summary>
    /// float を整数の目盛りに量子化する。四捨五入 (切り捨てではない) で、mode が <see cref="DitherMode.Off"/> でなければ
    /// 量子化の前にディザ (±1 LSB の三角分布) を加える。
    /// ディザは、量子化誤差が信号と相関して歪みになるのを、無相関なノイズに変える。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static float Quantize(float v, float scale, float min, float max, DitherMode mode, ref uint rng, ref float prev, float[]? hist, double[]? taps)
    {
        float x = v * scale;

        if (mode == DitherMode.Shaped && hist != null && taps != null)
        {
            if (v == 0f)
            {
                // 完全な無音は、無音のまま (履歴も捨てる。残すと、無音のはずの出力にノイズが出る)
                Array.Clear(hist);
                return 0f;
            }

            // 誤差のフィードバック: 出力 = 入力 + TPDF + NTF を通した誤差。誤差 = 出力 - フィードバック後の値 (ディザも含めて整形する)。
            // 計算は double で行う: float (仮数 24bit) だと、24bit 出力の大きな信号 (±数百万 LSB) では 1 LSB 未満の端数が丸められて消え、整形が効かなくなる
            double fed = (double)v * scale;
            for (int k = 0; k < taps.Length; k++) fed += taps[k] * hist[k];
            double u = fed + (NextUniform(ref rng) - NextUniform(ref rng));
            double q = Math.Floor(u + 0.5);
            q = q < min ? min : q > max ? max : q;
            float err = (float)Math.Clamp(q - fed, -MaxShapedError, MaxShapedError);
            for (int k = hist.Length - 1; k > 0; k--) hist[k] = hist[k - 1];
            hist[0] = err;
            return (float)q;
        }

        // 完全な無音 (0) にはディザを付けない。付けると、無音のはずの出力に -90dBFS 程度のヒスが出る
        if (v != 0f)
        {
            if (mode == DitherMode.Tpdf)
            {
                x += NextUniform(ref rng) - NextUniform(ref rng);
            }
            else if (mode == DitherMode.HpTpdf)
            {
                float u = NextUniform(ref rng);
                x += u - prev;
                prev = u;
            }
        }
        x = MathF.Floor(x + 0.5f);
        return x < min ? min : x > max ? max : x;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static void Write(byte* p, SampleFormat f, float v, DitherMode mode, ref uint rng, ref float prev, float[]? hist, double[]? taps)
    {
        // 非数 (プラグインの不具合で出ることがある) は無音にする。±無限大は下のクランプで ±フルスケールになる
        if (float.IsNaN(v)) v = 0f;
        if (v > 1f) v = 1f; else if (v < -1f) v = -1f;

        switch (f)
        {
            case SampleFormat.Float32:
                *(float*)p = v;
                break;
            case SampleFormat.Int32:
                // 32bit 整数は float (24bit 精度) より細かく、ディザしても意味がないので、丸めだけ行う
                *(int*)p = (int)Quantize(v, Scale32, -Scale32, Max32, DitherMode.Off, ref rng, ref prev, null, null);
                break;
            case SampleFormat.Int24:
                {
                    int s = (int)Quantize(v, Scale24, -Scale24, Scale24 - 1f, mode, ref rng, ref prev, hist, taps);
                    p[0] = (byte)s;
                    p[1] = (byte)(s >> 8);
                    p[2] = (byte)(s >> 16);
                    break;
                }
            default:
                *(short*)p = (short)Quantize(v, Scale16, -Scale16, Scale16 - 1f, mode, ref rng, ref prev, hist, taps);
                break;
        }
    }

    /// <summary>インターリーブされた任意フォーマットの入力をステレオ float に変換する (モノラルは両ch へ複製)。</summary>
    public static void ReadStereo(byte* src, SampleFormat f, int channels, int frames, float* l, float* r)
    {
        int bps = BytesPerSample(f);
        int stride = bps * channels;
        int rOffset = channels > 1 ? bps : 0;
        for (int i = 0; i < frames; i++)
        {
            byte* frame = src + i * stride;
            l[i] = Read(frame, f);
            r[i] = Read(frame + rOffset, f);
        }
    }

    /// <summary>
    /// ステレオ float を出力デバイスのチャンネル数/フォーマットへ書き出す (モノラルは L/R を平均、3ch 目以降は無音)。
    /// 整数形式への変換は四捨五入し、16bit / 24bit では <paramref name="mode"/> のディザを加える (32bit 整数と float では働かない)。
    /// <paramref name="state"/> は乱数などの状態で、呼び出し側が (スレッドごとに) 持ち続ける。
    /// </summary>
    public static void WriteStereo(float* l, float* r, SampleFormat f, int channels, byte* dst, int frames,
        ref DitherState state, DitherMode mode = DitherMode.Tpdf)
    {
        int bps = BytesPerSample(f);
        int stride = bps * channels;
        for (int i = 0; i < frames; i++)
        {
            byte* frame = dst + i * stride;
            if (channels == 1)
            {
                Write(frame, f, (l[i] + r[i]) * 0.5f, mode, ref state.Rng, ref state.Prev0, state.Hist0, state.Taps);
                continue;
            }

            Write(frame, f, l[i], mode, ref state.Rng, ref state.Prev0, state.Hist0, state.Taps);
            Write(frame + bps, f, r[i], mode, ref state.Rng, ref state.Prev1, state.Hist1, state.Taps);
            for (int c = 2; c < channels; c++) Write(frame + c * bps, f, 0f, DitherMode.Off, ref state.Rng, ref state.Prev1, null, null);
        }
    }

    public static float Peak(float* l, float* r, int frames)
    {
        float peak = 0f;
        for (int i = 0; i < frames; i++)
        {
            float a = Math.Abs(l[i]);
            float b = Math.Abs(r[i]);
            if (a > peak) peak = a;
            if (b > peak) peak = b;
        }
        return peak;
    }
}
