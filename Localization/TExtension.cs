using System.Windows.Markup;

namespace Koromogae;

/// <summary>
/// XAML から文字列の表を引く: <c>Header="{local:T Menu.Help}"</c>。<see cref="Loc.T(string)"/> と同じ。
/// 言語は再起動で反映するので、画面を作るときに 1 回だけ解決すれば足りる。
/// </summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TExtension : MarkupExtension
{
    [ConstructorArgument("key")]
    public string Key { get; set; } = "";

    public TExtension() { }

    public TExtension(string key) => Key = key;

    public override object ProvideValue(IServiceProvider serviceProvider) => Loc.T(Key);
}
