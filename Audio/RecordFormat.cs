namespace Koromogae.Audio;

/// <summary>
/// 録音ファイルの形式。既定 (0) は WAV。設定ファイルに値が無い古い設定は、これで読み込まれる。
/// FLAC は可逆圧縮で、整数 (24bit / 16bit) だけを扱える (Float32 は保存できない)。
/// </summary>
public enum RecordFormat
{
    Wav,
    Flac,
}

/// <summary>
/// 録音の形式とビット深度の組み合わせの規則。画面 (SettingsForm) と設定ファイルの検証 (AppSettings.Sanitize) が、同じ規則を使う。
/// このファイルは他に依存しない (単体テストでソースごとリンクしている)。
/// </summary>
public static class RecordFormatRules
{
    /// <summary>このビット深度を、その形式で保存できるか。FLAC は整数だけ (Float32 は保存できない)。</summary>
    public static bool AllowsDepth(RecordFormat format, RecordDepth depth)
        => !(format == RecordFormat.Flac && depth == RecordDepth.Float32);

    /// <summary>そのまま使えないビット深度は Int24 にする (FLAC に Float32 は保存できない)。使えるものはそのまま。</summary>
    public static RecordDepth CoerceDepth(RecordFormat format, RecordDepth depth)
        => AllowsDepth(format, depth) ? depth : RecordDepth.Int24;
}
