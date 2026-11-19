using System.Globalization;
using System.Resources;

[assembly: NeutralResourcesLanguage("ja")]

namespace VstCore;

/// <summary>
/// VstCore が利用者に見せるメッセージ (プラグインのロード失敗・ホストの起動失敗・エディタの「プリセット」メニュー) の文字列の表。
/// 言語は環境変数 <c>KOROMOGAE_LANG</c> (<c>ja</c> / <c>en</c>。本体が起動時に設定し、プラグインホストが引き継ぐ) で決める。無ければ日本語。
/// プロトコルの検証の文面やログは内部の診断なので、ここには置かない (日本語のまま)。
/// </summary>
public static class CoreLoc
{
    public const string LanguageEnvVar = "KOROMOGAE_LANG";

    static readonly ResourceManager Manager = new("VstCore.Resources.CoreStrings", typeof(CoreLoc).Assembly);
    static readonly Lazy<CultureInfo> FromEnvironment = new(() =>
        CultureInfo.GetCultureInfo(Environment.GetEnvironmentVariable(LanguageEnvVar)?.Trim().StartsWith("en", StringComparison.OrdinalIgnoreCase) == true ? "en" : "ja"));

    /// <summary>いまの言語。テストは差し替えられる。</summary>
    public static CultureInfo Culture { get; set; } = null!;

    public static string T(string key) => Manager.GetString(key, Culture ?? FromEnvironment.Value) ?? key;

    public static string F(string key, params object?[] args)
        => string.Format(Culture ?? FromEnvironment.Value, T(key), args);
}