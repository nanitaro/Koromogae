namespace Koromogae.Ui;

/// <summary>
/// 起動時の引数。<c>--minimized</c> は最小化 (トレイ) で起動、<c>--start</c> は起動したら自動で再生を始める。
/// 2 つは独立で、両方でもよい。大文字小文字は区別せず、先頭は <c>--</c> <c>-</c> <c>/</c> のどれでもよい。
/// 知らない引数は無視して、<see cref="Unknown"/> に返す (起動は妨げない)。WPF に依存しない (単体テストでソースごとリンクしている)。
/// </summary>
public sealed record StartupArgs(bool Minimized, bool Start, IReadOnlyList<string> Unknown)
{
    public const string MinimizedName = "minimized";
    public const string StartName = "start";

    public static StartupArgs Parse(IEnumerable<string>? args)
    {
        bool minimized = false, start = false;
        var unknown = new List<string>();
        foreach (string arg in args ?? Array.Empty<string>())
        {
            string name = Normalize(arg);
            if (name == MinimizedName) minimized = true;
            else if (name == StartName) start = true;
            else if (arg.Length > 0) unknown.Add(arg);
        }
        return new StartupArgs(minimized, start, unknown);
    }

    /// <summary>先頭の <c>--</c> <c>-</c> <c>/</c> を外して、小文字にする。</summary>
    static string Normalize(string arg)
    {
        string s = arg.Trim();
        if (s.StartsWith("--", StringComparison.Ordinal)) s = s[2..];
        else if (s.StartsWith('-') || s.StartsWith('/')) s = s[1..];
        else return "";                                  // 先頭の記号が無いものは、引数として扱わない
        return s.ToLowerInvariant();
    }
}