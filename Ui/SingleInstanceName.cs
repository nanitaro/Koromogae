using System.Security.Cryptography;
using System.Text;

namespace Koromogae.Ui;

/// <summary>
/// 二重起動の判定に使う、Mutex とイベントの名前。<b>保存先 (設定ファイルとログのフォルダ) ごと</b>に名前を作る:
/// 問題は「同じ設定ファイルを 2 つが書き合うこと」なので、別の場所にコピーしたポータブル版どうしや、保存先を分けた自動テストの並列起動は妨げない。
/// このファイルは WPF に依存しない (単体テストでソースごとリンクしている)。
/// </summary>
public static class SingleInstanceName
{
    /// <summary>保存先のフォルダを、同じ場所なら同じ文字列になるよう整える (大文字小文字・末尾の区切り・/ と \ の違いを吸収)。</summary>
    public static string Normalize(string directory)
    {
        string path = directory.Trim().Replace('/', '\\').TrimEnd('\\');
        return path.ToUpperInvariant();
    }

    static string Hash(string directory)
    {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(directory)));
        return Convert.ToHexString(bytes, 0, 16);   // 先頭 16 バイト (32 文字) で十分
    }

    /// <summary>起動中かを示す Mutex の名前 (セッションごと。<c>Local\</c>)。</summary>
    public static string MutexName(string dataDirectory) => $@"Local\Koromogae.Instance.{Hash(dataDirectory)}";

    /// <summary>2 つ目のプロセスが、最初のプロセスに「前面に出して」と知らせるイベントの名前。</summary>
    public static string ActivateEventName(string dataDirectory) => $@"Local\Koromogae.Activate.{Hash(dataDirectory)}";
}
