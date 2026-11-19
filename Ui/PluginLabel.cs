namespace Koromogae.Ui;

/// <summary>
/// プラグイン一覧に出す 1 行の文言: <c>名前  [VST3 / 64bit]</c>。ホストが止まっているときは末尾に「※プラグインホスト停止」を付ける。
/// このファイルは WPF にも VstCore にも依存しない (単体テストでソースごとリンクしている)。
/// </summary>
public static class PluginLabel
{
    public static string Format(string name, bool isVst3, bool is32bit, bool hostStopped)
    {
        string kind = isVst3 ? "VST3" : "VST2";
        string bits = is32bit ? "32bit" : "64bit";
        string state = hostStopped ? Loc.T("Plugin.HostStopped") : "";
        return $"{name}  [{kind} / {bits}]{state}";
    }
}