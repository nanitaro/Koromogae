using System.ComponentModel;
using System.Windows;
using VstCore;

namespace Koromogae
{
    /// <summary>プラグイン固有のエディタを持たない(または使えない)場合の、汎用パラメータ操作画面。</summary>
    public partial class ParametersWindow : Window
    {
        readonly IVstEffect _effect;

        public ParametersWindow(IVstEffect effect)
        {
            InitializeComponent();
            _effect = effect;
            Title = Loc.F("Params.TitleFormat", effect.Name);
            Loaded += async (_, _) => await ReloadAsync();
        }

        bool _loadingPrograms;

        async void ProgramCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_loadingPrograms || ProgramCombo.SelectedIndex < 0) return;
            int index = ProgramCombo.SelectedIndex;
            await Task.Run(() => _effect.SetProgram(index));
            await ReloadAsync(refreshPrograms: false); // プリセットでパラメータ値が変わるので読み直す
        }

        async void Reload_Click(object sender, RoutedEventArgs e) => await ReloadAsync();

        async Task ReloadAsync(bool refreshPrograms = true)
        {
            ReloadButton.IsEnabled = false;
            InfoText.Text = Loc.T("Params.Loading");
            try
            {
                if (refreshPrograms)
                {
                    var programs = await Task.Run(() => _effect.GetPrograms());
                    _loadingPrograms = true;
                    ProgramCombo.ItemsSource = programs.Names;
                    ProgramCombo.SelectedIndex = programs.Names.Length > 0 ? programs.Current : -1;
                    _loadingPrograms = false;
                    ProgramPanel.Visibility = programs.Names.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
                }

                var items = await Task.Run(() =>
                {
                    var list = new List<ParameterItem>();
                    foreach (var info in _effect.GetParameters())
                    {
                        var value = _effect.GetParameterValue(info.Index);
                        list.Add(new ParameterItem(_effect, info, value));
                    }
                    return list;
                });
                ParameterList.ItemsSource = items;
                InfoText.Text = items.Count == 0 ? Loc.T("Params.None") : Loc.F("Params.Count", items.Count);
            }
            catch (Exception ex)
            {
                InfoText.Text = Loc.F("Params.Error", ex.Message);
            }
            finally
            {
                ReloadButton.IsEnabled = true;
            }
        }
    }

    public sealed class ParameterItem : INotifyPropertyChanged
    {
        readonly IVstEffect _effect;
        readonly ParameterInfo _info;
        double _value;
        string _display;

        public ParameterItem(IVstEffect effect, ParameterInfo info, ParameterValue initial)
        {
            _effect = effect;
            _info = info;
            _value = initial.Normalized;
            _display = initial.Display;
        }

        public string Name => string.IsNullOrEmpty(_info.Unit) ? _info.Name : $"{_info.Name} ({_info.Unit})";

        public double Value
        {
            get => _value;
            set
            {
                if (_info.StepCount > 0) value = Math.Round(value * _info.StepCount) / _info.StepCount;
                if (value == _value) return;
                _value = value;
                _effect.SetParameter(_info.Index, value);
                // プラグインが丸めた値・表示文字列を反映する
                var actual = _effect.GetParameterValue(_info.Index);
                _display = actual.Display;
                Raise(nameof(Value));
                Raise(nameof(Display));
            }
        }

        public string Display => _display;

        public event PropertyChangedEventHandler? PropertyChanged;
        void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
