using System.IO;

namespace Koromogae;

/// <summary>設定・ログの保存先がどう決まったか。</summary>
public enum DataLocationMode
{
    /// <summary>既定: %APPDATA%\Koromogae。</summary>
    Standard,
    /// <summary>環境変数 <see cref="AppPaths.DataDirEnvVar"/> の指定 (自動テストが、利用者の設定を触らないために使う)。</summary>
    Override,
    /// <summary>ポータブルモード: 本体のフォルダに目印のファイルがあり、そのフォルダに保存する。</summary>
    Portable,
}

/// <summary>決まった保存先と、その理由。</summary>
/// <param name="FallbackReason">ポータブルモードを望んだが、本体のフォルダに書き込めないため既定へ戻したときの理由。それ以外は null。</param>
public sealed record DataLocation(string Directory, DataLocationMode Mode, string? FallbackReason = null, string? PortableFolder = null)
{
    /// <summary>戻したことを利用者に知らせる文 (画面の言語。ダイアログ用)。戻していなければ null。ログには日本語の <see cref="FallbackReason"/> を使う。</summary>
    public string? FallbackText(System.Globalization.CultureInfo? culture = null)
        => FallbackReason == null ? null : Loc.F(culture, "Paths.Fallback", Directory, PortableFolder);

    /// <summary>app.log に残す 1 行。</summary>
    public string Describe() => $"データの保存先 ({ModeLabel}): {Directory}";   // i18n:log

    string ModeLabel => Mode switch
    {
        DataLocationMode.Portable => "ポータブル",   // i18n:log
        DataLocationMode.Override => "環境変数の指定",   // i18n:log
        _ => FallbackReason != null ? "標準 (ポータブルは使えませんでした)" : "標準",   // i18n:log
    };
}

/// <summary>
/// 設定ファイル (settings.json) とログ (app.log) の保存先。優先順位:
/// <list type="number">
/// <item>環境変数 <see cref="DataDirEnvVar"/> (自動テストが、利用者の設定を読み書きしないため)。</item>
/// <item>本体 (Koromogae.exe) のフォルダに目印のファイル (<c>portable</c> か <c>portable.txt</c>) があれば、そのフォルダ (ポータブルモード)。
/// ただし書き込めないとき (Program Files など) は、次へ戻して、<see cref="FallbackMessage"/> で知らせる。</item>
/// <item>%APPDATA%\Koromogae (既定)。</item>
/// </list>
/// 決めるのは起動後の最初の 1 回だけ (書き込みの確認がディスクに触れる。あとから環境変数を設定しても結果は変わらない)。
/// このファイルは WPF に依存しない (単体テストでソースごとリンクしている)。
/// </summary>
public static class AppPaths
{
    public const string DataDirEnvVar = "KOROMOGAE_DATA_DIR";

    /// <summary>
    /// 標準の保存先 (%APPDATA%\Koromogae) の「%APPDATA%」の部分を差し替える、自動テスト用の環境変数。
    /// .NET の <c>GetFolderPath(ApplicationData)</c> は APPDATA 環境変数を無視する (実測) ので、テストが利用者の本物の設定を触らないための別の仕掛けが要る。
    /// ポータブルモードが効かなかったときの戻り先も、これで安全に確かめられる。
    /// </summary>
    public const string AppDataEnvVar = "KOROMOGAE_APPDATA_DIR";

    /// <summary>ポータブルモードの目印のファイル名 (どちらか。Explorer で拡張子を隠したまま「新規作成 > テキスト」で作っても動くように、両方を認める)。</summary>
    public static readonly string[] PortableMarkerNames = { "portable", "portable.txt" };

    static readonly Lazy<DataLocation> Detected = new(() => ResolveLocation(
        Environment.GetEnvironmentVariable(DataDirEnvVar),
        ResolveAppData(Environment.GetEnvironmentVariable(AppDataEnvVar), Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)),
        AppContext.BaseDirectory,
        File.Exists,
        CanWrite));

    /// <summary>この実行で使う保存先 (起動後の最初の 1 回で決まる)。</summary>
    public static DataLocation Location => Detected.Value;

    public static string DataDirectory => Location.Directory;

    public static DataLocationMode Mode => Location.Mode;

    /// <summary>ポータブルモードを望んだが戻したとき、利用者に知らせる 1 文。なければ null。</summary>
    public static string? FallbackMessage => Location.FallbackText();

    /// <summary>標準の保存先の起点 (%APPDATA% の部分)。<see cref="AppDataEnvVar"/> の指定があればそれ (相対パスは絶対パスにする)、なければ本物の <paramref name="realAppData"/>。空白だけの指定は無いものとみなす。</summary>
    public static string ResolveAppData(string? overrideAppData, string realAppData)
        => string.IsNullOrWhiteSpace(overrideAppData) ? realAppData : Path.GetFullPath(overrideAppData);

    /// <summary>指定があればそのフォルダ (相対パスは絶対パスにする)、なければ <c>appData\Koromogae</c>。空白だけの指定は無いものとみなす。</summary>
    public static string Resolve(string? overrideDirectory, string appData)
        => string.IsNullOrWhiteSpace(overrideDirectory)
            ? Path.Combine(appData, "Koromogae")
            : Path.GetFullPath(overrideDirectory);

    /// <summary>
    /// 保存先を決める (純粋な判断。ディスクやレジストリには、引数の関数を通してしか触れない)。
    /// </summary>
    /// <param name="baseDirectory">本体のフォルダ。</param>
    /// <param name="fileExists">ファイルがあるか。</param>
    /// <param name="canWrite">フォルダに書き込めるか。</param>
    public static DataLocation ResolveLocation(string? overrideDirectory, string appData, string baseDirectory,
        Func<string, bool> fileExists, Func<string, bool> canWrite)
    {
        if (!string.IsNullOrWhiteSpace(overrideDirectory))
            return new DataLocation(Resolve(overrideDirectory, appData), DataLocationMode.Override);

        string appFolder = baseDirectory.TrimEnd('\\', '/');
        if (appFolder.Length > 0 && PortableMarkerNames.Any(name => fileExists(Path.Combine(appFolder, name))))
        {
            if (canWrite(appFolder))
                return new DataLocation(appFolder, DataLocationMode.Portable);

            string standard = Resolve(null, appData);
            return new DataLocation(standard, DataLocationMode.Standard,
                $"本体のフォルダに「portable」の目印がありますが、そのフォルダに書き込めないため、設定とログは {standard} に保存します。\n({appFolder})", appFolder);   // i18n:log
        }

        return new DataLocation(Resolve(null, appData), DataLocationMode.Standard);
    }

    /// <summary>フォルダに一時ファイルを作って消せるか (権限や読み取り専用の媒体で、書き込めないことを事前に知るため)。</summary>
    static bool CanWrite(string directory)
    {
        try
        {
            string probe = Path.Combine(directory, ".write-test-" + Guid.NewGuid().ToString("N"));
            File.WriteAllBytes(probe, Array.Empty<byte>());
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
