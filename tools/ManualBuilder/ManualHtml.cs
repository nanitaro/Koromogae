using System.Text;
using System.Text.RegularExpressions;
using Markdig;
using Markdig.Extensions.AutoIdentifiers;

namespace ManualBuilder;

/// <summary>
/// README (Markdown) を、配布物に付ける 1 ファイルの HTML マニュアルにする。純粋な関数 (ファイルは引数の関数で読む) なので、<c>Settings.Tests</c> がソースをリンクして検査する。
/// 規則: ① 「開発者向け」の節 (と目次の行) を外す ② 言語切り替えのリンクを、相手の HTML へ向ける ③ <c>docs/images/…</c> の画像を base64 で埋め込む (外部ファイルなし)
/// ④ 見出しの ID は GitHub 流 (README の目次のリンクが、そのまま効く) ⑤ CSS はインライン。スクリプトなし。
/// </summary>
public static class ManualHtml
{
    /// <summary>「開発者向け」(日本語) と「For developers」(英語) の見出し。この節は、次の <c>## </c> の手前まで外す。</summary>
    static readonly string[] DeveloperHeadings = { "開発者向け", "For developers" };   // i18n:internal (README の見出しと照合する文字列)

    static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseAutoIdentifiers(AutoIdentifierOptions.GitHub)
        .Build();

    /// <param name="markdown">README の全文。</param>
    /// <param name="language"><c>ja</c> か <c>en</c>。<c>lang</c> 属性と、言語切り替えのリンク先を決める。</param>
    /// <param name="readFile">リポジトリの直下からの相対パス (<c>docs/images/main.png</c>) を、バイト列にして返す。読めなければ null。</param>
    public static string Convert(string markdown, string language, Func<string, byte[]?> readFile)
    {
        string md = markdown.Replace("\r\n", "\n");
        md = RemoveDeveloperSection(md);
        // README への言及 (リンクも、文中の「see README.en.md」も) は、配布物では対応する HTML を指す
        md = md.Replace("README.en.md", "manual.en.html").Replace("README.md", "manual.ja.html");

        string body = Markdown.ToHtml(md, Pipeline);
        body = EmbedImages(body, readFile);

        string title = Regex.Match(md, @"^# (.+)$", RegexOptions.Multiline) is { Success: true } m ? m.Groups[1].Value.Trim() : "Koromogae";
        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html>\n<html lang=\"").Append(language).Append("\">\n<head>\n<meta charset=\"utf-8\">\n");
        sb.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n");
        sb.Append("<title>").Append(System.Net.WebUtility.HtmlEncode(title)).Append("</title>\n<style>\n").Append(Css).Append("</style>\n</head>\n<body>\n<main>\n");
        sb.Append(body).Append("</main>\n</body>\n</html>\n");
        return sb.ToString();
    }

    /// <summary>「開発者向け」の節 (見出しから、次の <c>## </c> の手前まで) と、目次の <c>- […](#…)</c> の行を外す。見出しはコードブロックの中の <c>#</c> と混同しないよう、行頭の <c>## </c> だけを見る。</summary>
    public static string RemoveDeveloperSection(string md)
    {
        var lines = md.Split('\n').ToList();
        var result = new List<string>(lines.Count);
        bool skipping = false, inFence = false;
        foreach (string line in lines)
        {
            if (line.StartsWith("```")) inFence = !inFence;
            if (!inFence && line.StartsWith("## "))
                skipping = DeveloperHeadings.Contains(line[3..].Trim());
            if (skipping) continue;
            // 目次の行: `- [開発者向け](#開発者向け)` / `- [For developers](#for-developers)`
            if (DeveloperHeadings.Any(h => Regex.IsMatch(line, @"^\s*-\s*\[" + Regex.Escape(h) + @"\]\(#"))) continue;
            result.Add(line);
        }
        return string.Join("\n", result);
    }

    static string EmbedImages(string html, Func<string, byte[]?> readFile)
        => Regex.Replace(html, "src=\"(docs/images/[^\"]+)\"", m =>
        {
            byte[]? bytes = readFile(m.Groups[1].Value);
            if (bytes == null) throw new FileNotFoundException("画像が見つかりません: " + m.Groups[1].Value);   // i18n:internal (ビルド用ツールのメッセージ)
            string mime = m.Groups[1].Value.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg";
            return $"src=\"data:{mime};base64,{System.Convert.ToBase64String(bytes)}\"";
        });

    const string Css = """
        :root { color-scheme: light dark; }
        body { margin: 0; font-family: "Segoe UI", "Yu Gothic UI", Meiryo, sans-serif; line-height: 1.7; color: #1f2328; background: #fff; }
        main { max-width: 860px; margin: 0 auto; padding: 24px 20px 64px; }
        h1, h2, h3 { line-height: 1.3; }
        h1 { font-size: 2em; border-bottom: 1px solid #d0d7de; padding-bottom: .3em; }
        h2 { font-size: 1.5em; border-bottom: 1px solid #d0d7de; padding-bottom: .3em; margin-top: 2em; }
        h3 { font-size: 1.2em; margin-top: 1.6em; }
        a { color: #0969da; }
        img { max-width: 100%; height: auto; border: 1px solid #d0d7de; border-radius: 4px; }
        table { border-collapse: collapse; margin: 1em 0; display: block; overflow-x: auto; }
        th, td { border: 1px solid #d0d7de; padding: 6px 12px; text-align: left; vertical-align: top; }
        th { background: #f6f8fa; }
        code { font-family: Consolas, "MS Gothic", monospace; background: #f6f8fa; padding: .15em .35em; border-radius: 4px; font-size: 90%; }
        pre { background: #f6f8fa; padding: 12px 16px; overflow-x: auto; border-radius: 6px; }
        pre code { background: none; padding: 0; font-size: 90%; }
        blockquote { margin: 1em 0; padding: 0 1em; color: #59636e; border-left: 4px solid #d0d7de; }
        @media (prefers-color-scheme: dark) {
          body { color: #e6edf3; background: #0d1117; }
          h1, h2, img, th, td { border-color: #3d444d; }
          th, code, pre { background: #161b22; }
          a { color: #4493f8; }
          blockquote { color: #9198a1; border-color: #3d444d; }
        }

        """;
}
