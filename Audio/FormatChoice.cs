namespace Koromogae.Audio;

/// <summary>
/// デバイスを開くサンプル形式の指定。<see cref="Auto"/> は Float32 → Int32 → Int24 → Int16 の順に開けた形式を採用する。
/// それ以外は指定した形式だけを試し、開けなければエラーにする (自動に切り替えない)。
/// </summary>
public enum FormatChoice
{
    Auto,
    Float32,
    Int32,
    Int24,
    Int16,
}
