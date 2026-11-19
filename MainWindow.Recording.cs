using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using Koromogae.Audio;
using Koromogae.Ui;

namespace Koromogae
{
    /// <summary>録音 (加工後の音と、任意で加工前の音を WAV に残す)。録音そのものは AudioEngine / Recorder が行い、ここは画面の操作と表示。</summary>
    public partial class MainWindow
    {
        bool _recordingShown;   // 録音中として画面を出しているか (録音が自分で止まったことを見つけるため)

        /// <summary>録音を始める/止める。</summary>
        void RecordButton_Click(object sender, RoutedEventArgs e) => ToggleRecording();

        /// <summary>録音を始める/止める。画面の「録音」ボタンと、トレイのメニューが共有する (設定は画面の部品の値)。</summary>
        void ToggleRecording()
        {
            if (_engine.IsRecording) StopRecordingUi();
            else StartRecordingUi();
        }

        void StartRecordingUi()
        {
            if (!_engine.IsRunning) return;
            string folder = SelectedRecordFolder ?? AppSettings.DefaultRecordFolder;
            try
            {
                if (folder.Length > AppSettings.MaxPathLength) throw new ArgumentException(Loc.T("Rec.Error.PathTooLong"));
                folder = System.IO.Path.GetFullPath(folder);   // 使えない文字などは、ここで例外になる
                _engine.StartRecording(folder, SelectedRecordDepth, SelectedRecordDry, SelectedRecordFormat, _flacLevel);
            }
            catch (Exception ex)
            {
                AppLog.Error($"録音を始められません: {folder}", ex);
                NotifyUser(Loc.F("Rec.Error.CannotStart", ex.Message, folder), MessageBoxImage.Warning);
                return;
            }

            _recordingShown = true;
            SetRecordingUi(true);
            AppLog.Info($"録音を開始: {_engine.RecordingStatus?.WetPath} ({RecordSpec()})");
            UpdateRecordingStatus();
        }

        /// <summary>録音を終えて、結果を表示する。録音が自分で止まった (書き込みの失敗など) ときも、同じ経路で終える。</summary>
        void StopRecordingUi()
        {
            var status = _engine.StopRecording();
            _recordingShown = false;
            SetRecordingUi(false);
            if (status == null) return;

            string files = status.DryPath != null ? $"{status.WetPath}\n{status.DryPath}" : status.WetPath;
            if (status.Error != null)
            {
                AppLog.Error($"{status.Error} ({status.WetPath})");
                RecordStatusText.Text = status.Error;
                NotifyUser(Loc.F("Rec.Error.Stopped", status.Error, files), MessageBoxImage.Warning);
            }
            else
            {
                AppLog.Info($"録音を終了: {status.WetPath} ({RecordingText.FormatSize(status.WetBytes)}), 欠落 {status.DroppedMs:F0} ms");
                RecordStatusText.Text = Loc.F("Rec.Saved", status.WetPath)
                    + (status.DryPath != null ? Loc.F("Rec.Saved.Dry", System.IO.Path.GetFileName(status.DryPath)) : "")
                    + (status.DroppedMs > 0 ? Loc.F("Rec.Saved.Dropped", status.DroppedMs) : "");
            }
        }

        /// <summary>録音の操作部の有効・無効と見た目。録音中は、保存先・ビット深度・加工前の録音を変更できない。</summary>
        void SetRecordingUi(bool recording)
        {
            RecordButton.Content = recording ? Loc.T("Rec.ButtonStop") : Loc.T("Rec.Button");
            RecordButton.Foreground = recording ? ClipBrush : Brushes.Black;
            RecordButton.IsEnabled = _engine.IsRunning;
            foreach (var control in LockedWhileRecording) control.IsEnabled = !recording;
            UpdateRecordOptionsEnabled(recording);
            UpdateDitherEnabled(_engine.IsRunning);
        }

        IEnumerable<System.Windows.Controls.Control> LockedWhileRecording
            => new System.Windows.Controls.Control[] { RecordFolderBox, RecordBrowseButton, RecordDepthCombo, RecordFormatCombo, RecordDryCheck };

        /// <summary>録音ファイルの形式 (サンプルレート・チャンネル数・ビット深度・ディザ)。動作中はこれらを変更できないので、画面の選択がそのまま録音の形式。</summary>
        string RecordSpec() => RecordingText.Spec(SelectedRate, SelectedRecordDepth, SelectedDither, SelectedRecordFormat, _flacLevel);

        /// <summary>動作中で録音していないとき、これから録音される形式を予告する (ビット深度やディザ方式を変えるとすぐ更新)。</summary>
        void UpdateRecordReadyText()
        {
            if (_engine.IsRunning && !_engine.IsRecording) RecordStatusText.Text = RecordingText.Ready(RecordSpec());
        }

        /// <summary>録音中の経過時間とサイズを更新し、録音が自分で止まっていれば終える (約 0.5 秒ごと)。</summary>
        void UpdateRecordingStatus()
        {
            if (_recordingShown && !_engine.IsRecording)
            {
                StopRecordingUi();    // 書き込みの失敗・ディスクの空き不足などで、録音だけが止まった
                return;
            }
            if (_engine.RecordingStatus is { IsRecording: true } s)
                RecordStatusText.Text = RecordingText.Format(s.Elapsed, s.WetBytes, s.DryBytes, s.DryPath != null, s.LowDisk, RecordSpec());
        }

        void RecordBrowse_Click(object sender, RoutedEventArgs e)
        {
            string current = SelectedRecordFolder ?? AppSettings.DefaultRecordFolder;
            var dialog = new OpenFolderDialog { Title = Loc.T("Dialog.PickRecordFolder.Title") };
            if (System.IO.Directory.Exists(current)) dialog.InitialDirectory = current;
            if (dialog.ShowDialog(this) == true) RecordFolderBox.Text = dialog.FolderName;
        }
    }
}