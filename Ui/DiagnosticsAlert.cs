using Koromogae.Audio;

namespace Koromogae.Ui;

/// <summary>
/// 診断の行を赤くする条件と、例外・停止を app.log へ 1 回だけ残す判断。
/// 時刻は引数で受け取る (画面のタイマーに依存しないので、単体テストで固定できる)。UI スレッドだけが使う。
/// 音飛びは増えた直後だけ赤。コールバックの停止は止まっている間だけ赤。例外の回数は累計なので、一度でも起きると、動作を止めるまで赤のまま。
/// 録音の欠落は録音 1 回ぶんの値で、録音を止めても残る (エンジンが次の開始まで録音の状態を持つ) ので、次の録音を始めるか、動作を止めるまで赤のまま。
/// このファイルは WPF に依存しない (単体テストでソースごとリンクしている)。
/// </summary>
public sealed class DiagnosticsAlert
{
    /// <summary>音飛びが増えてから、赤くしておく時間。</summary>
    public static readonly TimeSpan UnderrunFlash = TimeSpan.FromSeconds(3);

    [Flags]
    public enum Faults
    {
        None = 0,
        HandlerErrors = 1,
        InputStall = 2,
        OutputStall = 4,
        RecordDrop = 8,
    }

    long _lastUnderruns;
    DateTime _flashUntil;
    bool _fault;
    Faults _logged;

    /// <summary>診断を更新した (約 0.5 秒ごと)。音飛びの回数が増えていれば、いまから <see cref="UnderrunFlash"/> の間は赤。</summary>
    public void Update(DiagnosticsInfo diagnostics, DateTime now)
    {
        if (diagnostics.Underruns > _lastUnderruns) _flashUntil = now + UnderrunFlash;
        _lastUnderruns = diagnostics.Underruns;
        _fault = diagnostics.HasFault;
    }

    /// <summary>動作中でなくなった。回数の基準と、例外・停止の記録済みの印を戻す (赤い表示は、時間が過ぎるまで残る)。</summary>
    public void Reset()
    {
        _lastUnderruns = 0;
        _fault = false;
        _logged = Faults.None;
    }

    /// <summary>診断の行を赤くするか (約 80ms ごとの画面の更新で呼ぶ)。</summary>
    public bool IsAlert(DateTime now) => now < _flashUntil || _fault;

    /// <summary>
    /// 今回はじめて見つかった例外・停止を返す (動作中の間、それぞれ 1 回だけ)。呼び出し側はこれをログに書く。
    /// 画面の更新ごとに書かないため。
    /// </summary>
    public Faults TakeNewFaults(DiagnosticsInfo diagnostics)
    {
        var current = Faults.None;
        if (diagnostics.HandlerErrors > 0) current |= Faults.HandlerErrors;
        if (diagnostics.InputStalledMs > 0) current |= Faults.InputStall;
        if (diagnostics.OutputStalledMs > 0) current |= Faults.OutputStall;
        if (diagnostics.RecordDroppedMs > 0) current |= Faults.RecordDrop;

        var added = current & ~_logged;
        _logged |= added;
        return added;
    }
}
