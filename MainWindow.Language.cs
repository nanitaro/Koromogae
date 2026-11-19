using System.Windows;
using System.Windows.Controls;

namespace Koromogae
{
    /// <summary>メニューバーの「Language」: 画面の言語を選ぶ。選んだ言語は再起動で反映する。</summary>
    public partial class MainWindow
    {
        /// <summary>どの言語の画面でも読めるよう、日本語と英語を併記した固定の文面 (翻訳表には入れない)。</summary>
        const string LanguageChangedPrompt =
            "言語を変更しました。再起動すると反映されます。今すぐ再起動しますか?\n" +   // i18n:fixed
            "Language changed. It takes effect after a restart. Restart now?";

        /// <summary>選んでいる言語の項目にチェックを付ける。</summary>
        void UpdateLanguageChecks()
        {
            LanguageAutoItem.IsChecked = _language == UiLanguage.Auto;
            LanguageJapaneseItem.IsChecked = _language == UiLanguage.Japanese;
            LanguageEnglishItem.IsChecked = _language == UiLanguage.English;
        }

        void LanguageItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem { Tag: string tag } || !Enum.TryParse<UiLanguage>(tag, out var language)) return;
            if (language == _language)
            {
                UpdateLanguageChecks();   // クリックでチェックが外れないように
                return;
            }

            _language = language;
            UpdateLanguageChecks();
            CaptureSettings().Save();   // すぐ保存する (再起動しなくても、次の起動で反映される)。失敗は AppLog に記録される
            AppLog.Info($"言語の設定を変更しました: {language}");

            var answer = MessageBox.Show(this, LanguageChangedPrompt, Title, MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) return;

            App.RequestRestart();
            Close();
        }
    }
}
