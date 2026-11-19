namespace Koromogae.Audio;

/// <summary>
/// <see cref="AudioEngine.Start"/> に渡す、デバイス以外の設定。画面の値 (<c>SettingsForm.ToEngineOptions</c>) から作る。
/// 入力と出力のサンプル形式は同じ型 (<see cref="FormatChoice"/>) で隣り合うので、引数を並べるより、名前で渡す。
/// 既定値は <see cref="AppSettings"/> の <c>Default*</c> と同じ (二重に書かない)。
/// このファイルは PortAudio に依存しない (単体テストでソースごとリンクしている)。
/// </summary>
public sealed record EngineOptions(
    ApiKind Api,
    int SampleRate,
    int LatencyMs,
    int CushionExtraMs = AppSettings.DefaultCushionExtraMs,
    FormatChoice InputFormat = AppSettings.DefaultFormat,
    FormatChoice OutputFormat = AppSettings.DefaultFormat,
    DitherMode DitherMode = AppSettings.DefaultDitherMode);