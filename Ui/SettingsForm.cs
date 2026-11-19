using Koromogae.Audio;

namespace Koromogae.Ui;

/// <summary>
/// 画面の設定項目の値の一覧。設定ファイル (<see cref="AppSettings"/>) との対応を 1 か所に集める。
/// 設定項目を足すときは、ここ (フィールド・<see cref="From"/>・<see cref="ApplyTo"/>) に足す。
/// 足し忘れは、<c>SettingsFormTests</c> が AppSettings の全項目を調べて検出する。
/// このファイルは WPF に依存しない (単体テストでソースごとリンクしている)。
/// </summary>
public sealed record SettingsForm(
    ApiKind Api,
    string? InputDevice,
    string? OutputDevice,
    int SampleRate,
    int LatencyMs,
    int CushionExtraMs,
    FormatChoice InputFormat,
    FormatChoice OutputFormat,
    DitherMode DitherMode,
    string? RecordFolder = null,
    RecordDepth RecordDepth = AppSettings.DefaultRecordDepth,
    bool RecordDry = false,
    RecordFormat RecordFormat = AppSettings.DefaultRecordFormat,
    int FlacLevel = AppSettings.DefaultFlacLevel,
    UiLanguage Language = AppSettings.DefaultLanguage,
    bool MinimizeToTray = AppSettings.DefaultMinimizeToTray)
{
    /// <summary>設定ファイルから画面の値を作る (値は <c>AppSettings.Load</c> で検証・補正済み)。</summary>
    public static SettingsForm From(AppSettings s) => new(
        s.Api, s.InputDevice, s.OutputDevice, s.SampleRate, s.LatencyMs, s.CushionExtraMs,
        s.InputFormat, s.OutputFormat, s.DitherMode, s.RecordFolder, s.RecordDepth, s.RecordDry,
        s.RecordFormat, s.FlacLevel, s.Language, s.MinimizeToTray);

    /// <summary>エンジンを開始するときの設定 (デバイス以外)。入力と出力のサンプル形式を、取り違えずに写す。</summary>
    public EngineOptions ToEngineOptions() => new(Api, SampleRate, LatencyMs, CushionExtraMs, InputFormat, OutputFormat, DitherMode);

    /// <summary>画面の値を設定ファイルの項目へ書き込む (プラグインなど、画面の設定項目でないものには触れない)。</summary>
    public void ApplyTo(AppSettings s)
    {
        s.Api = Api;
        s.InputDevice = InputDevice;
        s.OutputDevice = OutputDevice;
        s.SampleRate = SampleRate;
        s.LatencyMs = LatencyMs;
        s.CushionExtraMs = CushionExtraMs;
        s.InputFormat = InputFormat;
        s.OutputFormat = OutputFormat;
        s.DitherMode = DitherMode;
        s.RecordFolder = RecordFolder;
        s.RecordDepth = RecordDepth;
        s.RecordDry = RecordDry;
        s.RecordFormat = RecordFormat;
        s.FlacLevel = FlacLevel;
        s.Language = Language;
        s.MinimizeToTray = MinimizeToTray;
    }

    /// <summary>
    /// 画面の保存先の入力を、保存する値にする。空白だけ・既定の保存先と同じなら null (既定に従う)。
    /// 貼り付けた文字の前後の空白や、引用符 ("C:\...") は取り除く。
    /// </summary>
    public static string? NormalizeRecordFolder(string? text, string defaultFolder)
    {
        string t = (text ?? "").Trim().Trim('"').Trim();
        if (t.Length == 0) return null;
        return string.Equals(t.TrimEnd('\\', '/'), defaultFolder.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase) ? null : t;
    }

    /// <summary>
    /// ディザは Int24 / Int16 でだけ働く。Float32 / Int32 を指定したときは選べなくする
    /// (自動は開くまで形式が分からないので、選べる)。
    /// </summary>
    public static bool DitherApplies(FormatChoice outputFormat)
        => outputFormat is FormatChoice.Auto or FormatChoice.Int24 or FormatChoice.Int16;

    /// <summary>録音のビット深度で、ディザが働くか (Int24 / Int16。Float32 は丸めない)。</summary>
    public static bool RecordDitherApplies(RecordDepth depth) => depth != RecordDepth.Float32;

    /// <summary>
    /// ディザ方式のコンボボックスを有効にするか。動作中は変更できない。
    /// 再生か録音のどちらかでディザが働くなら有効 (録音は動作を始めた後に押すので、方式は事前に選べる必要がある)。
    /// </summary>
    public static bool DitherEnabled(bool running, FormatChoice outputFormat, RecordDepth recordDepth)
        => !running && (DitherApplies(outputFormat) || RecordDitherApplies(recordDepth));
}
