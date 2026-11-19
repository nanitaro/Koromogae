using System.Windows.Media;
using Koromogae.Audio;
using Koromogae.Ui;

namespace Koromogae
{
    /// <summary>
    /// 入出力メーター、遅延の表示、音飛びなどの診断の表示 (約 80ms ごとのタイマー)。
    /// 赤くする条件や保持時間は <see cref="LevelMeter"/> / <see cref="DiagnosticsAlert"/> が決める (ここは部品に反映するだけ)。
    /// </summary>
    public partial class MainWindow
    {
        static readonly Brush NormalBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x06, 0xB0, 0x25)));
        static readonly Brush ClipBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xE0, 0x20, 0x20)));

        static Brush Frozen(SolidColorBrush brush)
        {
            brush.Freeze();
            return brush;
        }

        readonly LevelMeter _inputMeter = LevelMeter.ForInput();
        readonly LevelMeter _outputMeter = LevelMeter.ForOutput();
        readonly DiagnosticsAlert _alert = new();
        int _latencyTick;

        void Timer_Tick(object? sender, EventArgs e)
        {
            var now = DateTime.UtcNow;
            _inputMeter.Update(_engine.TakeInputPeak(), now);
            _outputMeter.Update(_engine.TakeOutputPeak(), now);
            InputMeter.Value = _inputMeter.Level;
            OutputMeter.Value = _outputMeter.Level;
            InputMeter.Foreground = _inputMeter.IsClipping(now) ? ClipBrush : NormalBrush;
            OutputMeter.Foreground = _outputMeter.IsClipping(now) ? ClipBrush : NormalBrush;

            foreach (var slot in _engine.Chain.Slots)
                if (slot.Effect.IsFaulted) slot.RefreshDisplay();

            if (++_latencyTick % 6 == 0)    // 約 0.5 秒ごと (内部バッファは平均値なので、頻繁に更新しても意味がない)
            {
                UpdateLatencyText();
                UpdateRecordingStatus();
            }

            DiagnosticsText.Foreground = _alert.IsAlert(now) ? ClipBrush : Brushes.Black;
        }

        void UpdateLatencyText()
        {
            LatencyText.Text = _engine.GetLatency() is { } latency ? latency.Format() : Loc.T("Play.Latency.Initial");

            var stats = _engine.IsRunning ? _engine.GetStats() : null;
            if (stats == null)
            {
                DiagnosticsText.Text = Loc.T("Play.Diag.Initial");
                _alert.Reset();
                return;
            }

            var diagnostics = stats.ToDiagnostics();
            DiagnosticsText.Text = diagnostics.Format();
            _alert.Update(diagnostics, DateTime.UtcNow);
            LogNewFaults(stats, diagnostics);
        }

        /// <summary>例外・停止は始まったときに 1 回だけ app.log へ残す (画面の更新ごとに書かない)。止まったまま終了しても記録が残る。</summary>
        void LogNewFaults(AudioStats stats, DiagnosticsInfo diagnostics)
        {
            var faults = _alert.TakeNewFaults(diagnostics);
            if (faults.HasFlag(DiagnosticsAlert.Faults.HandlerErrors))
            {
                string? first = stats.Input.FirstError ?? stats.Output.FirstError;
                AppLog.Warn($"コールバックの中で例外が起きました: {first} ({_engine.LogDescription})");
            }
            if (faults.HasFlag(DiagnosticsAlert.Faults.InputStall))
                AppLog.Warn($"入力のコールバックが止まっています ({diagnostics.InputStalledMs / 1000:F1} 秒): {_engine.LogDescription}");
            if (faults.HasFlag(DiagnosticsAlert.Faults.RecordDrop))
                AppLog.Warn($"録音の書き込みが追いつかず、音を捨てました ({diagnostics.RecordDroppedMs:F0} ms): {_engine.RecordingStatus?.WetPath}");
            if (faults.HasFlag(DiagnosticsAlert.Faults.OutputStall))
                AppLog.Warn($"出力のコールバックが止まっています ({diagnostics.OutputStalledMs / 1000:F1} 秒): {_engine.LogDescription}");
        }
    }
}
