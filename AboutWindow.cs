using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Koromogae.Ui;

namespace Koromogae
{
    /// <summary>
    /// 「ヘルプ > バージョン情報」: 版番号・環境・プラグインホストの版・ライセンスを出す小さなダイアログ。
    /// 文言は <see cref="AppVersionInfo"/> が組み立て、ここは部品に並べるだけ。
    /// </summary>
    public sealed class AboutWindow : Window
    {
        /// <summary>VST の商標の帰属文 (Steinberg の VST Usage Guidelines の文面)。</summary>
        public const string TrademarkNotice = "VST is a registered trademark of Steinberg Media Technologies GmbH.";

        readonly string _report;
        readonly TextBlock _copyStatus = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };

        public AboutWindow(AppVersionReport report)
        {
            Title = Loc.T("About.Title");
            Width = 660;
            Height = 620;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            AutomationProperties.SetAutomationId(this, "AboutWindow");

            _report = AppVersionInfo.Report(report);

            var heading = new TextBlock
            {
                Text = $"{AppVersionInfo.ProductName} {AppVersionInfo.CleanVersion(report.Version)}",
                FontSize = 22,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
            };
            AutomationProperties.SetAutomationId(heading, "AboutHeading");

            // 見出しの左にアプリのアイコン (App.ico の最大のフレームを縮小して、細部をきれいに出す)
            var headerRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            if (LoadLargestIcon() is { } icon)
            {
                var image = new Image { Source = icon, Width = 64, Height = 64, Margin = new Thickness(0, 0, 12, 0) };
                RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
                AutomationProperties.SetAutomationId(image, "AboutIcon");
                headerRow.Children.Add(image);
            }
            headerRow.Children.Add(heading);

            var reportBox = ReadOnlyBox(_report, "AboutReportText");
            reportBox.Height = double.NaN;
            reportBox.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;

            // VST の商標の帰属文 (Steinberg の VST Usage Guidelines §15 の文。ガイドラインの文言なので、翻訳しない固定の英文)
            var trademark = new TextBlock
            {
                Text = TrademarkNotice,
                FontSize = 11,
                Opacity = 0.75,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 6, 0, 0),
            };
            AutomationProperties.SetAutomationId(trademark, "AboutTrademark");

            var tabs = new TabControl { Margin = new Thickness(0, 12, 0, 0) };
            tabs.Items.Add(new TabItem { Header = Loc.T("About.TabLicense"), Content = ReadOnlyBox(ReadAppFile("LICENSE"), "AboutLicenseText") });
            tabs.Items.Add(new TabItem { Header = Loc.T("About.TabNotices"), Content = ReadOnlyBox(ReadAppFile("THIRD-PARTY-NOTICES.md"), "AboutNoticesText") });
            AutomationProperties.SetAutomationId(tabs, "AboutTabs");

            var copy = new Button { Content = Loc.T("About.Copy"), Width = 90, Margin = new Thickness(0, 0, 8, 0) };
            var close = new Button { Content = Loc.T("About.Close"), Width = 90, IsDefault = true, IsCancel = true };
            AutomationProperties.SetAutomationId(copy, "AboutCopyButton");
            AutomationProperties.SetAutomationId(close, "AboutCloseButton");
            AutomationProperties.SetAutomationId(_copyStatus, "AboutCopyStatus");
            copy.Click += (_, _) => CopyReport();
            close.Click += (_, _) => Close();

            var buttons = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
            var right = new StackPanel { Orientation = Orientation.Horizontal };
            right.Children.Add(close);
            DockPanel.SetDock(right, Dock.Right);
            var left = new StackPanel { Orientation = Orientation.Horizontal };
            left.Children.Add(copy);
            left.Children.Add(_copyStatus);
            buttons.Children.Add(right);
            buttons.Children.Add(left);

            var grid = new Grid { Margin = new Thickness(16) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Place(grid, headerRow, 0);
            Place(grid, reportBox, 1);
            Place(grid, trademark, 2);
            Place(grid, tabs, 3);
            Place(grid, buttons, 4);
            Content = grid;
        }

        static void Place(Grid grid, UIElement element, int row)
        {
            Grid.SetRow(element, row);
            grid.Children.Add(element);
        }

        /// <summary>埋め込んだ App.ico のうち、いちばん大きいフレーム (256x256)。読めなければ null (アイコンなしで見出しだけを出す)。</summary>
        static BitmapSource? LoadLargestIcon()
        {
            try
            {
                var decoder = BitmapDecoder.Create(new Uri("pack://application:,,,/Resources/App.ico"),
                    BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                return decoder.Frames.OrderByDescending(f => f.PixelWidth).FirstOrDefault();
            }
            catch (Exception ex)
            {
                AppLog.Warn($"バージョン情報のアイコンを読めません: {ex.Message}");
                return null;
            }
        }

        /// <summary>選択・コピーできる読み取り専用の枠 (長い文章はスクロールする)。</summary>
        static TextBox ReadOnlyBox(string text, string automationId)
        {
            var box = new TextBox
            {
                Text = text,
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                FontFamily = new System.Windows.Media.FontFamily("Consolas, MS Gothic"),
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(6),
            };
            AutomationProperties.SetAutomationId(box, automationId);
            return box;
        }

        /// <summary>実行ファイルと同じフォルダのファイルを読む (ビルドが LICENSE と THIRD-PARTY-NOTICES.md をコピーする)。</summary>
        static string ReadAppFile(string name)
        {
            string path = System.IO.Path.Combine(AppContext.BaseDirectory, name);
            try
            {
                return File.Exists(path) ? File.ReadAllText(path) : Loc.F("About.FileNotFound", name, path);
            }
            catch (Exception ex)
            {
                return Loc.F("About.FileReadError", name, ex.Message);
            }
        }

        void CopyReport()
        {
            try
            {
                Clipboard.SetText(_report);
                _copyStatus.Text = Loc.T("About.Copied");
            }
            catch (Exception ex)
            {
                // クリップボードを他のアプリが使っているときなど
                _copyStatus.Text = Loc.F("About.CopyFailed", ex.Message);
            }
        }
    }
}