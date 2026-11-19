using System.Globalization;
using System.Resources;

[assembly: NeutralResourcesLanguage("ja")]

namespace Koromogae;

/// <summary>
/// 画面に出す文字列の表 (<c>Resources/Strings.resx</c> = 日本語、<c>Strings.en.resx</c> = 英語) を引く。
/// 型付きの Designer クラスは使わない (CLI のビルドでは生成されない)。<see cref="ResourceManager"/> を包むだけの、WPF にも音声ライブラリにも依存しないクラスで、
/// 単体テストがソースごとリンクして使う (リンク先では <c>RootNamespace</c> を Koromogae にして、リソース名をそろえる)。
/// ログ (app.log) は日本語のままなので、画面とログの両方に使う文字列は、ログ側が <see cref="Japanese"/> を渡して引く。
/// </summary>
public static class Loc
{
    public static readonly CultureInfo Japanese = CultureInfo.GetCultureInfo("ja");
    public static readonly CultureInfo English = CultureInfo.GetCultureInfo("en");

    static readonly ResourceManager Manager = new("Koromogae.Resources.Strings", typeof(Loc).Assembly);
    static readonly AsyncLocal<CultureInfo?> Scoped = new();
    static volatile CultureInfo _current = Japanese;

    /// <summary>いまの画面の言語。<see cref="Override"/> の範囲内ならそれ (その範囲の非同期処理にも引き継ぐ。並列に走るテストが干渉しない)。</summary>
    public static CultureInfo Culture => Scoped.Value ?? _current;

    /// <summary>画面の言語を決める (起動の最初の 1 回。再起動で反映するので、実行中は変えない)。</summary>
    public static void SetCulture(CultureInfo culture) => _current = culture;

    /// <summary>範囲内だけ言語を差し替える。<c>using</c> で使う (テスト用)。</summary>
    public static IDisposable Override(CultureInfo culture)
    {
        var previous = Scoped.Value;
        Scoped.Value = culture;
        return new Restore(previous);
    }

    /// <summary>キーの文字列を引く。無いキーは、気づけるようにキー名をそのまま返す。</summary>
    public static string T(string key) => T(key, Culture);

    public static string T(string key, CultureInfo? culture) => Manager.GetString(key, culture ?? Culture) ?? key;

    /// <summary>書式つきの文字列 (<c>{0}</c> など) を引いて、その言語の書式で埋める。</summary>
    public static string F(string key, params object?[] args) => F(Culture, key, args);

    public static string F(CultureInfo? culture, string key, params object?[] args)
    {
        culture ??= Culture;
        return string.Format(culture, T(key, culture), args);
    }

    /// <summary>表にあるキーか (テスト用)。</summary>
    public static bool Has(string key, CultureInfo culture) => Manager.GetString(key, culture) != null;

    sealed class Restore : IDisposable
    {
        readonly CultureInfo? _previous;
        public Restore(CultureInfo? previous) => _previous = previous;
        public void Dispose() => Scoped.Value = _previous;
    }
}
