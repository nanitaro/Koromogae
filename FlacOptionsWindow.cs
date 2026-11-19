using System.Windows;
using System.Windows.Controls;

namespace Koromogae
{
    /// <summary>「圧縮オプション...」: FLAC の圧縮レベル (0〜8) を選ぶ小さなダイアログ。</summary>
    public sealed class FlacOptionsWindow : Window
    {
        readonly ComboBox _levelCombo = new() { Name = "FlacLevelCombo", MinWidth = 330, Margin = new Thickness(0, 4, 0, 12) };

        /// <summary>選んだ圧縮レベル (OK で閉じたとき)。</summary>
        public int Level => _levelCombo.SelectedValue is int level ? level : AppSettings.DefaultFlacLevel;

        /// <summary>レベルごとの説明 (画面の言語)。公式の flac コマンドのレベルに合わせた内容 (-0〜-8)。</summary>
        public static string[] Descriptions => Enumerable.Range(0, 9).Select(i => Loc.T($"Flac.Level.{i}")).ToArray();

        /// <summary>選択肢。ToString が表示名なので、スクリーンリーダーや UI オートメーションにも表示名で伝わる。</summary>
        sealed record LevelItem(int Level, string Label)
        {
            public override string ToString() => Label;
        }

        public FlacOptionsWindow(int level)
        {
            Title = Loc.T("Flac.Title");
            SizeToContent = SizeToContent.WidthAndHeight;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            _levelCombo.ItemsSource = Descriptions.Select((text, i) => new LevelItem(i, text)).ToList();
            _levelCombo.DisplayMemberPath = nameof(LevelItem.Label);
            _levelCombo.SelectedValuePath = nameof(LevelItem.Level);
            _levelCombo.SelectedValue = Math.Clamp(level, AppSettings.MinFlacLevel, AppSettings.MaxFlacLevel);
            System.Windows.Automation.AutomationProperties.SetAutomationId(_levelCombo, "FlacLevelCombo");

            var ok = new Button { Content = "OK", IsDefault = true, Width = 80, Margin = new Thickness(0, 0, 8, 0) };
            var cancel = new Button { Content = Loc.T("Flac.Cancel"), IsCancel = true, Width = 80 };
            System.Windows.Automation.AutomationProperties.SetAutomationId(ok, "FlacOkButton");
            System.Windows.Automation.AutomationProperties.SetAutomationId(cancel, "FlacCancelButton");
            ok.Click += (_, _) => DialogResult = true;

            var note = new TextBlock
            {
                Text = Loc.T("Flac.Note"),
                Margin = new Thickness(0, 0, 0, 8),
                TextWrapping = TextWrapping.Wrap,
            };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);

            var panel = new StackPanel { Margin = new Thickness(16) };
            panel.Children.Add(new TextBlock { Text = Loc.T("Flac.LevelLabel") });
            panel.Children.Add(_levelCombo);
            panel.Children.Add(note);
            panel.Children.Add(buttons);
            Content = panel;
        }
    }
}