using System.Globalization;
using System.IO;

namespace Koromogae.Ui;

/// <summary>
/// 「ヘルプ > マニュアルを開く」で開く HTML のマニュアル (配布物の <c>manual.ja.html</c> / <c>manual.en.html</c>。本体と同じフォルダ) を探す。
/// 画面の言語のものを優先し、無ければもう一方の言語のものを使う (英語の画面で英語版が無くても、日本語版が読める)。どちらも無ければ null。
/// ファイルの有無は引数の関数で受け取り、ディスクには触れない。このファイルは WPF に依存しない (単体テストでソースごとリンクしている)。
/// </summary>
public static class ManualLocator
{
    /// <summary>言語 (<c>ja</c> / それ以外は <c>en</c>) のマニュアルのファイル名。</summary>
    public static string FileName(string language) => language == "ja" ? "manual.ja.html" : "manual.en.html";

    /// <param name="baseDirectory">本体 (Koromogae.exe) のフォルダ。</param>
    /// <param name="culture">画面の言語。日本語 (<c>ja</c>) 以外は英語として扱う (翻訳のない言語は、画面も英語になるため)。</param>
    /// <param name="exists">ファイルがあるか。</param>
    public static string? Resolve(string baseDirectory, CultureInfo culture, Func<string, bool> exists)
    {
        string first = culture.TwoLetterISOLanguageName == "ja" ? "ja" : "en";
        string second = first == "ja" ? "en" : "ja";
        foreach (string language in new[] { first, second })
        {
            string path = Path.Combine(baseDirectory, FileName(language));
            if (exists(path)) return path;
        }
        return null;
    }
}