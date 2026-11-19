using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace Settings.Tests;

/// <summary>
/// 多言語対応の「抜け」を機械的に見つける検査: 文字列の表 (resx) の整合、コードが参照するキーの存在、使われていないキー、
/// 製品コードに日本語の文字列リテラルが残っていないこと。ソースを走査するので、リポジトリの場所は csproj の AssemblyMetadata (RepoDir) で受け取る。
/// </summary>
public class LocalizationCoverageTests
{
    static readonly string RepoDir = typeof(LocalizationCoverageTests).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>().First(a => a.Key == "RepoDir").Value!;

    static readonly Regex Cjk = new(@"[぀-ヿ一-鿿＀-￯]");
    static readonly Regex Placeholder = new(@"\{(\d+)(?:[:,][^}]*)?\}");

    /// <summary>文字列の表 (表の名前 → キー → 値)。<c>Strings</c> = 本体、<c>CoreStrings</c> = VstCore。</summary>
    static readonly (string Name, string Dir)[] Tables =
    {
        ("Strings", "Resources"),
        ("CoreStrings", @"VstCore\Resources"),
    };

    static Dictionary<string, string> ReadResx(string dir, string file)
    {
        var doc = XDocument.Load(Path.Combine(RepoDir, dir, file));
        return doc.Root!.Elements("data").ToDictionary(d => (string)d.Attribute("name")!, d => (string)d.Element("value")!);
    }

    public static IEnumerable<object[]> TableNames() => Tables.Select(t => new object[] { t.Name });

    static (Dictionary<string, string> Ja, Dictionary<string, string> En) Load(string table)
    {
        var (name, dir) = Tables.First(t => t.Name == table);
        return (ReadResx(dir, name + ".resx"), ReadResx(dir, name + ".en.resx"));
    }

    // ---- 表の整合 ----

    [Theory, MemberData(nameof(TableNames))]
    public void Every_key_exists_in_both_languages_and_is_not_empty(string table)
    {
        var (ja, en) = Load(table);

        Assert.Empty(ja.Keys.Except(en.Keys));   // 英語に無いキー
        Assert.Empty(en.Keys.Except(ja.Keys));   // 日本語に無いキー
        Assert.All(ja, kv => Assert.False(string.IsNullOrWhiteSpace(kv.Value), $"{kv.Key} (ja) is empty"));
        Assert.All(en, kv => Assert.False(string.IsNullOrWhiteSpace(kv.Value), $"{kv.Key} (en) is empty"));
        Assert.NotEmpty(ja);
    }

    [Theory, MemberData(nameof(TableNames))]
    public void Placeholders_match_between_the_languages(string table)
    {
        var (ja, en) = Load(table);

        // 食い違うと、実行時に FormatException になるか、引数が表示されない
        foreach (var (key, jaText) in ja)
        {
            var jaSet = Placeholder.Matches(jaText).Select(m => m.Groups[1].Value).Distinct().Order().ToList();
            var enSet = Placeholder.Matches(en[key]).Select(m => m.Groups[1].Value).Distinct().Order().ToList();
            Assert.True(jaSet.SequenceEqual(enSet), $"{key}: placeholders differ (ja: {string.Join(",", jaSet)} / en: {string.Join(",", enSet)})");
        }
    }

    [Theory, MemberData(nameof(TableNames))]
    public void English_is_really_translated(string table)
    {
        var (ja, en) = Load(table);

        foreach (var (key, enText) in en)
        {
            Assert.False(Cjk.IsMatch(enText), $"{key}: the English text contains Japanese characters");
            // 日本語の文字を含まない値 (数字だけなど) は、同じでよい。含むのに同じなら、訳し忘れ
            if (Cjk.IsMatch(ja[key])) Assert.NotEqual(ja[key], enText);
        }
    }

    [Fact]
    public void The_satellite_assembly_serves_the_english_text()
    {
        // 表のファイルだけでなく、実際に組み込まれた表 (衛星アセンブリ) から引けること
        Assert.Equal(ReadResx("Resources", "Strings.en.resx")["Play.Start"], Koromogae.Loc.T("Play.Start", Koromogae.Loc.English));
        Assert.Equal(ReadResx("Resources", "Strings.resx")["Play.Start"], Koromogae.Loc.T("Play.Start", Koromogae.Loc.Japanese));
        Assert.NotEqual(Koromogae.Loc.T("Play.Start", Koromogae.Loc.Japanese), Koromogae.Loc.T("Play.Start", Koromogae.Loc.English));
    }

    // ---- コードとの対応 ----

    /// <summary>キーを組み立てて引く所 (リテラルが無い) の、キーの先頭。</summary>
    static readonly string[] DynamicPrefixes = { "Flac.Level." };

    static readonly Regex KeyLike = new(@"""([A-Z][A-Za-z0-9]*(?:\.[A-Za-z0-9]+)+)""");
    static readonly Regex XamlKey = new(@"\{local:T ([A-Za-z0-9.]+)\}");

    static IEnumerable<string> ProductionFiles(params string[] patterns)
    {
        string[] skip = { @"\obj\", @"\bin\", @"\tests\", @"\Resources\" };
        return patterns.SelectMany(p => Directory.EnumerateFiles(RepoDir, p, SearchOption.AllDirectories))
            .Where(f => !skip.Any(s => f.Contains(s, StringComparison.OrdinalIgnoreCase)));
    }

    static string Read(string file) => File.ReadAllText(file, Encoding.UTF8);

    [Fact]
    public void Every_key_the_code_refers_to_exists()
    {
        var main = ReadResx("Resources", "Strings.resx").Keys.ToHashSet();
        var core = ReadResx(@"VstCore\Resources", "CoreStrings.resx").Keys.ToHashSet();

        var direct = new Regex(@"(?<loc>Core)?Loc\.(?:T|F)\((?:[^""()]*,\s*)?""(?<key>[A-Za-z0-9.]+)""");
        var missing = new List<string>();
        foreach (string file in ProductionFiles("*.cs", "*.xaml"))
        {
            string text = Read(file);
            foreach (Match m in direct.Matches(text))
                if (!(m.Groups["loc"].Success ? core : main).Contains(m.Groups["key"].Value))
                    missing.Add($"{Path.GetFileName(file)}: {m.Groups["key"].Value}");
            foreach (Match m in XamlKey.Matches(text))
                if (!main.Contains(m.Groups[1].Value)) missing.Add($"{Path.GetFileName(file)}: {m.Groups[1].Value}");
        }

        Assert.True(missing.Count == 0, "\n" + string.Join("\n", missing));
    }

    [Fact]
    public void Every_key_is_used_somewhere()
    {
        var used = new HashSet<string>();
        foreach (string file in ProductionFiles("*.cs", "*.xaml"))
        {
            string text = Read(file);
            foreach (Match m in KeyLike.Matches(text)) used.Add(m.Groups[1].Value);
            foreach (Match m in XamlKey.Matches(text)) used.Add(m.Groups[1].Value);
        }

        var unused = Tables.SelectMany(t => ReadResx(t.Dir, t.Name + ".resx").Keys)
            .Where(k => !used.Contains(k) && !DynamicPrefixes.Any(k.StartsWith))
            .ToList();

        Assert.True(unused.Count == 0, "\n" + string.Join("\n", unused));
    }

    // ---- 製品コードに日本語の文字列が残っていない ----

    /// <summary>
    /// この印が付いた行は、日本語のままでよい (ログや内部の診断。ログは画面の言語にかかわらず日本語に保つ)。
    /// <c>AppLog.</c> / <c>HostLog.</c> の呼び出しの行も同じ。<c>HostProtocol.</c> の呼び出しの日本語は、プロトコルの項目名 (内部の診断)。
    /// <c>i18n:fixed</c> は、どの言語の画面でも読めるよう、あえて固定した表記。<c>i18n:log-begin</c> 〜 <c>i18n:log-end</c> の範囲は、まとめてログ用。
    /// </summary>
    static readonly string[] AllowedLineMarkers = { "i18n:log", "i18n:internal", "i18n:fixed", "AppLog.", "HostLog.", "HostProtocol." };

    /// <summary>内部の診断だけで、利用者の画面には出ないファイル (プロトコルの検証の文面・ホスト側の診断)。</summary>
    static readonly string[] InternalFiles = { "HostProtocol.cs", "PluginHostServer.cs", "HostLog.cs", "HostShutdown.cs" };

    /// <summary>コメントを除いた行を返す (文字列の中の <c>//</c> は残す)。</summary>
    static IEnumerable<(int Line, string Code)> CodeLines(string text)
    {
        bool inBlockComment = false;
        int lineNo = 0;
        foreach (string raw in text.Split('\n'))
        {
            lineNo++;
            var sb = new StringBuilder();
            bool inString = false, verbatim = false;
            for (int i = 0; i < raw.Length; i++)
            {
                char c = raw[i], next = i + 1 < raw.Length ? raw[i + 1] : '\0';
                if (inBlockComment)
                {
                    if (c == '*' && next == '/') { inBlockComment = false; i++; }
                    continue;
                }
                if (inString)
                {
                    sb.Append(c);
                    if (verbatim ? c == '"' && next == '"' : c == '\\') { sb.Append(next); i++; }
                    else if (c == '"') inString = false;
                    continue;
                }
                if (c == '/' && next == '/') break;
                if (c == '/' && next == '*') { inBlockComment = true; i++; continue; }
                if (c == '"')
                {
                    inString = true;
                    verbatim = i > 0 && (raw[i - 1] == '@' || (i > 1 && raw[i - 1] == '$' && raw[i - 2] == '@') || (i > 1 && raw[i - 1] == '@' && raw[i - 2] == '$'));
                }
                sb.Append(c);
            }
            yield return (lineNo, sb.ToString());
        }
    }

    [Fact]
    public void Production_code_has_no_hard_coded_japanese_text()
    {
        var found = new List<string>();
        foreach (string file in ProductionFiles("*.cs"))
        {
            string name = Path.GetFileName(file);
            if (InternalFiles.Contains(name)) continue;
            string text = Read(file);
            string[] originals = text.Split('\n');   // 印はコメントの中にあるので、元の行で調べる
            bool inLogRegion = false;
            foreach (var (line, code) in CodeLines(text))
            {
                string original = originals[line - 1];
                if (original.Contains("i18n:log-begin", StringComparison.Ordinal)) inLogRegion = true;
                if (original.Contains("i18n:log-end", StringComparison.Ordinal)) inLogRegion = false;
                if (inLogRegion || !Cjk.IsMatch(code)) continue;
                if (AllowedLineMarkers.Any(m => original.Contains(m, StringComparison.Ordinal))) continue;
                found.Add($"{name}:{line}: {code.Trim()}");
            }
        }

        Assert.True(found.Count == 0, "\n" + string.Join("\n", found));
    }

    [Fact]
    public void XAML_has_no_hard_coded_japanese_text_except_the_fixed_language_menu()
    {
        var found = new List<string>();
        foreach (string file in ProductionFiles("*.xaml"))
        {
            string text = Regex.Replace(Read(file), @"<!--.*?-->", "", RegexOptions.Singleline);
            foreach (string line in text.Split('\n'))
            {
                if (!Cjk.IsMatch(line)) continue;
                if (line.Contains("Tag=\"Auto\"") || line.Contains("Tag=\"Japanese\"")) continue;   // 「Language」メニューの固定の表記
                found.Add($"{Path.GetFileName(file)}: {line.Trim()}");
            }
        }

        Assert.True(found.Count == 0, "\n" + string.Join("\n", found));
    }
}
