namespace Koromogae.Audio;

/// <summary>
/// 録音ファイル (WAV) のビット深度。既定 (0) は Int24 (24bit)。設定ファイルに値が無い古い設定は、これで読み込まれる。
/// Int24 と Int16 は、画面の「ディザ方式」で整数に丸める。Float32 は丸めもディザもない。
/// </summary>
public enum RecordDepth
{
    Int24,
    Float32,
    Int16,
}
