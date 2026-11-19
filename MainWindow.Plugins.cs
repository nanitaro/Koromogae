using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using Koromogae.Audio;
using Koromogae.Ui;

namespace Koromogae
{
    /// <summary>VST プラグインのチェーン (追加・削除・並べ替え・エディタ・パラメータ画面)。</summary>
    public partial class MainWindow
    {
        readonly Dictionary<PluginSlot, ParametersWindow> _parameterWindows = new();

        async Task RestorePluginsAsync()
        {
            _unloaded.AddRange(_settings.Plugins);
            var failures = new List<string>();

            foreach (var plugin in _settings.Plugins.ToList())
            {
                string? error = await TryAddPluginAsync(plugin.Path, plugin.Enabled, plugin.State);
                _unloaded.Remove(plugin);
                if (error == null) continue;

                // 残すか消すかの判断と文面は PluginRestorePolicy。ここは結果を反映するだけ
                bool exists = System.IO.File.Exists(plugin.Path) || System.IO.Directory.Exists(plugin.Path);
                var decision = PluginRestorePolicy.OnFailure(plugin.Path, error, plugin.FailCount, exists);
                plugin.FailCount = decision.FailCount;
                failures.Add(decision.Message);
                if (decision.LogMessage != null) AppLog.Warn(decision.LogMessage);
                if (decision.Action == RestoreAction.Keep) _unloaded.Add(plugin);
            }

            ShowFailures(Loc.T("Plugins.Failed.LoadHeader"), failures);
        }

        async void AddPlugin_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = Loc.T("Dialog.PickPlugin.Title"),
                Filter = Loc.T("Dialog.PickPlugin.Filter"),
                Multiselect = true,
            };
            if (dialog.ShowDialog(this) != true) return;

            await AddPluginsAsync(dialog.FileNames);
        }

        async void AddBundle_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog { Title = Loc.T("Dialog.PickBundle.Title"), Multiselect = true };
            if (dialog.ShowDialog(this) != true) return;

            await AddPluginsAsync(dialog.FolderNames);
        }

        /// <summary>複数のプラグインを追加し、失敗はダイアログを連発せず最後にまとめて表示する。</summary>
        async Task AddPluginsAsync(IEnumerable<string> paths)
        {
            var failures = new List<string>();
            foreach (string path in paths)
            {
                string? error = CapacityError() ?? await TryAddPluginAsync(path);
                if (error != null) failures.Add($"{PluginRestorePolicy.DisplayName(path)}: {error}");
            }
            ShowFailures(Loc.T("Plugins.Failed.AddHeader"), failures);
        }

        string? CapacityError() => PluginRestorePolicy.CapacityError(_engine.Chain.Slots.Count, _unloaded.Count);

        void ShowFailures(string header, List<string> failures)
        {
            if (PluginRestorePolicy.FormatFailures(header, failures, AppLog.LogPath) is { } text)
                NotifyUser(text, MessageBoxImage.Warning);
        }

        /// <summary>プラグインをロードしてチェーンに追加する。成功したら null、失敗したらエラーメッセージを返す (ダイアログは出さない)。</summary>
        async Task<string?> TryAddPluginAsync(string path, bool enabled = true, string? stateBase64 = null)
        {
            int rate = SelectedRate;
            StatusText.Text = Loc.F("Status.LoadingPlugin", System.IO.Path.GetFileName(path));
            try
            {
                var effect = await Task.Run(() =>
                {
                    var fx = PluginLoader.Load(path, rate);
                    if (stateBase64 != null)
                    {
                        try { fx.SetState(Convert.FromBase64String(stateBase64)); }
                        catch (Exception ex) { AppLog.Warn($"{path} の状態を復元できませんでした: {ex.Message}"); }
                    }
                    return fx;
                });
                _engine.Chain.Add(new PluginSlot(effect) { Enabled = enabled });
                StatusText.Text = _engine.IsRunning ? _engine.Description : Loc.T("Play.Status.Stopped");
                return null;
            }
            catch (Exception ex)
            {
                AppLog.Error($"プラグインをロードできません: {path}", ex);
                StatusText.Text = Loc.T("Status.PluginLoadFailed");
                return ex.Message;
            }
        }

        void RemovePlugin_Click(object sender, RoutedEventArgs e)
        {
            if (PluginList.SelectedItem is PluginSlot slot)
            {
                if (_parameterWindows.TryGetValue(slot, out var window)) window.Close();
                _engine.Chain.Remove(slot);
            }
        }

        void MoveUp_Click(object sender, RoutedEventArgs e) => MoveSelected(-1);
        void MoveDown_Click(object sender, RoutedEventArgs e) => MoveSelected(1);

        void MoveSelected(int offset)
        {
            int index = PluginList.SelectedIndex;
            if (index < 0) return;
            _engine.Chain.Move(index, offset);
            PluginList.SelectedIndex = Math.Clamp(index + offset, 0, _engine.Chain.Slots.Count - 1);
        }

        void OpenEditor_Click(object sender, RoutedEventArgs e) => OpenSelectedEditor();

        void PluginList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is DependencyObject source && FindAncestor<CheckBox>(source) == null)
                OpenSelectedEditor();
        }

        static T? FindAncestor<T>(DependencyObject? element) where T : DependencyObject
        {
            while (element != null)
            {
                if (element is T match) return match;
                element = System.Windows.Media.VisualTreeHelper.GetParent(element);
            }
            return null;
        }

        void OpenSelectedEditor()
        {
            if (PluginList.SelectedItem is not PluginSlot slot) return;
            if (!slot.Effect.HasEditor)
            {
                // エディタを持たないプラグイン (Airwindows 等) は汎用パラメータ画面で操作する
                OpenParameters(slot);
                return;
            }
            slot.Effect.OpenEditor();
        }

        void OpenParameters_Click(object sender, RoutedEventArgs e)
        {
            if (PluginList.SelectedItem is PluginSlot slot) OpenParameters(slot);
        }

        void OpenParameters(PluginSlot slot)
        {
            if (_parameterWindows.TryGetValue(slot, out var existing))
            {
                existing.Activate();
                return;
            }

            var window = new ParametersWindow(slot.Effect) { Owner = this };
            window.Closed += (_, _) => _parameterWindows.Remove(slot);
            _parameterWindows[slot] = window;
            window.Show();
        }
    }
}
