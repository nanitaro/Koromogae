using System.Globalization;

namespace Koromogae;

/// <summary>画面の言語の設定。設定ファイルには数値で保存する (既存の値を変えないこと)。</summary>
public enum UiLanguage
{
    /// <summary>Windows の表示言語に従う (日本語なら日本語、それ以外は英語)。</summary>
    Auto = 0,
    Japanese = 1,
    English = 2,
}

/// <summary>実際に使う言語を決める (純粋な判断。環境や設定は引数で受け取る)。</summary>
public static class UiLanguages
{
    /// <summary>言語を強制する環境変数 (<c>ja</c> / <c>en</c>)。自動テストと動作確認用で、設定より優先する。プラグインホストにも渡す。</summary>
    public const string EnvVar = "KOROMOGAE_LANG";

    /// <summary>
    /// 使う言語 (<see cref="UiLanguage.Japanese"/> か <see cref="UiLanguage.English"/>) を決める。優先順位:
    /// ① 環境変数 (<c>ja</c> / <c>en</c>。読めない値は無いものとみなす) → ② 設定が日本語か英語ならそれ → ③ 自動なら Windows の表示言語 (日本語以外は英語)。
    /// </summary>
    public static UiLanguage Resolve(UiLanguage setting, string? envValue, string? windowsUiLanguage)
    {
        if (TryParse(envValue, out var fromEnv)) return fromEnv;
        if (setting is UiLanguage.Japanese or UiLanguage.English) return setting;
        return StartsWith(windowsUiLanguage, "ja") ? UiLanguage.Japanese : UiLanguage.English;
    }

    /// <summary><c>ja</c> / <c>ja-JP</c> / <c>en</c> / <c>en-US</c> を読む。それ以外 (空・未知の言語) は false。</summary>
    public static bool TryParse(string? text, out UiLanguage language)
    {
        string t = (text ?? "").Trim();
        if (StartsWith(t, "ja")) { language = UiLanguage.Japanese; return true; }
        if (StartsWith(t, "en")) { language = UiLanguage.English; return true; }
        language = UiLanguage.Auto;
        return false;
    }

    /// <summary>決まった言語 (日本語か英語) のカルチャ。</summary>
    public static CultureInfo ToCulture(UiLanguage resolved) => resolved == UiLanguage.English ? Loc.English : Loc.Japanese;

    /// <summary>環境変数に渡す短い名前 (<c>ja</c> / <c>en</c>)。</summary>
    public static string ToCode(UiLanguage resolved) => resolved == UiLanguage.English ? "en" : "ja";

    static bool StartsWith(string? text, string prefix)
        => text != null && text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
           && (text.Length == prefix.Length || text[prefix.Length] is '-' or '_');
}
