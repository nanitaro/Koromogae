using System.Text;
using ManualBuilder;

// 使い方: ManualBuilder <README の Markdown> <出力の HTML> <ja|en>
// README と同じフォルダを、リポジトリの直下とみなして、docs/images/… の画像を読む。
if (args.Length != 3 || args[2] is not ("ja" or "en"))
{
    Console.Error.WriteLine("usage: ManualBuilder <readme.md> <output.html> <ja|en>");
    return 2;
}

string readme = Path.GetFullPath(args[0]);
string root = Path.GetDirectoryName(readme)!;
string output = Path.GetFullPath(args[1]);

string html = ManualHtml.Convert(
    File.ReadAllText(readme, Encoding.UTF8),
    args[2],
    relative =>
    {
        string path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    });

Directory.CreateDirectory(Path.GetDirectoryName(output)!);
File.WriteAllText(output, html, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));   // HTML は meta charset で伝えるので、BOM なし
Console.WriteLine($"{Path.GetFileName(output)}: {html.Length:N0} chars");
return 0;
