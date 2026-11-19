using System.Windows;
using System.Windows.Controls;
using Koromogae.Audio;
using Koromogae.Ui;

namespace Koromogae
{
    /// <summary>画面の設定項目 (コンボボックス) と、設定ファイル (AppSettings) との対応。</summary>
    public partial class MainWindow
    {
        ApiKind SelectedApi => ApiCombo.SelectedValue is ApiKind api ? api : AppSettings.DefaultApi;
        int SelectedRate => RateCombo.SelectedItem is int rate ? rate : AppSettings.DefaultSampleRate;
        int SelectedLatency => LatencyCombo.SelectedItem is int ms ? ms : AppSettings.DefaultLatencyMs;
        int SelectedCushionExtra => CushionCombo.SelectedValue is int ms ? ms : AppSettings.DefaultCushionExtraMs;
        FormatChoice SelectedInputFormat => InputFormatCombo.SelectedValue is FormatChoice f ? f : AppSettings.DefaultFormat;
        FormatChoice SelectedOutputFormat => OutputFormatCombo.SelectedValue is FormatChoice f ? f : AppSettings.DefaultFormat;
        DitherMode SelectedDither => DitherCombo.SelectedValue is DitherMode m ? m : AppSettings.DefaultDitherMode;
        RecordDepth SelectedRecordDepth => RecordDepthCombo.SelectedValue is RecordDepth d ? d : AppSettings.DefaultRecordDepth;
        bool SelectedRecordDry => RecordDryCheck.IsChecked == true;
        RecordFormat SelectedRecordFormat => RecordFormatCombo.SelectedValue is RecordFormat f ? f : AppSettings.DefaultRecordFormat;
        int _flacLevel = AppSettings.DefaultFlacLevel;     // 「圧縮オプション...」で選んだ FLAC の圧縮レベル
        UiLanguage _language = AppSettings.DefaultLanguage; // 「Language」メニューで選んだ言語 (再起動で反映)
        bool _minimizeToTray = AppSettings.DefaultMinimizeToTray;   // 「オプション」メニューで選んだ、最小化でトレイへ格納するか
        /// <summary>画面の保存先を、保存する値にしたもの (空・既定と同じなら null)。</summary>
        string? SelectedRecordFolder => SettingsForm.NormalizeRecordFolder(RecordFolderBox.Text, AppSettings.DefaultRecordFolder);

        /// <summary>コンボボックスの選択肢 (値 + 表示名)。ToString が表示名なので、スクリーンリーダーや UI オートメーションにも表示名で伝わる。</summary>
        sealed record ComboItem<T>(T Value, string Label)
        {
            public override string ToString() => Label;
        }

        /// <summary>録音のビット深度の選択肢。FLAC のとき Float32 は選べない (Enabled = false)。</summary>
        sealed record DepthItem(RecordDepth Value, string Label, bool Enabled)
        {
            public override string ToString() => Label;
        }

        /// <summary>ビット深度の選択肢を作り直す (形式によって選べるものが変わる)。選んでいた値はそのまま (選べなくなったものは Int24)。</summary>
        void RebuildDepthItems(RecordFormat format)
        {
            var keep = RecordFormatRules.CoerceDepth(format, SelectedRecordDepth);
            RecordDepthCombo.ItemsSource = new[]
            {
                new DepthItem(RecordDepth.Float32, "Float32", RecordFormatRules.AllowsDepth(format, RecordDepth.Float32)),
                new DepthItem(RecordDepth.Int24, "Int24", true),
                new DepthItem(RecordDepth.Int16, "Int16", true),
            };
            RecordDepthCombo.DisplayMemberPath = nameof(DepthItem.Label);
            RecordDepthCombo.SelectedValuePath = nameof(DepthItem.Value);
            RecordDepthCombo.SelectedValue = keep;
        }

        /// <summary>コンボボックスに「値 + 表示名」の一覧を設定する。選択は SelectedValue (値) で扱う。</summary>
        static void BindItems<T>(ComboBox combo, IEnumerable<ComboItem<T>> items)
        {
            combo.ItemsSource = items.ToList();
            combo.DisplayMemberPath = nameof(ComboItem<T>.Label);
            combo.SelectedValuePath = nameof(ComboItem<T>.Value);
        }

        static ComboItem<T> Item<T>(T value, string label) => new(value, label);

        /// <summary>各コンボボックスの選択肢を設定する (選択はまだしない)。</summary>
        void InitSettingsCombos()
        {
            BindItems(ApiCombo, Enum.GetValues<ApiKind>().Select(k => Item(k, AudioDevices.Label(k))));
            RateCombo.ItemsSource = AppSettings.AllowedSampleRates;
            LatencyCombo.ItemsSource = AppSettings.AllowedLatenciesMs;
            BindItems(CushionCombo, AppSettings.AllowedCushionExtrasMs.Select(ms => Item(ms, ms == 0 ? Loc.T("Main.Cushion.Default") : $"+{ms}")));
            var formats = Enum.GetValues<FormatChoice>().Select(f => Item(f, f == FormatChoice.Auto ? Loc.T("Main.Format.Auto") : f.ToString())).ToList();
            BindItems(InputFormatCombo, formats);
            BindItems(OutputFormatCombo, formats);
            BindItems(RecordFormatCombo, new[]
            {
                Item(RecordFormat.Wav, "WAV"),
                Item(RecordFormat.Flac, "FLAC"),
            });
            RebuildDepthItems(RecordFormat.Wav);
            BindItems(DitherCombo, new[]
            {
                Item(DitherMode.Tpdf, "TPDF"),
                Item(DitherMode.HpTpdf, "HP-TPDF"),
                Item(DitherMode.Shaped, Loc.T("Dither.Shaped")),
                Item(DitherMode.Off, Loc.T("Dither.Off")),
            });
        }

        /// <summary>設定を画面に反映する。値は AppSettings.Load で検証・補正済み。デバイスは別 (RefreshDevices)。</summary>
        void ApplySettings(AppSettings settings)
        {
            var form = SettingsForm.From(settings);
            ApiCombo.SelectedValue = form.Api;
            RateCombo.SelectedItem = form.SampleRate;
            LatencyCombo.SelectedItem = form.LatencyMs;
            CushionCombo.SelectedValue = form.CushionExtraMs;
            InputFormatCombo.SelectedValue = form.InputFormat;
            OutputFormatCombo.SelectedValue = form.OutputFormat;
            DitherCombo.SelectedValue = form.DitherMode;
            RecordFolderBox.Text = form.RecordFolder ?? AppSettings.DefaultRecordFolder;
            RecordFormatCombo.SelectedValue = form.RecordFormat;
            _flacLevel = form.FlacLevel;
            _language = form.Language;
            RebuildDepthItems(form.RecordFormat);
            RecordDepthCombo.SelectedValue = RecordFormatRules.CoerceDepth(form.RecordFormat, form.RecordDepth);
            UpdateRecordOptionsEnabled(recording: false);
            RecordDryCheck.IsChecked = form.RecordDry;
            UpdateLanguageChecks();
            _minimizeToTray = form.MinimizeToTray;
            MinimizeToTrayMenuItem.IsChecked = _minimizeToTray;
        }

        /// <summary>いま画面で選ばれている設定項目の値 (デバイスは名前)。</summary>
        SettingsForm ReadForm() => new(
            SelectedApi,
            (InputCombo.SelectedItem as AudioDevice)?.Name,
            (OutputCombo.SelectedItem as AudioDevice)?.Name,
            SelectedRate, SelectedLatency, SelectedCushionExtra,
            SelectedInputFormat, SelectedOutputFormat, SelectedDither,
            SelectedRecordFolder, SelectedRecordDepth, SelectedRecordDry, SelectedRecordFormat, _flacLevel, _language, _minimizeToTray);

        /// <summary>画面の設定と、チェーンのプラグイン (状態つき) から、保存する設定を作る。</summary>
        AppSettings CaptureSettings()
        {
            var settings = new AppSettings();
            ReadForm().ApplyTo(settings);
            foreach (var slot in _engine.Chain.Slots)
            {
                string? state = null;
                try
                {
                    byte[]? data = slot.Effect.GetState();
                    if (data != null && data.Length > AppSettings.MaxStateBytes)
                        AppLog.Warn($"{slot.Effect.Name} の状態データが大きすぎるため保存しません ({data.Length:N0} バイト)");
                    else if (data != null)
                        state = Convert.ToBase64String(data);
                }
                catch (Exception ex)
                {
                    AppLog.Error($"{slot.Effect.Name} の状態を取得できません", ex);
                }
                settings.Plugins.Add(new PluginSetting { Path = slot.Effect.Path, Enabled = slot.Enabled, State = state });
            }
            settings.Plugins.AddRange(_unloaded);
            return settings;
        }

        void UpdateDitherEnabled(bool running)
        {
            DitherCombo.IsEnabled = SettingsForm.DitherEnabled(running, SelectedOutputFormat, SelectedRecordDepth);
            UpdateDitherTooltip();
        }

        /// <summary>
        /// ディザ方式のツールチップ: 固定の説明 + 「いまの設定でディザが働くのは」の 1 行 (再生と録音のどちらで働くか)。
        /// 動作中は、再生は実際に開いた形式で判定する。
        /// </summary>
        void UpdateDitherTooltip()
        {
            if (DitherCombo == null || DitherLabel == null) return;   // 画面の初期化中
            var opened = _engine.OutputFormatInUse;
            string status = DitherStatus.Describe(opened ?? SelectedOutputFormat, opened != null, SelectedRecordDepth, _engine.IsRecording);
            string text = Loc.T("Tip.Dither") + "\n" + status;
            DitherCombo.ToolTip = text;
            DitherLabel.ToolTip = text;
        }

        void OutputFormatCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
            => UpdateDitherEnabled(_engine.IsRunning);

        void RecordFormatCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (RecordDepthCombo == null || RecordOptionsButton == null) return;     // 画面の初期化中
            RebuildDepthItems(SelectedRecordFormat);        // FLAC に切り替えたとき、Float32 は Int24 になる
            UpdateRecordOptionsEnabled(_engine.IsRecording);
            UpdateDitherEnabled(_engine.IsRunning);
            UpdateRecordReadyText();
        }

        /// <summary>「圧縮オプション...」は FLAC のときだけ使える。録音中は変更できない。</summary>
        void UpdateRecordOptionsEnabled(bool recording)
        {
            RecordOptionsButton.IsEnabled = SelectedRecordFormat == RecordFormat.Flac && !recording;
        }

        void RecordOptions_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new FlacOptionsWindow(_flacLevel) { Owner = this };
            if (dialog.ShowDialog() == true)
            {
                _flacLevel = dialog.Level;
                UpdateRecordReadyText();
            }
        }

        void RecordDepthCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (RecordDepthCombo.SelectedValue == null) return;     // 選択肢の作り直し中
            UpdateDitherEnabled(_engine.IsRunning);
            UpdateRecordReadyText();
        }

        void DitherCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateRecordReadyText();

        /// <summary>動作中は変更できない設定項目 (デバイスも含む)。ディザは出力の形式にもよるので別に扱う (UpdateDitherEnabled)。</summary>
        IEnumerable<Control> LockedWhileRunning => new Control[]
        {
            ApiCombo, InputCombo, OutputCombo, RateCombo, LatencyCombo, CushionCombo,
            InputFormatCombo, OutputFormatCombo, RefreshButton,
        };
    }
}
