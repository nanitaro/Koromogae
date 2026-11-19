using System.Globalization;

namespace Koromogae.Ui;

/// <summary>バージョン情報の画面と起動ログに出す内容 (収集した値の入れ物)。値の収集は <c>AppInfo</c> が行う。</summary>
public sealed record AppVersionReport(
    string Version,
    string Runtime,
    string Os,
    string Architecture,
    string PortAudio,
    string? Host32Version,
    string? Host64Version,
    IReadOnlyCollection<string> X86Runtimes);

/// <summary>
/// バージョン情報の文言の組み立て (版の整形・起動ログの 1 行・ホストの版の一致・x86 版 .NET の有無の診断)。
/// 値は引数で受け取り、ファイル・レジストリ・時刻には触れない。
/// このファイルは WPF に依存しない (単体テストでソースごとリンクしている)。
/// </summary>
public static class AppVersionInfo
{
    public const string ProductName = "Koromogae";

    /// <summary>プラグインホストが必要とする .NET のメジャー版 (<c>VstPluginHost.runtimeconfig.json</c> の net10.0)。</summary>
    public const int RequiredRuntimeMajor = 10;

    /// <summary>版文字列から、<c>+</c> 以降のビルドメタデータ (コミット ID など) を除く。空・null は「不明」。ログに使うときは、日本語のカルチャを渡す。</summary>
    public static string CleanVersion(string? version, CultureInfo? culture = null)
    {
        if (string.IsNullOrWhiteSpace(version)) return Loc.T("Ver.Unknown", culture);
        string v = version.Trim();
        int plus = v.IndexOf('+');
        return plus >= 0 ? v[..plus] : v;
    }

    /// <summary>起動時に app.log へ残す 1 行 (ログは日本語のまま)。</summary>
    public static string StartupLine(string version, string runtime, string os, string architecture)
        => $"起動: {ProductName} {CleanVersion(version, Loc.Japanese)} / {runtime} {architecture} / {os}";   // i18n:log

    /// <summary>プラグインホストの版を、本体の版と比べた 1 行。ホストが見つからなければ (null) その旨を出す。</summary>
    public static string HostStatus(string label, string? hostVersion, string appVersion)
    {
        if (hostVersion == null) return Loc.F("Ver.HostNotFound", label);
        string host = CleanVersion(hostVersion), app = CleanVersion(appVersion);
        return host == app
            ? Loc.F("Ver.HostMatch", label, host)
            : Loc.F("Ver.HostMismatch", label, host, app);
    }

    /// <summary>
    /// x86 版 .NET (32bit プラグインのホストが使う) の診断。<paramref name="installed"/> は、インストール済みの共有ランタイムの版 (フォルダ名)。
    /// 32bit ホストが動くには、<see cref="RequiredRuntimeMajor"/>.x が必要 (別のメジャー版には切り替わらない)。
    /// </summary>
    public static string X86RuntimeStatus(IReadOnlyCollection<string> installed)
    {
        string need = Loc.F("Ver.X86Need", RequiredRuntimeMajor);
        if (installed.Count == 0) return Loc.F("Ver.X86None", need);

        var usable = installed.Where(v => MajorOf(v) == RequiredRuntimeMajor).OrderByDescending(SortKey).ToList();
        if (usable.Count > 0) return Loc.F("Ver.X86Ok", usable[0]);

        return Loc.F("Ver.X86Wrong", string.Join(", ", installed.OrderBy(v => v, StringComparer.Ordinal)), RequiredRuntimeMajor, need);
    }

    /// <summary>バージョン情報の画面に出す (クリップボードにもコピーする) テキスト全体。</summary>
    public static string Report(AppVersionReport r)
    {
        string version = CleanVersion(r.Version);
        return string.Join(Environment.NewLine, new[]
        {
            $"{ProductName} {version}",
            $".NET: {r.Runtime} ({r.Architecture})",
            $"OS: {r.Os}",
            $"PortAudio: {r.PortAudio}",
            Loc.T("Ver.Hosts"),
            "  " + HostStatus("32bit", r.Host32Version, version),
            "  " + HostStatus("64bit", r.Host64Version, version),
            "  " + X86RuntimeStatus(r.X86Runtimes),
        });
    }

    static int MajorOf(string version)
    {
        string head = version.Split('.', '-', '+')[0];
        return int.TryParse(head, out int major) ? major : -1;
    }

    /// <summary>「10.0.9」より「10.0.15」が新しいように、数字として並べる (プレリリースの接尾辞は無視)。</summary>
    static Version SortKey(string version)
    {
        string core = version.Split('-', '+')[0];
        return System.Version.TryParse(core, out var parsed) ? parsed : new Version(0, 0);
    }
}