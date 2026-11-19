using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Koromogae.Audio;

namespace Koromogae;

public sealed class PluginSetting
{
    public string Path { get; set; } = "";
    public bool Enabled { get; set; } = true;
    /// <summary>プラグインの状態 (Base64)。</summary>
    public string? State { get; set; }
    /// <summary>起動時にロードに失敗した連続回数。上限に達したら設定から削除する。</summary>
    public int FailCount { get; set; }
}

public sealed class AppSettings
{
    // ---- 設定ファイルの上限 (破損・肥大した設定で起動が遅くなったりメモリを使いすぎたりしないため) ----

    public const long MaxFileBytes = 128L * 1024 * 1024;
    /// <summary>VST チェーンの最大数。プラグインは 1 つにつきプラグインホストプロセスを 1 つ起動する (1 つあたり 20〜70MB 程度)。</summary>
    public const int MaxPlugins = 64;
    /// <summary>1 プラグインの状態データ (デコード後)。プラグインホスト通信の上限と同じ。</summary>
    public const int MaxStateBytes = 64 * 1024 * 1024;
    public const int MaxPathLength = 4096;
    public const int MaxDeviceNameLength = 512;
    /// <summary>ロード失敗がこの回数連続したプラグインは設定から外す。</summary>
    public const int MaxLoadFailures = 3;

    public static readonly int[] AllowedSampleRates = { 44100, 48000, 88200, 96000, 176400, 192000 };
    /// <summary>選べるバッファ (ms)。0 は PortAudio に「できるだけ小さく」を任せる値 (実際の大きさは API とデバイスで決まる)。</summary>
    public static readonly int[] AllowedLatenciesMs = { 0, 5, 10, 20, 30, 50, 100, 200, 300, 400, 500 };
    /// <summary>選べる内部バッファの上乗せ (ms)。0 は「自動」(上乗せなし = 出力1回ぶん + 直前の入力コールバック1回ぶん)。</summary>
    public static readonly int[] AllowedCushionExtrasMs = { 0, 5, 10, 20, 30, 40, 50, 100 };
    public const int MinFlacLevel = 0;
    public const int MaxFlacLevel = 8;
    public const int DefaultFlacLevel = 5;
    // ---- 既定値。プロパティの初期値、読み込み時の補正 (Sanitize)、画面で何も選ばれていないときの値は、すべてここを使う ----
    public const ApiKind DefaultApi = ApiKind.WasapiShared;
    public const int DefaultSampleRate = 48000;
    public const int DefaultLatencyMs = 20;
    public const int DefaultCushionExtraMs = 0;
    public const FormatChoice DefaultFormat = FormatChoice.Auto;
    public const DitherMode DefaultDitherMode = DitherMode.Tpdf;
    public const RecordDepth DefaultRecordDepth = RecordDepth.Int24;
    public const RecordFormat DefaultRecordFormat = RecordFormat.Wav;
    public const UiLanguage DefaultLanguage = UiLanguage.Auto;
    public const bool DefaultMinimizeToTray = false;

    /// <summary>Base64 文字列としての最大長 (<see cref="MaxStateBytes"/> をエンコードした長さ)。</summary>
    public static int MaxStateBase64Chars => (MaxStateBytes + 2) / 3 * 4;

    public ApiKind Api { get; set; } = DefaultApi;
    public string? InputDevice { get; set; }
    public string? OutputDevice { get; set; }
    public int SampleRate { get; set; } = DefaultSampleRate;
    public int LatencyMs { get; set; } = DefaultLatencyMs;
    public int CushionExtraMs { get; set; } = DefaultCushionExtraMs;
    public FormatChoice InputFormat { get; set; } = DefaultFormat;
    public FormatChoice OutputFormat { get; set; } = DefaultFormat;
    public DitherMode DitherMode { get; set; } = DefaultDitherMode;
    /// <summary>録音ファイルの保存先。null は既定 (<see cref="DefaultRecordFolder"/>)。</summary>
    public string? RecordFolder { get; set; }
    public RecordDepth RecordDepth { get; set; } = DefaultRecordDepth;
    /// <summary>録音ファイルの形式 (WAV / FLAC)。FLAC は整数 (Int24 / Int16) だけ。</summary>
    public RecordFormat RecordFormat { get; set; } = DefaultRecordFormat;
    /// <summary>FLAC の圧縮レベル (0〜8)。大きいほど小さくなるが、CPU を使う。</summary>
    public int FlacLevel { get; set; } = DefaultFlacLevel;
    public bool RecordDry { get; set; }
    /// <summary>画面の言語。変更は再起動で反映する (起動の最初に決める)。</summary>
    public UiLanguage Language { get; set; } = DefaultLanguage;
    /// <summary>最小化したとき、タスクトレイへ格納するか (false は Windows 標準の最小化)。<c>--minimized</c> での起動だけは、この設定にかかわらずトレイへ。</summary>
    public bool MinimizeToTray { get; set; } = DefaultMinimizeToTray;
    public List<PluginSetting> Plugins { get; set; } = new();

    /// <summary>
    /// 読み込み時に問題があった場合の、利用者向けの説明 (保存はしない)。
    /// 設定を読むのは、画面の言語を決める前 (言語の設定がこのファイルにある) なので、文は読んだ時ではなく、参照した時に画面の言語で組み立てる。
    /// </summary>
    [JsonIgnore]
    public string? LoadMessage => _loadMessage?.Invoke();

    Func<string>? _loadMessage;

    /// <summary>録音ファイルの既定の保存先 (ミュージック\Koromogae)。</summary>
    public static string DefaultRecordFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "Koromogae");

    public static string DefaultFilePath => Path.Combine(AppPaths.DataDirectory, "settings.json");

    static readonly Lazy<AppSettings> Loaded = new(() => Load());

    /// <summary>
    /// 起動時に読んだ設定 (最初に使うときに 1 回だけ読む)。言語は画面を作る前に要るので、<c>App</c> と <c>MainWindow</c> が同じものを使う
    /// (別々に読むと、設定の補正の警告がログに 2 回出る)。
    /// </summary>
    public static AppSettings Current => Loaded.Value;

    static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    /// <summary>
    /// 設定を読み込む。壊れている・大きすぎる場合は元のファイルを .bak に退避して既定値を返す
    /// (そのまま終了時の保存で上書きして、元のデータを失わないため)。値は読み込み後に必ず検証・補正する。
    /// </summary>
    public static AppSettings Load(string? path = null)
    {
        path ??= DefaultFilePath;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return new AppSettings();
            if (info.Length > MaxFileBytes)
                return Quarantine(path, $"ファイルが大きすぎます ({info.Length:N0} バイト、上限 {MaxFileBytes:N0})",   // i18n:log
                    () => Loc.F("Settings.TooLarge", info.Length, MaxFileBytes), null);

            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path));
            if (settings == null) return Quarantine(path, "内容が空です", () => Loc.T("Settings.Empty"), null);   // i18n:log

            var warnings = new List<string>();
            settings.Sanitize(warnings);
            if (warnings.Count > 0)
            {
                foreach (string warning in warnings) AppLog.Warn($"設定の補正: {warning}");
                int corrected = warnings.Count;
                settings._loadMessage = () => Loc.F("Settings.Corrected", corrected, AppLog.LogPath);
            }
            return settings;
        }
        catch (Exception ex)
        {
            return Quarantine(path, ex.Message, () => ex.Message, ex);
        }
    }

    /// <param name="reason">ログに残す理由 (日本語)。</param>
    /// <param name="screenReason">画面に出す理由を、参照した時に画面の言語で作る関数。</param>
    static AppSettings Quarantine(string path, string reason, Func<string> screenReason, Exception? ex)
    {
        string backup = path + ".bak";
        bool moved;
        try
        {
            File.Move(path, backup, overwrite: true);
            moved = true;
            AppLog.Error($"設定ファイルを読み込めません: {path} -> {backup} に退避 ({reason})", ex);
        }
        catch (Exception moveEx)
        {
            moved = false;
            AppLog.Error($"設定ファイルを読み込めず、退避にも失敗しました: {path} ({reason})", moveEx);
        }
        return new AppSettings
        {
            _loadMessage = () => Loc.F("Settings.Unreadable", screenReason()) + (moved ? Loc.F("Settings.Moved", backup) : Loc.T("Settings.MoveFailed")),
        };
    }

    /// <summary>範囲外・異常な値を既定値/安全な値に補正する。補正した内容を <paramref name="warnings"/> に追加する。</summary>
    internal void Sanitize(List<string> warnings)
    {
        // i18n:log-begin  (補正の警告は app.log にだけ書くので日本語のまま。画面に出す LoadMessage は翻訳する)
        Api = FixUndefined(Api, DefaultApi, "サウンド API の値", "既定値", warnings);
        InputFormat = FixUndefined(InputFormat, DefaultFormat, "入力のサンプル形式の値", "自動", warnings);
        OutputFormat = FixUndefined(OutputFormat, DefaultFormat, "出力のサンプル形式の値", "自動", warnings);
        DitherMode = FixUndefined(DitherMode, DefaultDitherMode, "ディザ方式の値", "TPDF", warnings);
        RecordDepth = FixUndefined(RecordDepth, DefaultRecordDepth, "録音のビット深度の値", "Int24", warnings);
        RecordFormat = FixUndefined(RecordFormat, DefaultRecordFormat, "録音の形式の値", "WAV", warnings);
        Language = FixUndefined(Language, DefaultLanguage, "言語の値", "自動", warnings);
        if (FlacLevel < MinFlacLevel || FlacLevel > MaxFlacLevel)
        {
            warnings.Add($"FLAC の圧縮レベル {FlacLevel} は 0〜8 の範囲外です。{DefaultFlacLevel} にしました。");
            FlacLevel = DefaultFlacLevel;
        }
        if (!RecordFormatRules.AllowsDepth(RecordFormat, RecordDepth))
        {
            // FLAC は float を保存できない (画面でも、FLAC のとき Float32 は選べない。規則は RecordFormatRules に 1 つだけ)
            warnings.Add("FLAC には Float32 で録音できません。ビット深度を Int24 にしました。");
            RecordDepth = RecordFormatRules.CoerceDepth(RecordFormat, RecordDepth);
        }
        if (RecordFolder != null)
        {
            string folder = RecordFolder.Trim();
            if (folder.Length == 0)
            {
                RecordFolder = null;
            }
            else if (folder.Length > MaxPathLength || folder.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            {
                warnings.Add("録音の保存先が長すぎるか、使えない文字を含みます。既定の保存先にしました。");
                RecordFolder = null;
            }
            else
            {
                RecordFolder = folder;
            }
        }
        if (!AllowedSampleRates.Contains(SampleRate))
        {
            warnings.Add($"サンプルレート {SampleRate} は選択肢にありません。{DefaultSampleRate} にしました。");
            SampleRate = DefaultSampleRate;
        }
        if (!AllowedLatenciesMs.Contains(LatencyMs))
        {
            warnings.Add($"バッファ {LatencyMs} ms は選択肢にありません。{DefaultLatencyMs} ms にしました。");
            LatencyMs = DefaultLatencyMs;
        }
        if (!AllowedCushionExtrasMs.Contains(CushionExtraMs))
        {
            warnings.Add($"内部バッファの上乗せ {CushionExtraMs} ms は選択肢にありません。既定にしました。");
            CushionExtraMs = DefaultCushionExtraMs;
        }
        InputDevice = LimitDeviceName(InputDevice, "録音デバイス名", warnings);
        OutputDevice = LimitDeviceName(OutputDevice, "再生デバイス名", warnings);

        var clean = new List<PluginSetting>();
        int total = 0;
        foreach (var plugin in Plugins ?? new List<PluginSetting>())
        {
            total++;
            if (plugin == null || string.IsNullOrWhiteSpace(plugin.Path))
            {
                warnings.Add($"{total} 件目のプラグインはパスが空のため無視しました。");
                continue;
            }
            if (plugin.Path.Length > MaxPathLength || plugin.Path.IndexOfAny(System.IO.Path.GetInvalidPathChars()) >= 0)
            {
                warnings.Add($"{total} 件目のプラグインはパスが不正なため無視しました。");
                continue;
            }
            if (clean.Count >= MaxPlugins)
            {
                warnings.Add($"プラグインが多すぎます ({total} 件以上)。上限 {MaxPlugins} 件を超える分は無視しました。");
                break;
            }
            if (plugin.State != null && plugin.State.Length > MaxStateBase64Chars)
            {
                warnings.Add($"{System.IO.Path.GetFileName(plugin.Path)} の状態データが大きすぎます (上限 {MaxStateBytes / 1024 / 1024} MB)。状態は読み込みません。");
                plugin.State = null;
            }
            plugin.FailCount = Math.Clamp(plugin.FailCount, 0, MaxLoadFailures);
            clean.Add(plugin);
        }
        Plugins = clean;
    }

    /// <summary>列挙型の値が定義済みでなければ、警告を足して既定値に戻す (設定ファイルの数値が範囲外のとき)。</summary>
    static T FixUndefined<T>(T value, T fallback, string what, string fallbackText, List<string> warnings) where T : struct, Enum
    {
        if (Enum.IsDefined(value)) return value;
        warnings.Add($"{what} {Convert.ToInt32(value)} は不正です。{fallbackText}にしました。");
        return fallback;
    }

    static string? LimitDeviceName(string? name, string what, List<string> warnings)
    {
        if (name == null || name.Length <= MaxDeviceNameLength) return name;
        warnings.Add($"{what}が長すぎます。無視しました。");
        return null;
    }
    // i18n:log-end

    /// <summary>
    /// 設定を保存する。一時ファイルに書いてから置き換えるので、途中でクラッシュしても元のファイルは壊れない。
    /// 失敗しても例外は投げず、ログに記録して false を返す。
    /// </summary>
    public bool Save(string? path = null)
    {
        path ??= DefaultFilePath;
        string temp = path + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string json = JsonSerializer.Serialize(this, WriteOptions);
            if (Encoding.UTF8.GetByteCount(json) > MaxFileBytes)
            {
                AppLog.Error($"設定が大きすぎるため保存しません ({json.Length:N0} 文字、上限 {MaxFileBytes:N0} バイト)");
                return false;
            }

            File.WriteAllText(temp, json, new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(temp, path, null);
            else File.Move(temp, path);
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error($"設定を保存できません: {path}", ex);
            try { File.Delete(temp); } catch { }
            return false;
        }
    }
}
