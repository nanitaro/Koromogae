namespace Koromogae.Audio;

/// <summary>
/// サンプル形式の相互変換 (<see cref="SampleFormat"/> / <see cref="FormatChoice"/> / <see cref="RecordDepth"/>)。
/// 「自動」で試す形式の順序は、ここだけに置く (<see cref="FormatChoice"/> の説明と一致させる)。
/// このファイルは PortAudio に依存しない (単体テストでソースごとリンクしている)。PortAudio の定数への変換は <c>PaStream</c> にある。
/// </summary>
public static class SampleFormats
{
    /// <summary>「自動」のときに、開けるまで順に試す形式: Float32 → Int32 → Int24 → Int16。</summary>
    static readonly SampleFormat[] AutoOrder = { SampleFormat.Float32, SampleFormat.Int32, SampleFormat.Int24, SampleFormat.Int16 };

    /// <summary>実際に開いた形式を、画面の指定の形に直す。未定義の値は null。</summary>
    public static FormatChoice? ToChoice(SampleFormat format) => format switch
    {
        SampleFormat.Float32 => FormatChoice.Float32,
        SampleFormat.Int32 => FormatChoice.Int32,
        SampleFormat.Int24 => FormatChoice.Int24,
        SampleFormat.Int16 => FormatChoice.Int16,
        _ => null,
    };

    /// <summary>デバイスを開くときに試す形式。指定した形式ならその 1 つだけ (開けなくても、自動に切り替えない)。自動なら <see cref="AutoOrder"/>。</summary>
    public static SampleFormat[] Candidates(FormatChoice choice) => choice switch
    {
        FormatChoice.Float32 => new[] { SampleFormat.Float32 },
        FormatChoice.Int32 => new[] { SampleFormat.Int32 },
        FormatChoice.Int24 => new[] { SampleFormat.Int24 },
        FormatChoice.Int16 => new[] { SampleFormat.Int16 },
        _ => (SampleFormat[])AutoOrder.Clone(),
    };

    /// <summary>録音のビット深度に対応する、書き出しの形式。</summary>
    public static SampleFormat ToSampleFormat(RecordDepth depth) => depth switch
    {
        RecordDepth.Float32 => SampleFormat.Float32,
        RecordDepth.Int16 => SampleFormat.Int16,
        _ => SampleFormat.Int24,
    };
}