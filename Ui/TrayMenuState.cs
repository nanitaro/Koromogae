namespace Koromogae.Ui;

/// <summary>トレイのアイコンの右クリックメニューの、文言 (resx のキー) と有効・無効。メニューを開くたびに、再生と録音の状態から決める。</summary>
/// <param name="PlayKey">再生の項目。動作中は「停止」、そうでなければ「開始」。</param>
/// <param name="RecordKey">録音の項目。録音中は「停止」、そうでなければ「開始」。</param>
/// <param name="RecordEnabled">録音の項目を選べるか。再生中だけ (録音は動作を始めた後にしか始められない)。録音中は止められるので、動作中なら常に true。</param>
/// <param name="TipKey">ツールチップに添える状態。</param>
public readonly record struct TrayMenuState(string PlayKey, string RecordKey, bool RecordEnabled, string TipKey)
{
    public const string RestoreKey = "Tray.Restore";
    public const string ExitKey = "Tray.Exit";

    /// <summary>「元に戻す」と「終了」は、いつでも選べる。</summary>
    public static bool RestoreEnabled => true;
    public static bool ExitEnabled => true;

    /// <param name="running">再生 (エンジン) が動作中か。</param>
    /// <param name="recording">録音中か。</param>
    public static TrayMenuState For(bool running, bool recording) => new(
        PlayKey: running ? "Tray.Play.Stop" : "Tray.Play.Start",
        RecordKey: recording ? "Tray.Rec.Stop" : "Tray.Rec.Start",
        RecordEnabled: running,
        TipKey: recording ? "Tray.Tip.Recording" : running ? "Tray.Tip.Running" : "Tray.Tip.Stopped");
}
