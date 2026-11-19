using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using ManualBuilder;
using Xunit;

namespace Settings.Tests;

/// <summary>
/// 配布物に付ける説明書の検査: 短いテキスト (dist\README*.txt) と、README から作る HTML マニュアル (tools\ManualBuilder の <see cref="ManualHtml"/>)。
/// ソースの場所は csproj の AssemblyMetadata (RepoDir)。ビルドが出力フォルダへ HTML を作ることは、ビルドの手順 (Koromogae.csproj の BuildManuals) で確かめる。
/// </summary>
public class ReleaseDocsTests
{
    static readonly string RepoDir = typeof(ReleaseDocsTests).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>().First(a => a.Key == "RepoDir").Value!;

    static readonly Regex Cjk = new(@"[\u3040-\u30FF\u4E00-\u9FFF\uFF00-\uFFEF]");

    static string ReadRepo(string relative) => File.ReadAllText(Path.Combine(RepoDir, relative), Encoding.UTF8);

    static byte[]? ReadImage(string relative)
    {
        string path = Path.Combine(RepoDir, relative.Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    static string Manual(string readme, string language) => ManualHtml.Convert(ReadRepo(readme), language, ReadImage);

    // ---- 製品名 (旧名が残っていないこと) ----

    [Fact]
    public void The_old_product_name_is_not_left_in_the_sources_or_the_docs()
    {
        // 旧名をそのまま書くと、このファイル自身に当たるので、つなげて作る
        string[] oldNames = { "VST" + "_Output", "VST" + " Output", "VST" + "_OUTPUT" };
        string[] skipped = { "bin", "obj", ".git", ".vs", ".claude" };
        string[] patterns = { "*.cs", "*.xaml", "*.resx", "*.csproj", "*.props", "*.slnx", "*.md", "*.txt", "*.json", "*.manifest" };

        var hits = patterns
            .SelectMany(p => Directory.EnumerateFiles(RepoDir, p, SearchOption.AllDirectories))
            .Where(f => !Path.GetRelativePath(RepoDir, f).Split(Path.DirectorySeparatorChar).Any(s => skipped.Contains(s)))
            .Where(f => Path.GetFileName(f) != "CLAUDE.md")                         // 設計の記録 (移行の経緯に旧名が残る)
            .Where(f => oldNames.Any(n => File.ReadAllText(f, Encoding.UTF8).Contains(n, StringComparison.Ordinal)))
            .Select(f => Path.GetRelativePath(RepoDir, f))
            .ToList();

        Assert.True(hits.Count == 0, "旧名が残っています: " + string.Join(", ", hits));
        Assert.False(File.Exists(Path.Combine(RepoDir, oldNames[0] + ".csproj")));   // ファイル名も変えてある
        Assert.False(File.Exists(Path.Combine(RepoDir, oldNames[0] + ".slnx")));
    }

    // ---- HTML マニュアル ----

    [Theory]
    [InlineData("README.md", "ja")]
    [InlineData("README.en.md", "en")]
    public void The_manual_is_one_self_contained_file_with_the_images_embedded(string readme, string language)
    {
        string html = Manual(readme, language);

        Assert.Equal(4, Regex.Matches(html, "src=\"data:image/png;base64,").Count);   // main-plugins / main / parameters / about
        Assert.DoesNotContain("src=\"docs/", html);                                   // 画像のファイルを別に持たない
        Assert.DoesNotMatch("(?i)<script", html);                                     // スクリプトなし
        Assert.DoesNotMatch(@"(?i)(href|src)=""https?://[^""]*\.(css|js)", html);     // 外部の CSS / JS なし
        Assert.Contains($"<html lang=\"{language}\">", html);
        Assert.Contains("<meta charset=\"utf-8\">", html);
        Assert.Contains("<style>", html);
    }

    [Theory]
    [InlineData("README.md", "ja")]
    [InlineData("README.en.md", "en")]
    public void Every_table_of_contents_link_points_to_a_heading_in_the_same_page(string readme, string language)
    {
        string html = Manual(readme, language);

        var ids = Regex.Matches(html, "id=\"([^\"]+)\"").Select(m => m.Groups[1].Value).ToHashSet();
        // 日本語の見出しへのリンクは、Markdig が % でエンコードする (ブラウザはデコードして ID と照合する)
        var links = Regex.Matches(html, "href=\"#([^\"]+)\"").Select(m => Uri.UnescapeDataString(m.Groups[1].Value)).ToList();

        Assert.NotEmpty(links);
        Assert.Empty(links.Where(l => !ids.Contains(l)));
    }

    [Theory]
    [InlineData("README.md", "ja", "開発者向け")]
    [InlineData("README.en.md", "en", "For developers")]
    public void The_developer_section_and_its_table_of_contents_line_are_left_out(string readme, string language, string heading)
    {
        string source = ReadRepo(readme);
        string html = ManualHtml.Convert(source, language, ReadImage);

        Assert.Contains(heading, source);                 // README には、ある
        Assert.DoesNotContain(heading, html);             // マニュアルには、ない (見出しも目次も)
        Assert.DoesNotContain("dotnet build", html);      // 節の中身 (ビルドの手順) も
        Assert.Contains(language == "ja" ? "ライセンス" : "License", html);   // 後ろの節は残る
    }

    [Fact]
    public void The_language_switch_points_to_the_other_manual()
    {
        Assert.Contains("href=\"manual.en.html\"", Manual("README.md", "ja"));
        Assert.Contains("href=\"manual.ja.html\"", Manual("README.en.md", "en"));
        Assert.DoesNotContain("README", Manual("README.md", "ja"));       // 文中の言及も含めて、配布物にない README を指さない
        Assert.DoesNotContain("README", Manual("README.en.md", "en"));
    }

    [Fact]
    public void Removing_the_developer_section_keeps_the_sections_around_it_and_ignores_headings_inside_code_blocks()
    {
        string md = "## 使い方\nA\n\n## 開発者向け\nB\n```\n## 次の節に見える行\n```\nC\n\n## ライセンス\nD\n";

        string result = ManualHtml.RemoveDeveloperSection(md.Replace("\r\n", "\n"));

        Assert.Contains("## 使い方", result);
        Assert.Contains("## ライセンス", result);
        Assert.DoesNotContain("B", result.Replace("A", ""));
        Assert.DoesNotContain("開発者向け", result);
        Assert.DoesNotContain("次の節に見える行", result);   // コードブロックの中の ## は、節の終わりとみなさない
    }

    [Fact]
    public void A_missing_image_stops_the_conversion_instead_of_leaving_a_broken_link()
    {
        Assert.Throws<FileNotFoundException>(() =>
            ManualHtml.Convert("# T\n<img src=\"docs/images/none.png\">\n", "ja", _ => null));
    }

    // ---- 短いテキスト (dist\README*.txt) ----

    /// <summary>メモ帳で折り返さずに読める幅 (全角を 2 桁で数える)。</summary>
    static int DisplayWidth(string line) => line.Sum(c => c > 0x2E80 || c == '■' ? 2 : 1);

    [Theory]
    [InlineData(@"dist\README.txt", true)]
    [InlineData(@"dist\README.en.txt", false)]
    public void The_short_readme_is_notepad_friendly_and_in_the_right_language(string relative, bool japanese)
    {
        string path = Path.Combine(RepoDir, relative);
        byte[] bytes = File.ReadAllBytes(path);
        string text = Encoding.UTF8.GetString(bytes);

        Assert.True(bytes.Length > 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "UTF-8 の BOM が必要です (メモ帳が文字コードを誤認しないため)");
        Assert.Equal(0, Regex.Matches(text, "(?<!\r)\n").Count);            // 行末は CRLF だけ
        Assert.Equal(japanese, Cjk.IsMatch(text));                          // 英語版に日本語が混ざらない (日本語版には日本語がある)
        var lines = text.Split("\r\n");
        Assert.All(lines, l => Assert.True(DisplayWidth(l) <= 80, $"80 桁を超えています ({DisplayWidth(l)}): {l}"));
        Assert.InRange(lines.Length, 30, 90);                               // 「1 画面ぶん」に収まる長さ
    }

    [Theory]
    [InlineData(@"dist\README.txt", "manual.ja.html", "README.en.txt")]
    [InlineData(@"dist\README.en.txt", "manual.en.html", "README.txt")]
    public void The_short_readme_points_to_the_manual_the_license_and_the_other_language(string relative, string manual, string other)
    {
        string text = ReadRepo(relative);

        Assert.Contains(manual, text);                       // 詳しい説明
        Assert.Contains(other, text);                        // 相手の言語の短い説明
        Assert.Contains("LICENSE", text);
        Assert.Contains("THIRD-PARTY-NOTICES.md", text);
        Assert.Contains("portable", text);                   // ポータブルモード
        Assert.Contains("Koromogae.exe", text);
        Assert.DoesNotMatch(@"\b\d+\.\d+\.\d+\b", text.Replace("10.0", ""));   // 版番号を書かない (更新を忘れるため)
    }

    [Fact]
    public void The_build_copies_the_short_readmes_and_makes_both_manuals_incrementally()
    {
        string csproj = ReadRepo("Koromogae.csproj");

        Assert.Contains(@"dist\README.txt", csproj);
        Assert.Contains(@"dist\README.en.txt", csproj);
        Assert.Contains("Name=\"BuildManuals\"", csproj);
        Assert.Contains("manual.ja.html", csproj);
        Assert.Contains("manual.en.html", csproj);
        Assert.Contains("Inputs=", csproj);                  // 増分 (README か画像かツールが変わったときだけ作り直す)
        Assert.Contains(@"tools\**", csproj);                // ツールのソースを本体に取り込まない
    }

    // ---- ライセンス・商標の表示 (第三者の通知と、VST の帰属文) ----

    const string VstAttribution = "VST is a registered trademark of Steinberg Media Technologies GmbH.";

    [Fact]
    public void The_notices_list_the_copyright_holders_of_the_bundled_portaudio_windows_sources()
    {
        string notices = ReadRepo("THIRD-PARTY-NOTICES.md");

        // 同梱 DLL と同じリビジョンの公式ソースの、ファイルごとの表示で確かめた著作権者 (雛形の <author(s)> だけでは足りない)
        foreach (string holder in new[] { "Ross Bencina", "Phil Burk", "David Viens", "Dmitry Kostjuchenko", "Andrew Baldwin", "Robert Marsanyi" })
            Assert.Contains(holder, notices);
        Assert.Contains("147dd722548358763a8b649b3e4b41dfffbcfbb6", notices);      // どのリビジョンで確認したか
        foreach (string api in new[] { "WASAPI", "WDM-KS", "DirectSound", "MME" })
            Assert.Contains(api, notices);
    }

    [Fact]
    public void The_notices_carry_the_dotnet_foundation_mit_notice_for_the_apphost_executables()
    {
        string notices = ReadRepo("THIRD-PARTY-NOTICES.md");

        Assert.Contains("Copyright (c) .NET Foundation and Contributors", notices);
        Assert.Contains("Permission is hereby granted, free of charge", notices);
        Assert.Contains("apphost", notices);
        foreach (string exe in new[] { "Koromogae.exe", @"PluginHost64\VstPluginHost.exe", @"PluginHost32\VstPluginHost.exe" })
            Assert.Contains(exe, notices);                                          // 通知が対象にする実行ファイルを、すべて挙げている
        Assert.Contains(".NET Library License", notices);                           // ランタイム (同梱しない) のライセンスを、MIT だけと書かない
    }

    [Fact]
    public void The_notices_state_the_vst_trademark_and_that_no_vst_sdk_file_or_logo_is_included()
    {
        string notices = ReadRepo("THIRD-PARTY-NOTICES.md");

        Assert.Contains(VstAttribution, notices);
        Assert.Contains("not affiliated with", notices);
    }

    [Theory]
    [InlineData("README.md")]
    [InlineData("README.en.md")]
    [InlineData("THIRD-PARTY-NOTICES.md")]
    public void The_docs_never_say_just_VST_SDK(string relative)
    {
        Assert.DoesNotMatch(@"(?<![A-Za-z0-9])VST SDK", ReadRepo(relative));       // 「VST SDK」とだけ書かない (VST2 SDK と VST3 SDK は別物)
    }

    [Fact]
    public void The_notices_distinguish_the_vst2_sdk_and_the_vst3_sdk()
    {
        string notices = ReadRepo("THIRD-PARTY-NOTICES.md");

        Assert.Contains("VST2 SDK", notices);
        Assert.Contains("VST3 SDK", notices);
    }

    [Theory]
    [InlineData("README.md")]
    [InlineData("README.en.md")]
    [InlineData(@"dist\README.txt")]
    [InlineData(@"dist\README.en.txt")]
    public void The_docs_do_not_state_a_legal_uncertainty_about_vst2(string relative)
    {
        string text = ReadRepo(relative);

        Assert.DoesNotContain("法的な不確実性", text);                              // 所有者の判断で、文書には書かない
        Assert.DoesNotContain("legal uncertainty", text, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("README.md", "ja")]
    [InlineData("README.en.md", "en")]
    public void The_html_manuals_do_not_state_a_legal_uncertainty_about_vst2(string readme, string language)
    {
        string html = Manual(readme, language);

        Assert.DoesNotContain("法的な不確実性", html);
        Assert.DoesNotContain("legal uncertainty", html, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("README.md")]
    [InlineData("README.en.md")]
    [InlineData(@"dist\README.txt")]
    [InlineData(@"dist\README.en.txt")]
    public void Each_document_marks_the_first_use_of_vst_with_the_registered_symbol_once_and_gives_the_attribution(string relative)
    {
        string text = ReadRepo(relative);

        Assert.Equal(1, Regex.Matches(text, "VST®").Count);                         // ® は初出の 1 回だけ
        // 初出 = 見出し (製品名「Koromogae」。製品名の一部に ® を付けると、製品名そのものが登録商標に見える) の次の、最初の VST
        string body = text[(text.IndexOf('\n') + 1)..];
        Assert.StartsWith("VST®", body[body.IndexOf("VST", StringComparison.Ordinal)..]);        Assert.Contains(VstAttribution, Regex.Replace(text, @"\s+", " "));          // 帰属文 (改行で折り返していても、文として含まれる)
    }

    [Fact]
    public void The_html_manuals_keep_the_trademark_notice_and_the_registered_symbol()
    {
        foreach (var (readme, language) in new[] { ("README.md", "ja"), ("README.en.md", "en") })
        {
            string html = Manual(readme, language);

            Assert.Contains(VstAttribution, html);
            Assert.Contains("VST®", html);
            Assert.Equal(4, Regex.Matches(html, "src=\"data:image/png;base64,").Count);       // 埋め込む画像は docs/images の 4 枚だけ (公式ロゴは含めない)
        }
    }

    [Theory]
    [InlineData("README.md")]
    [InlineData("README.en.md")]
    [InlineData(@"dist\README.txt")]
    [InlineData(@"dist\README.en.txt")]
    public void The_requirements_link_to_the_downloads_and_do_not_name_a_year_range_for_the_vc_runtime(string relative)
    {
        string text = ReadRepo(relative);

        Assert.Contains("https://dotnet.microsoft.com/download/dotnet/10.0", text);                       // .NET の配布先
        Assert.Contains("https://learn.microsoft.com/cpp/windows/latest-supported-vc-redist", text);       // VC++ 再頒布可能パッケージの配布先
        Assert.DoesNotMatch(@"2015\s*[-–]\s*20\d\d", text);                                                // 年の範囲は、今後変わるので書かない
    }

    [Theory]
    [InlineData("README.md", "Microsoft Visual C++ 再頒布可能パッケージ")]
    [InlineData("README.en.md", "Microsoft Visual C++ Redistributable")]
    [InlineData(@"dist\README.txt", "Microsoft Visual C++ 再頒布可能パッケージ")]
    [InlineData(@"dist\README.en.txt", "Microsoft Visual C++ Redistributable")]
    public void The_requirements_name_the_visual_cpp_runtime_that_the_bundled_portaudio_dll_needs(string relative, string name)
    {
        // portaudio.dll は vcruntime140.dll を使う (同梱していない)。必要環境の節にあること
        string text = ReadRepo(relative);

        int requirements = Math.Max(text.IndexOf("## 動作環境", StringComparison.Ordinal), Math.Max(text.IndexOf("## Requirements", StringComparison.Ordinal), 0));
        Assert.Contains(name, text[requirements..]);
        Assert.Contains("vcruntime140.dll", ReadRepo("README.md"));
        Assert.Contains("portaudio.dll", text);
    }
}